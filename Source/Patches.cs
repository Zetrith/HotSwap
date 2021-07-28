using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
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
        public static string Tooltip = "Hot swap.";

        static FieldInfo WidgetRow = AccessTools.Field(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.widgetRow));
        static MethodInfo DrawMethod = AccessTools.Method(typeof(DebugButtonsPatch), nameof(Draw));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            var list = new List<CodeInstruction>(insts);

            var labels = list.Last().labels;
            list.RemoveLast();

            list.Add(new CodeInstruction(OpCodes.Ldarg_0) { labels = labels });
            list.Add(new CodeInstruction(OpCodes.Ldfld, WidgetRow));
            list.Add(new CodeInstruction(OpCodes.Call, DrawMethod));
            list.Add(new CodeInstruction(OpCodes.Ret));

            return list;
        }

        static MethodInfo WidgetRowButtonIcon = AccessTools.Method(typeof(WidgetRow), nameof(Draw));

        static void Draw(WidgetRow row)
        {
            if (WidgetRow_ButtonIcon(row, TexButton.Paste, Tooltip))
                HotSwapMain.DoHotSwap();
        }

        // WidgetRow.ButtonIcon as a custom method for cross-version compatibility
        static bool WidgetRow_ButtonIcon(WidgetRow row, Texture2D tex, string tooltip)
        {
            const float width = 24f;
            row.IncrementYIfWillExceedMaxWidth(width);

            var rect = new Rect(row.LeftX(width), row.curY, width, width);
            var result = Widgets.ButtonImage(rect, tex, Color.white, GenUI.MouseoverColor, true);

            row.IncrementPosition(width);

            if (!tooltip.NullOrEmpty())
                TooltipHandler.TipRegion(rect, tooltip);

            return result;
        }
    }

}
