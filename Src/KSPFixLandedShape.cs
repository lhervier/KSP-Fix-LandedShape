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

            // LandedShapeModule, a vessel module, reads its setting itself; the suspension states it gives back need a
            // patch. KSP-Fix-PhysicsEasing skips the physics easing and holds the vessel until its wheels hold it.
            if (Settings.KeepLandedShape)
            {
                try
                {
                    WheelSetupPatch.Install(harmony);
                    Log.Info("Wheel suspensions given back their state after a shape is put back");
                }
                catch (Exception e)
                {
                    Log.Error($"Could not give the wheel suspensions back their state, they start as stock starts them: {e}");
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
