using RimWorld;
using Verse;

namespace HotSwap
{
    [DefOf]
    public class HotSwapKeyDefOf
    {
        public static KeyBindingDef HotSwapKey;

        static HotSwapKeyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(NeedDefOf));
        }
    }
}
