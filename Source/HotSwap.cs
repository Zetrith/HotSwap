using dnlib.DotNet;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace HotSwap
{
    class HotSwapMain : Mod
    {
        public static Dictionary<Assembly, FileInfo> AssemblyFiles;
        static Harmony harmony = new("HotSwap");
        static DateTime startTime = DateTime.Now;

        public HotSwapMain(ModContentPack content) : base(content)
        {
            harmony.PatchAll();
            AssemblyFiles = MapModAssemblies();
        }

        static Dictionary<Assembly, FileInfo> MapModAssemblies()
        {
            var dict = new Dictionary<Assembly, FileInfo>();

            IEnumerable<FileInfo> ModDlls(ModContentPack mod, string folder)
            {
                return ModContentPack
                    .GetAllFilesForModPreserveOrder(mod, folder, e => e.ToLower() == ".dll")
                    .Select(t => t.Item2);
            }

            foreach (var mod in LoadedModManager.RunningMods)
            {
                // Multiplayer uses "AssembliesCustom/"
                foreach (var fileInfo in ModDlls(mod, "Assemblies/").Concat(ModDlls(mod, "AssembliesCustom/")))
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
            if (MessageTypeDefOf.SilentInput != null)
                Messages.Message("Hotswapping...", MessageTypeDefOf.SilentInput);
        }

        public static void DoHotSwap()
        {
            Info("Hotswapping...");

            var watches = new Dictionary<string, Stopwatch>();

            Stopwatch Watch(string name)
            {
                if (watches.TryGetValue(name, out var watch))
                    return watch;
                return watches[name] = new Stopwatch();
            }

            var updatedTypes = new HashSet<Type>();

            Watch("Top").Start();

            foreach (var kv in AssemblyFiles)
            {
                kv.value.Refresh();
                if (kv.Value.LastWriteTime < startTime)
                    continue;

                using var dnModule = ModuleDefMD.Load(kv.Value.FullName);
                dnModule.Assembly.Version = new Version(4, 0, 0, 0);

                foreach (var dnTypeTop in dnModule.GetTypes())
                {
                    if (!dnTypeTop.HasCustomAttributes) continue;
                    if (!dnTypeTop.CustomAttributes.Select(a => a.AttributeType.Name)
                            .Any(n => n == AttrName1 || n == AttrName2)) continue;

                    const BindingFlags allDeclared = BindingFlags.DeclaredOnly | BindingFlags.NonPublic |
                                                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

                    var typeWithAttr = Type.GetType(dnTypeTop.AssemblyQualifiedName);
                    var types = typeWithAttr.GetNestedTypes(allDeclared).Where(IsCompilerGenerated)
                        .Concat(typeWithAttr);
                    var typesKv = types
                        .Select(t => new KeyValuePair<Type, TypeDef>(t, dnModule.FindReflection(t.FullName)))
                        .Where(t => t.Key != null && t.Value != null);

                    foreach (var (systemType, dnType) in typesKv)
                    {
                        if (systemType.IsGenericTypeDefinition)
                            continue;

                        var anyUpdates = false;

                        foreach (var method in systemType.GetMethods(allDeclared)
                                     .Concat(systemType.GetConstructors(allDeclared).Cast<MethodBase>()))
                        {
                            if (method.GetMethodBody() == null) continue;
                            if (method.IsGenericMethodDefinition) continue;

                            Watch("Sigs").Start();
                            byte[] code = method.GetMethodBody().GetILAsByteArray();
                            var dnMethod = dnType.Methods.FirstOrDefault(m => Translator.MethodSigMatch(method, m));
                            Watch("Sigs").Stop();

                            if (dnMethod == null) continue;

                            Watch("Ser").Start();
                            var methodBody = dnMethod.Body;
                            byte[] newCode = MethodSerializer.SerializeInstructions(methodBody);
                            Watch("Ser").Stop();

                            if (ByteArrayCompare(code, newCode)) continue;

                            try
                            {
                                Watch("Dyn").Start();
                                var replacement = OldHarmony.CreateDynamicMethod(method, $"_HotSwap{count++}");
                                var ilGen = replacement.GetILGenerator();
                                Watch("Dyn").Stop();

                                Watch("Tra").Start();
                                MethodTranslator.TranslateLocals(methodBody, ilGen);
                                MethodTranslator.TranslateRefs(methodBody, newCode, replacement, Watch);
                                Watch("Tra").Stop();

                                ilGen.code = newCode;
                                ilGen.code_len = newCode.Length;
                                ilGen.max_stack = methodBody.MaxStack;

                                Watch("Detour").Start();
                                MethodTranslator.TranslateExceptions(methodBody, ilGen);
                                OldHarmony.PrepareDynamicMethod(replacement);
                                Memory.DetourMethod(method, replacement);
                                Watch("Detour").Stop();

                                dynMethods[method] = replacement;
                                updatedTypes.Add(systemType);
                            }
                            catch (Exception e)
                            {
                                Error($"Patching {method.FullDescription()} failed with {e}");
                            }
                        }
                    }
                }
            }

            // Watch("Harm").Start();
            // UpdateHarmonyPatches(updatedTypes);
            // Watch("Harm").Stop();

            Watch("Top").Stop();

            Info($"Hotswapping done... {watches.Join(kv => $"{kv.Key}:{kv.Value.ElapsedMilliseconds}ms")}");
        }

        private static void UpdateHarmonyPatches(HashSet<Type> updatedTypes)
        {
            var unpatchedTypes = new HashSet<(string, Type)>();

            foreach (var (original, patch) in from m in Harmony.GetAllPatchedMethods()
                     let info = Harmony.GetPatchInfo(m)
                     from p in info.Prefixes.Concat(info.Postfixes)
                         .Concat(info.Transpilers).Concat(info.Finalizers)
                     select (m, p))
            {
                if (updatedTypes.Contains(patch.PatchMethod.DeclaringType) && patch.PatchMethod.DeclaringType.HasAttribute<HarmonyPatch>())
                {
                    harmony.Unpatch(original, patch.PatchMethod);
                    unpatchedTypes.Add((patch.owner, patch.PatchMethod.DeclaringType));
                }
            }

            foreach (var (owner, type) in unpatchedTypes)
                new Harmony(owner).CreateClassProcessor(type).Patch();
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
