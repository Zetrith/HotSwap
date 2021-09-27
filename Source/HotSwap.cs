using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace HotSwap
{
    [HotSwappable]
    [StaticConstructorOnStartup]
    static class HotSwapMain
    {
        public static Dictionary<Assembly, FileInfo> AssemblyFiles;
        static Harmony harmony = new("HotSwap");

        public static KeyBindingDef HotSwapKey = KeyBindingDef.Named("HotSwapKey");

        static DateTime startTime = DateTime.Now;

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
                        Info($"HotSwap mapped {fileInfo} to {search.GetName()}");
                    }
                }
            }

            return dict;
        }

        // A cache used so the methods don't get GC'd
        private static Dictionary<MethodBase, DynamicMethod> dynMethods = new();
        private static int count;

        const string AttrName1 = "HotSwappable";
        const string AttrName2 = "HotSwappableAttribute";

        public static int runInFrames;

        public static void ScheduleHotSwap()
        {
            runInFrames = 2;
            Messages.Message("Hotswapping...", MessageTypeDefOf.SilentInput);
        }

        public static void DoHotSwap()
        {
            Info("Hotswapping...");

            var watches = new[]
            {
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
                new Stopwatch(),
            };

            watches[5].Start();

            foreach (var kv in AssemblyFiles)
            {
                kv.value.Refresh();
                if (kv.Value.LastWriteTime < startTime)
                    continue;

                using var dnModule = ModuleDefMD.Load(kv.Value.FullName);

                foreach (var dnType in dnModule.GetTypes())
                {
                    if (!dnType.HasCustomAttributes) continue;
                    if (!dnType.CustomAttributes.Select(a => a.AttributeType.Name).Any(n => n == AttrName1 || n == AttrName2)) continue;

                    const BindingFlags allDeclared = BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

                    var typeWithAttr = Type.GetType(dnType.AssemblyQualifiedName);
                    var types = typeWithAttr.GetNestedTypes(allDeclared).Where(IsCompilerGenerated).Concat(typeWithAttr);
                    var typesKv = types.Select(t => new KeyValuePair<Type, TypeDef>(t, dnModule.FindReflection(t.FullName))).Where(t => t.Key != null && t.Value != null);

                    foreach (var typePair in typesKv)
                    {
                        if (typePair.Key.IsGenericTypeDefinition)
                            continue;

                        foreach (var method in typePair.Key.GetMethods(allDeclared).Concat(typePair.key.GetConstructors(allDeclared).Cast<MethodBase>()))
                        {
                            if (method.GetMethodBody() == null) continue;
                            if (method.IsGenericMethodDefinition) continue;

                            watches[0].Start();
                            byte[] code = method.GetMethodBody().GetILAsByteArray();
                            var dnMethod = typePair.Value.Methods.FirstOrDefault(m => Translator.MethodSigMatch(method, m));
                            watches[0].Stop();

                            if (dnMethod == null) continue;

                            watches[1].Start();
                            var methodBody = dnMethod.Body;
                            byte[] newCode = MethodSerializer.SerializeInstructions(methodBody);
                            watches[1].Stop();

                            if (ByteArrayCompare(code, newCode)) continue;

                            try
                            {
                                watches[2].Start();
                                var replacement = OldHarmony.CreateDynamicMethod(method, $"_HotSwap{count++}");
                                var ilGen = replacement.GetILGenerator();
                                watches[2].Stop();

                                watches[3].Start();
                                MethodTranslator.TranslateLocals(methodBody, ilGen);
                                MethodTranslator.TranslateRefs(methodBody, newCode, replacement, watches);
                                watches[3].Stop();

                                ilGen.code = newCode;
                                ilGen.code_len = newCode.Length;
                                ilGen.max_stack = methodBody.MaxStack;

                                watches[4].Start();
                                MethodTranslator.TranslateExceptions(methodBody, ilGen);
                                OldHarmony.PrepareDynamicMethod(replacement);
                                Memory.DetourMethod(method, replacement);
                                watches[4].Stop();

                                dynMethods[method] = replacement;
                            }
                            catch (Exception e)
                            {
                                Error($"Patching {method.FullDescription()} failed with {e}");
                            }
                        }
                    }
                }
            }
            watches[5].Stop();

            Info($"Hotswapping done... {watches.Join(w => w.ElapsedMilliseconds.ToString())}");
        }

        // Obsolete signatures, used for cross-version compat
        static void Info(string str) => Log.Message(str, false);
        static void Error(string str) => Log.Error(str, false);

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
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class HotSwappableAttribute : Attribute
    {
    }

}
