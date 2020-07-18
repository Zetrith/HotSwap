using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace HotSwap
{
    [HotSwappable]
    [StaticConstructorOnStartup]
    static class HotSwapMain
    {
        public static Dictionary<Assembly, FileInfo> AssemblyFiles;
        static Harmony harmony = new Harmony("HotSwap");

        static HotSwapMain()
        {
            harmony.PatchAll();
            AssemblyFiles = MapModAssemblies();
        }

        static Dictionary<Assembly, FileInfo> MapModAssemblies()
        {
            var dict = new Dictionary<Assembly, FileInfo>();
            
            foreach (var mod in LoadedModManager.RunningMods)
            {
                foreach (FileInfo fileInfo in ModContentPack.GetAllFilesForModPreserveOrder(mod, "Assemblies/", (string e) => e.ToLower() == ".dll", null).Select(t => t.Item2))
                {
                    var fileAsmName = AssemblyName.GetAssemblyName(fileInfo.FullName).FullName;
                    var search = mod.assemblies.loadedAssemblies.Find(a => a.GetName().FullName == fileAsmName);
                    if (search != null && !dict.ContainsKey(search))
                    {
                        dict[search] = fileInfo;
                        Log.Message($"HotSwap mapped {fileInfo} to {search.GetName()}");
                    }
                }
            }

            return dict;
        }

        private static Dictionary<MethodBase, DynamicMethod> dynMethods = new Dictionary<MethodBase, DynamicMethod>();
        private static int count;

        const string AttrName1 = "HotSwappable";
        const string AttrName2 = "HotSwappableAttribute";

        public static void DoHotSwap()
        {
            foreach (var kv in AssemblyFiles)
            {
                var asm = kv.Key;
                using var dnModule = ModuleDefMD.Load(kv.Value.FullName);

                foreach (var dnType in dnModule.GetTypes())
                {
                    if (!dnType.HasCustomAttributes) continue;
                    if (!dnType.CustomAttributes.Select(a => a.AttributeType.Name).Any(n => n == AttrName1 || n == AttrName2)) continue;

                    var flags = BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

                    var typeWithAttr = Type.GetType(dnType.AssemblyQualifiedName);
                    var types = typeWithAttr.GetNestedTypes(flags).Where(t => IsCompilerGenerated(t)).Concat(typeWithAttr);
                    var typesKv = types.Select(t => new KeyValuePair<Type, TypeDef>(t, dnModule.FindReflection(t.FullName))).Where(t => t.Key != null && t.Value != null);

                    foreach (var typePair in typesKv)
                    {
                        if (typePair.Key.IsGenericTypeDefinition) continue;

                        foreach (var method in typePair.Key.GetMethods(flags))
                        {
                            if (method.GetMethodBody() == null) continue;
                            if (method.IsGenericMethodDefinition) continue;

                            byte[] code = method.GetMethodBody().GetILAsByteArray();
                            var dnMethod = typePair.Value.Methods.FirstOrDefault(m => Translator.MethodSigMatch(method, m));
                            if (dnMethod == null) continue;

                            var methodBody = dnMethod.Body;
                            byte[] newCode = SerializeInstructions(methodBody);

                            if (ByteArrayCompare(code, newCode)) continue;

                            Log.Message($"Patching {method.FullDescription()}");

                            try
                            {
                                var replacement = CreateDynamicMethod(method, $"_HotSwap{count++}");
                                var ilGen = replacement.GetILGenerator();

                                TranslateLocals(methodBody, ilGen);
                                TranslateRefs(methodBody, newCode, replacement);

                                ilGen.code = newCode;
                                ilGen.code_len = newCode.Length;
                                ilGen.max_stack = methodBody.MaxStack;

                                TranslateExceptions(methodBody, ilGen);

                                Log.Message("Preparing method");

                                PrepareDynamicMethod(replacement);

                                Log.Message("Detouring");

                                dynMethods[method] = replacement;

                                Log.Message($"Patch done {Memory.DetourMethod(method, replacement)}");
                            }
                            catch (Exception e)
                            {
                                Log.Error($"Patching failed with {e}");
                            }
                        }
                    }
                }
            }
        }

        public static bool IsCompilerGenerated(Type type)
        {
            while (type != null)
            {
                if (type.HasAttribute<CompilerGeneratedAttribute>()) return true;
                type = type.DeclaringType;
            }

            return false;
        }

        static bool ByteArrayCompare(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }

        private static void TranslateLocals(CilBody methodBody, ILGenerator ilGen)
        {
            foreach (var local in methodBody.Variables)
            {
                ilGen.DeclareLocal((Type)Translator.TranslateRef(local.Type));
            }
        }

        private static void TranslateRefs(CilBody methodBody, byte[] newCode, DynamicMethod replacement)
        {
            int pos = 0;

            foreach (var inst in methodBody.Instructions)
            {
                switch (inst.OpCode.OperandType)
                {
                    case dnlib.DotNet.Emit.OperandType.InlineString:
                    case dnlib.DotNet.Emit.OperandType.InlineType:
                    case dnlib.DotNet.Emit.OperandType.InlineMethod:
                    case dnlib.DotNet.Emit.OperandType.InlineField:
                    case dnlib.DotNet.Emit.OperandType.InlineSig:
                    case dnlib.DotNet.Emit.OperandType.InlineTok:
                        pos += inst.OpCode.Size;
                        object refe = Translator.TranslateRef(inst.Operand);
                        if (refe == null)
                            throw new NullReferenceException($"Null translation {inst.Operand} {inst.Operand.GetType()}");

                        int token = replacement.AddRef(refe);
                        newCode[pos++] = (byte)(token & 255);
                        newCode[pos++] = (byte)(token >> 8 & 255);
                        newCode[pos++] = (byte)(token >> 16 & 255);
                        newCode[pos++] = (byte)(token >> 24 & 255);

                        break;
                    default:
                        pos += inst.GetSize();
                        break;
                }
            }
        }
        
        private static void TranslateExceptions(CilBody methodBody, ILGenerator ilGen)
        {
            var dnhandlers = methodBody.ExceptionHandlers.Reverse().ToArray();
            var exinfos = ilGen.ex_handlers = new ILExceptionInfo[dnhandlers.Length];

            for (int i = 0; i < dnhandlers.Length; i++)
            {
                var ex = dnhandlers[i];
                int start = (int)ex.TryStart.Offset;
                int end = (int)ex.TryEnd.Offset;
                int len = end - start;

                exinfos[i].start = start;
                exinfos[i].len = len;

                int handlerStart = (int)ex.HandlerStart.Offset;
                int handlerEnd = (int)ex.HandlerEnd.Offset;
                int handlerLen = handlerEnd - handlerStart;

                Type catchType = null;
                int filterOffset = 0;

                if (ex.CatchType != null)
                    catchType = (Type)Translator.TranslateRef(ex.CatchType);
                else if (ex.FilterStart != null)
                    filterOffset = (int)ex.FilterStart.Offset;

                exinfos[i].handlers = new ILExceptionBlock[]
                {
                    new ILExceptionBlock()
                    {
                        extype = catchType,
                        type = (int)ex.HandlerType,
                        start = handlerStart,
                        len = handlerLen,
                        filter_offset = filterOffset
                    }
                };
            }
        }

        public class TokenProvider : ITokenProvider
        {
            public void Error(string message)
            {
            }

            public MDToken GetToken(object o)
            {
                if (o is string)
                    return new MDToken((Table)0x70, 1);
                else if (o is IMDTokenProvider token)
                    return token.MDToken;
                else if (o is StandAloneSig sig)
                    return sig.MDToken;

                return new MDToken();
            }

            public MDToken GetToken(IList<TypeSig> locals, uint origToken)
            {
                return new MDToken(origToken);
            }
        }

        static FieldInfo codeSizeField = AccessTools.Field(typeof(MethodBodyWriter), "codeSize");

        private static byte[] SerializeInstructions(CilBody body)
        {
            var writer = new MethodBodyWriter(new TokenProvider(), body);
            writer.Write();
            int codeSize = (int)(uint)codeSizeField.GetValue(writer);
            byte[] newCode = new byte[codeSize];
            Array.Copy(writer.Code, writer.Code.Length - codeSize, newCode, 0, codeSize);
            return newCode;
        }

        /* Methods below are copied from old Harmony */

        public static DynamicMethod CreateDynamicMethod(MethodBase original, string suffix)
        {
            if (original == null) throw new ArgumentNullException("original cannot be null");
            var patchName = original.Name + suffix;
            patchName = patchName.Replace("<>", "");

            var parameters = original.GetParameters();
            var result = parameters.Types().ToList();
            if (original.IsStatic == false)
                result.Insert(0, typeof(object));
            var paramTypes = result.ToArray();
            var returnType = AccessTools.GetReturnedType(original);

            // DynamicMethod does not support byref return types
            if (returnType == null || returnType.IsByRef)
                return null;

            DynamicMethod method;
            try
            {
                method = new DynamicMethod(
                patchName,
                System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
                CallingConventions.Standard,
                returnType,
                paramTypes,
                original.DeclaringType,
                true
            );
            }
            catch (Exception)
            {
                return null;
            }

            for (var i = 0; i < parameters.Length; i++)
                method.DefineParameter(i + 1, parameters[i].Attributes, parameters[i].Name);

            return method;
        }

        public static void PrepareDynamicMethod(DynamicMethod method)
        {
            var nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var nonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

            // on mono, just call 'CreateDynMethod'
            //
            var m_CreateDynMethod = typeof(DynamicMethod).GetMethod("CreateDynMethod", nonPublicInstance);
            if (m_CreateDynMethod != null)
            {
                m_CreateDynMethod.Invoke(method, new object[0]);
                return;
            }

            // on all .NET Core versions, call 'RuntimeHelpers._CompileMethod' but with a different parameter:
            //
            var m__CompileMethod = typeof(RuntimeHelpers).GetMethod("_CompileMethod", nonPublicStatic);

            var m_GetMethodDescriptor = typeof(DynamicMethod).GetMethod("GetMethodDescriptor", nonPublicInstance);
            var handle = (RuntimeMethodHandle)m_GetMethodDescriptor.Invoke(method, new object[0]);

            // 1) RuntimeHelpers._CompileMethod(handle.GetMethodInfo())
            //
            var m_GetMethodInfo = typeof(RuntimeMethodHandle).GetMethod("GetMethodInfo", nonPublicInstance);
            if (m_GetMethodInfo != null)
            {
                var runtimeMethodInfo = m_GetMethodInfo.Invoke(handle, new object[0]);
                try
                {
                    // this can throw BadImageFormatException "An attempt was made to load a program with an incorrect format"
                    m__CompileMethod.Invoke(null, new object[] { runtimeMethodInfo });
                    return;
                }
                catch (Exception)
                {
                }
            }

            // 2) RuntimeHelpers._CompileMethod(handle.Value)
            //
            if (m__CompileMethod.GetParameters()[0].ParameterType.IsAssignableFrom(handle.Value.GetType()))
            {
                m__CompileMethod.Invoke(null, new object[] { handle.Value });
                return;
            }

            // 3) RuntimeHelpers._CompileMethod(handle)
            //
            if (m__CompileMethod.GetParameters()[0].ParameterType.IsAssignableFrom(handle.GetType()))
            {
                m__CompileMethod.Invoke(null, new object[] { handle });
                return;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class HotSwappableAttribute : Attribute
    {
    }

}
