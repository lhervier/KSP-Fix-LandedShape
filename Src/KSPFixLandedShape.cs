using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>
    /// Reads the settings and installs the fixes they turn on, once, when KSP starts: from then on, a landed vessel is
    /// loaded at the altitude it was saved at, and gets back the shape its load gave it.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPFixLandedShape : MonoBehaviour
    {
        private const string HarmonyId = "com.github.lhervier.ksp.fix.landedshape";

        private void Start()
        {
            Settings.Load();
            string version = typeof(KSPFixLandedShape).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Log.Info($"Version {version}, fixLandedLoad {Settings.FixLandedLoad}, " +
                $"keepLandedShape {Settings.KeepLandedShape}, log level {Log.Level}");

            Harmony harmony = new Harmony(HarmonyId);
            if (Settings.FixLandedLoad)
            {
                try
                {
                    LandedLoadFix.Install(harmony);
                    Log.Info("Landed load fix installed");
                }
                catch (Exception e)
                {
                    Log.Error($"Could not install the landed load fix, landed vessels are raised as stock raises them: {e}");
                }
            }

            // LandedShapeModule, a vessel module, reads its setting itself; only the easing it brings needs a patch.
            if (Settings.KeepLandedShape)
            {
                try
                {
                    NoEasingAfterShape.Install(harmony);
                    Log.Info("Physics easing skipped after a shape is put back");
                    WheelSetupPatch.Install(harmony);
                    Log.Info("Wheel suspensions given back their state after a shape is put back");
                }
                catch (Exception e)
                {
                    Log.Error("Could not skip the physics easing, a vessel whose shape is put back is still eased into " +
                        $"physics and may bounce: {e}");
                }
                try
                {
                    GroundPositioningForgetsShape.Install(harmony);
                    Log.Info("Saved shape dropped when the game puts a vessel back onto the ground");
                }
                catch (Exception e)
                {
                    Log.Error("Could not watch the ground positioning, a vessel the game puts back onto the ground still " +
                        $"gets its saved shape back: {e}");
                }
            }
        }
    }
}
