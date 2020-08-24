using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using dnlib.DotNet;
using Verse;

namespace HotSwap
{
    static class Translator
    {
        static BindingFlags all = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.GetField
            | BindingFlags.SetField
            | BindingFlags.GetProperty
            | BindingFlags.SetProperty;

        static Assembly markerAsm;

        public static object TranslateRef(object dnRef)
        {
            if (dnRef is string)
                return dnRef;

            if (dnRef is TypeSig sig)
                return TranslateTypeSig(sig);

            if (!(dnRef is IMemberRef member))
                return null;

            if (member.IsField)
            {
                Type declType = (Type)TranslateRef(member.DeclaringType);
                return declType.GetField(member.Name, all);
            }
            else if (member.IsMethod && member is IMethod method)
            {
                Type declType = (Type)TranslateRef(member.DeclaringType);
                var origMembers = declType.GetMembers(all);

                Type[] genericArgs = null;
                if (method.IsMethodSpec && method is MethodSpec spec)
                {
                    method = spec.Method;
                    var generic = spec.GenericInstMethodSig;
                    genericArgs = generic.GenericArguments.Select(t => (Type)TranslateRef(t)).ToArray();
                }

                var openType = declType.IsGenericType ? declType.GetGenericTypeDefinition() : declType;
                var members = openType.GetMembers(all);

                for (int i = 0; i < members.Length; i++)
                {
                    var typeMember = members[i];
                    if (!(typeMember is MethodBase m)) continue;

                    if (!MethodSigMatch(m, method)) continue;

                    if (genericArgs != null)
                        return (origMembers[i] as MethodInfo).MakeGenericMethod(genericArgs);

                    return origMembers[i];
                }

                return null;
            }
            else if (member.IsType && member is IType type)
            {
                if (type is dnlib.DotNet.TypeSpec spec)
                    return TranslateRef(spec.TypeSig);

                return Type.GetType(type.AssemblyQualifiedName);
            }

            return null;
        }

        static Type TranslateTypeSig(TypeSig sig)
        {
            if (markerAsm == null)
                markerAsm = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("hotswapmarkerassembly"), AssemblyBuilderAccess.ReflectionOnly);

            var flatGenerics = new Queue<TypeSig>();
            CollectGenerics(sig, flatGenerics);

            Assembly AsmResolver(AssemblyName aname)
            {
                if (aname.Name == "<<<NULL>>>")
                    return markerAsm;

                return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(asm => asm.GetName().Name == aname.Name);
            }

            Type TypeResolver(Assembly asm, string s, bool flag)
            {
                var generic = flatGenerics.Dequeue();

                if (asm != markerAsm)
                    return asm.GetType(s);

                if (generic is GenericSig g)
                {
                    Type translated = null;

                    if (g.HasOwnerMethod)
                        translated = (TranslateRef(g.OwnerMethod) as MethodInfo).GetGenericArguments()[g.Number];
                    else if (g.HasOwnerType)
                        translated = (TranslateRef(g.OwnerType) as Type).GetGenericArguments()[g.Number];

                    return translated;
                }

                return null;
            }

            return Type.GetType(sig.AssemblyQualifiedName, AsmResolver, TypeResolver, false);
        }

        public static bool MethodSigMatch(MethodBase m, IMethod method)
        {
            return new SigComparer().Equals(method.Module.Import(m), method);
        }

        static void CollectGenerics(TypeSig sig, Queue<TypeSig> flatGenerics)
        {
            while (sig.Next != null)
                sig = sig.Next;

            flatGenerics.Enqueue(sig);

            if (sig is GenericInstSig generic)
                foreach (var arg in generic.GenericArguments)
                    CollectGenerics(arg, flatGenerics);
        }
    }
}
