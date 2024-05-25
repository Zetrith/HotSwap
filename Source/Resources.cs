using UnityEngine;
using Verse;

namespace HotSwap
{
    [StaticConstructorOnStartup]
    static class Resources
    {
        public static readonly Texture2D HotSwapButtonIcon = ContentFinder<Texture2D>.Get("UI/Buttons/HotSwap");
    }
}
