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
        private const string Tooltip = "Hot swap.";

        static void Postfix(DebugWindowsOpener __instance)
        {
            Draw(__instance.widgetRow);
        }

        static void Draw(WidgetRow row)
        {
            if (WidgetRow_ButtonIcon(row, TexButton.Paste, Tooltip))
                HotSwapMain.ScheduleHotSwap();
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
