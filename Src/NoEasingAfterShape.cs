using HarmonyLib;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>
    /// Skips the physics easing of a vessel whose shape <see cref="LandedShapeModule"/> has just put back.
    ///
    /// When a landed vessel leaves the rails, KSP eases it into physics (GameSettings.PHYSICS_EASE, on by default):
    /// VesselPrecalculate.StartEasing sets Vessel.gravityMultiplier to 0, and it then grows by 1/16 at every physics
    /// step. Meant for a vessel set down roughly, it takes the weight off a vessel put back at rest in its shape under
    /// load: its joints relax, the wheel suspensions sized on that weight soften, and the vessel bounces as the weight
    /// comes back. Such a vessel starts with its full weight instead.
    /// </summary>
    [HarmonyPatch(typeof(VesselPrecalculate), "StartEasing")]
    internal static class NoEasingAfterShape
    {
        /// <summary>Applies the patch. Throws when it cannot be applied: vessels are then eased as stock eases them.</summary>
        public static void Install(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(NoEasingAfterShape)).Patch();
        }

        private static bool Prefix(VesselPrecalculate __instance)
        {
            Vessel vessel = __instance.Vessel;
            LandedShapeModule module = vessel != null ? vessel.GetComponent<LandedShapeModule>() : null;
            if (module == null || !module.TakeJustRestored())
            {
                return true;
            }
            Log.Debug($"{vessel.GetDisplayName()}: physics easing skipped, its shape was just put back");
            return false;
        }
    }
}
