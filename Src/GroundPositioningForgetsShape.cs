using HarmonyLib;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>
    /// Drops the saved shape of a vessel the game puts back onto the ground as it leaves the rails.
    ///
    /// Vessel.GoOffRails puts a landed vessel back onto the ground (Vessel.CheckGroundCollision, its only caller) when
    /// it does not trust the saved position: the terrain detail changed since the save, a vessel of a single part, a
    /// vessel being launched, a PRELAUNCH vessel when the landed load fix is off; the guard of
    /// <see cref="LandedLoadFix"/> calls it too, when the vessel's root is below the ground. The vessel is moved by
    /// the lowest point of its parts, measured on its packed, construction shape: the shape saved by
    /// <see cref="LandedShapeModule"/> no longer matches where it stands, and is dropped with its velocities and
    /// suspension states. The vessel then leaves the rails as it does without the mod.
    /// </summary>
    [HarmonyPatch(typeof(Vessel), nameof(Vessel.CheckGroundCollision))]
    internal static class GroundPositioningForgetsShape
    {
        /// <summary>Applies the patch. Throws when it cannot be applied: the shape is then put back even on a vessel the
        /// game has just put back onto the ground.</summary>
        public static void Install(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(GroundPositioningForgetsShape)).Patch();
        }

        private static void Prefix(Vessel __instance)
        {
            LandedShapeModule module = __instance.GetComponent<LandedShapeModule>();
            if (module != null)
            {
                module.ForgetShape("put back onto the ground by the game as it leaves the rails");
            }
        }
    }
}
