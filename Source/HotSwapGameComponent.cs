
using System;
using System.IO;
using Verse;

namespace HotSwap
{
    public class HotSwapGameComponent : GameComponent
    {
        private static TimeSpan UpdateFrequency = TimeSpan.FromSeconds(5);

        private DateTime lastUpdate = DateTime.UtcNow;

        public HotSwapGameComponent(Game _)
        {
        }

        public override void GameComponentUpdate()
        {
            if (HotSwapMain.settings.EnableAutoHotSwap && (DateTime.UtcNow - lastUpdate) > UpdateFrequency)
            {
                bool triggerHotswap = false;
                foreach (var assemblyFile in HotSwapMain.AssemblyFiles)
                {
                    var hotSwapFile = assemblyFile.Value.FullPath + ".hotswap";
                    if (File.Exists(hotSwapFile))
                    {
                        try
                        {
                            File.Delete(hotSwapFile);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"Couldn't delete {hotSwapFile}: {ex}");
                        }
                        triggerHotswap = true;
                    }
                }
                if (triggerHotswap)
                {
                    HotSwapMain.ScheduleHotSwap();
                }
                lastUpdate = DateTime.UtcNow;
            }
        }
    }
}
