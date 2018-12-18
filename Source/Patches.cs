using Harmony;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using Verse;

namespace HotSwap
{
    [HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DevToolStarterOnGUI))]
    static class AddDebugButtonPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            bool found = false;

            foreach (CodeInstruction inst in insts)
            {
                if (!found && inst.opcode == OpCodes.Stloc_1)
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4_1);
                    yield return new CodeInstruction(OpCodes.Add);
                    found = true;
                }

                yield return inst;
            }
        }
    }

    [HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DrawButtons))]
    static class DebugButtonsPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            var list = new List<CodeInstruction>(insts);

            var labels = list.Last().labels;
            list.RemoveLast();

            list.Add(new CodeInstruction(OpCodes.Ldloc_0) { labels = labels });
            list.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DebugButtonsPatch), nameof(Draw))));
            list.Add(new CodeInstruction(OpCodes.Ret));

            return list;
        }

        static void Draw(WidgetRow row)
        {
            if (row.ButtonIcon(TexButton.Paste, "Hot swap."))
                HotSwapMain.DoHotSwap();
        }
    }

}
