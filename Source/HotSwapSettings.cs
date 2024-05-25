using UnityEngine;
using Verse;


namespace HotSwap
{
    public class HotSwapSettings : ModSettings
    {
        private bool _enableAutoHotSwap = false;
        public bool EnableAutoHotSwap
        {
            get => _enableAutoHotSwap;
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref _enableAutoHotSwap, "enableAutoHotSwap", false);
        }

        internal void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listingStandard = new();
            listingStandard.Begin(inRect);

            listingStandard.CheckboxLabeled(
                "Enable auto-hotswap",
                ref _enableAutoHotSwap,
                "If this setting is enabled, the mod will regularly look for a file named " +
                "[AssemblyName].dll.hotswap next to any hot-swappable [AssemblyName].dll, " +
                "and if it finds one, it will trigger a hot-swap, and then delete the " +
                "[AssemblyName].dll.hotswap file.");

            listingStandard.End();
        }
    }

}
