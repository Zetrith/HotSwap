using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using dnlib.DotNet;
using Verse;

namespace HotSwap
{
    [HotSwappable]
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

        private static Dictionary<string, Type> typeCache = new();
        private static Dictionary<string, Type> typeSigCache = new();
        private static Dictionary<Type, MemberInfo[]> memberCache = new();
        private static Dictionary<MethodBase, ParameterInfo[]> paramsCache = new();

        private static MemberInfo[] GetMembers(Type t)
        {
            if (!memberCache.TryGetValue(t, out var cached))
                return memberCache[t] = t.GetMembers(all);
            return cached;
        }

        private static ParameterInfo[] GetParams(MethodBase m)
        {
            if (!paramsCache.TryGetValue(m, out var cached))
                return paramsCache[m] = m.GetParameters();
            return cached;
        }

        public static object TranslateRef(object dnRef)
        {
            if (dnRef is string)
                return dnRef;

            if (dnRef is TypeSig sig)
                return TranslateTypeSig(sig);

            if (dnRef is not IMemberRef member)
                return null;

            if (member.IsField)
            {
                Type declType = (Type)TranslateRef(member.DeclaringType);
                var field = declType.GetField(member.Name, all);
                return field;
            }

            if (member.IsMethod && member is IMethod method)
            {
                Type declType = (Type)TranslateRef(member.DeclaringType);
                var origMembers = GetMembers(declType);

                Type[] genericArgs = null;
                if (method.IsMethodSpec && method is MethodSpec spec)
                {
                    method = spec.Method;
                    var generic = spec.GenericInstMethodSig;
                    genericArgs = generic.GenericArguments.Select(t => (Type)TranslateRef(t)).ToArray();
                }

                var openType = declType.IsGenericType ? declType.GetGenericTypeDefinition() : declType;
                var members = GetMembers(openType);
                MemberInfo ret = null;

                if (genericArgs == null)
                {
                    // If a method is unambiguous by name, param count and first param type, return it
                    // This is a very good heuristic
                    foreach (var m in origMembers.OfType<MethodBase>())
                    {
                        if (m.Name != method.Name) continue;
                        if (GetParams(m).Length != method.GetParamCount()) continue;
                        if (GetParams(m).Length > 0 &&
                            GetParams(m)[0].ParameterType != TranslateRef(method.GetParam(0))) continue;

                        if (ret == null)
                            ret = m;
                        else
                        {
                            ret = null;
                            break;
                        }
                    }
                }

                if (ret != null)
                {
                    return ret;
                }

                for (int i = 0; i < members.Length; i++)
                {
                    var typeMember = members[i];
                    if (typeMember is not MethodBase m) continue;
                    if (!MethodSigMatch(m, method)) continue;

                    if (genericArgs != null)
                        return (origMembers[i] as MethodInfo).MakeGenericMethod(genericArgs);

                    return origMembers[i];
                }

                return null;
            }

            if (member.IsType && member is IType type)
            {
                if (type is dnlib.DotNet.TypeSpec spec)
                    return TranslateRef(spec.TypeSig);

                var aqn = type.AssemblyQualifiedName;
                if (!typeCache.TryGetValue(aqn, out var cached))
                    return typeCache[aqn] = Type.GetType(aqn);

                return cached;
            }

            return null;
        }

        static Type TranslateTypeSig(TypeSig sig)
        {
            var aqn = sig.AssemblyQualifiedName;
            if (typeSigCache.TryGetValue(aqn, out var cached))
                return cached;

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

            var result = Type.GetType(aqn, AsmResolver, TypeResolver, false);
            typeSigCache[aqn] = result;
            return result;
        }

        public static bool MethodSigMatch(MethodBase m, IMethod method)
        {
            return m.Name == method.Name && new SigComparer().Equals(method.Module.Import(m), method);
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
