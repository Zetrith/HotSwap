using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;

namespace HotSwap
{
    [HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DevToolStarterOnGUI))]
    static class AddDebugButtonPatch
    {
        static void Prefix()
        {
            if (Event.current.type == EventType.Repaint && --HotSwapMain.runInFrames == 0)
                HotSwapMain.DoHotSwap();

            if (HotSwapKeyDefOf.HotSwapKey?.KeyDownEvent ?? (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Home))
            {
                HotSwapMain.ScheduleHotSwap();
                Event.current.Use();
            }
        }
    }

    [HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DrawButtons))]
    static class DebugButtonsPatch
    {
        private const string Tooltip = "Hot swap";

        static readonly FieldInfo _fieldDebugWindowsOpenerWidgetRow = AccessTools.Field(typeof(DebugWindowsOpener), "widgetRow");
        static readonly MethodInfo _methodWidgetRowFinalX_get = AccessTools.PropertyGetter(typeof(WidgetRow), nameof(WidgetRow.FinalX));
        static readonly MethodInfo _methodDraw = SymbolExtensions.GetMethodInfo(() => Draw(default));
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codeMatcher = new CodeMatcher(instructions);

            // Locate the call to WidgetRow.FinalX; we want to place our own button right before it.
            codeMatcher.SearchForward(i => i.opcode == OpCodes.Callvirt && i.operand is MethodInfo m && m == _methodWidgetRowFinalX_get);
            if (!codeMatcher.IsValid)
            {
                Log.Error("Could not patch DebugWindowsOpener.DrawButtons, IL does not match expectations: call to get value of WidgetRow.FinalX was not found.");
                return codeMatcher.Instructions();
            }
            codeMatcher.Insert(new CodeInstruction[]{
                // call patch method (Draw)
                new(OpCodes.Call, _methodDraw),
                // put WidgetRow field back on stack (we "stole" it from the original call to FinalX)
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldfld, _fieldDebugWindowsOpenerWidgetRow)
            });

            return codeMatcher.Instructions();
        }

        static void Draw(WidgetRow row)
        {
            if (row.ButtonIcon(Resources.HotSwapButtonIcon, Tooltip))
                HotSwapMain.ScheduleHotSwap();
        }
    }

}
