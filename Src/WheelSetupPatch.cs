using HarmonyLib;
using ModuleWheels;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>
    /// Gives a wheel suspension back its saved state as soon as the game builds the wheel.
    ///
    /// ModuleWheelBase builds a wheel (wheelSetup) by a coroutine that waits for its part to be unpacked: the frame
    /// after the unpack, once a physics step has gone by with no wheel at all. Its suspension is given back its state
    /// right after the build (<see cref="LandedShapeModule.OnWheelBuilt"/>).
    /// </summary>
    [HarmonyPatch(typeof(ModuleWheelBase), "wheelSetup")]
    internal static class WheelSetupPatch
    {
        /// <summary>Applies the patch. Throws when it cannot be applied: the suspensions then start as stock starts them.</summary>
        public static void Install(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(WheelSetupPatch)).Patch();
        }

        private static void Postfix(ModuleWheelBase __instance)
        {
            Part part = __instance.part;
            Vessel vessel = part != null ? part.vessel : null;
            LandedShapeModule module = vessel != null ? vessel.GetComponent<LandedShapeModule>() : null;
            if (module != null)
            {
                module.OnWheelBuilt(part);
            }
        }
    }
}
