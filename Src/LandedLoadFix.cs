using System;
using HarmonyLib;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>
    /// Loads a landed vessel where it was saved, instead of raising it to the height the terrain is computed at.
    ///
    /// At every load of a landed vessel, stock raises its root up to the height the terrain should have at its
    /// latitude and longitude, and never lowers it (Vessel.getCorrectedLandedAltitude). The ground the vessel rests on
    /// is the terrain's collider, made of flat triangles between points at that height: on a summit, a ridge or the
    /// edge of a flattened area, the triangles pass below the computed height, by tens of centimetres on Kerbin and by
    /// metres on rough ground. A vessel whose root rests there is saved below the computed height and loaded above its
    /// own position. A vessel of a single part, or one whose terrain detail changed since it was saved, is then put
    /// back onto the ground (Vessel.CheckGroundCollision); any other wakes up in the air and falls the whole gap, at
    /// every load.
    ///
    /// Here, every landed vessel is loaded at its saved altitude. The raise stock gives it guards against a terrain
    /// changed since the save, a planet pack updated for instance, which would leave the vessel inside the ground:
    /// that guard is kept, measured against the ground itself rather than the computed height. When a vessel stock
    /// leaves where it is goes off rails, the ground is built under it; if its root is then below that ground, the
    /// saved position is not trusted, and the vessel is put back onto the ground as stock does when the terrain detail
    /// has changed. Where no terrain collider can be found under it, the vessel gets the stock raise.
    ///
    /// A vessel that never left the ground since its launch (PRELAUNCH) is treated the same once it has been launched:
    /// stock puts it back onto the ground by its straight, packed shape at every load and warp exit, which raises a
    /// vessel bent under its weight and lets it fall.
    ///
    /// Vessels holding a ground part (a stock ground anchor, deployed science) skip that guard: riveted to the ground,
    /// with their origin at or below it, they are left where they were saved.
    /// </summary>
    internal static class LandedLoadFix
    {
        // The layer of the terrain colliders (and of the statics).
        private const int LocalSceneryLayer = 15;

        // How far above and below the root, in metres, the ground under it is looked for.
        private const float SearchAbove = 500f;
        private const float SearchBelow = 1000f;

        // How far below the ground, in metres, a root must be for the vessel to be put back onto it: a part whose
        // origin is at its bottom rests with it within millimetres of the ground.
        private const double Tolerance = 0.01;

        private static readonly Func<Vessel, bool> PhysicsHoldExpired =
            AccessTools.MethodDelegate<Func<Vessel, bool>>(AccessTools.Method(typeof(Vessel), "PhysicsHoldExpired"));

        // The overload of PQS.GetSurfaceHeight stock calls for its raise, internal.
        private static readonly Func<PQS, Vector3d, bool, double> SurfaceHeight =
            AccessTools.MethodDelegate<Func<PQS, Vector3d, bool, double>>(
                AccessTools.Method(typeof(PQS), "GetSurfaceHeight", new[] { typeof(Vector3d), typeof(bool) }));

        /// <summary>
        /// Applies the patches that load a landed vessel where it was saved and check it against the ground as it goes
        /// off rails. Throws when they cannot be applied: landed vessels are then raised as stock raises them.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            // The check first: alone, it leaves a vessel stock has raised where it is.
            harmony.CreateClassProcessor(typeof(GoOffRailsPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CorrectedLandedAltitudePatch)).Patch();
        }

        // Whether the vessel is one this fix handles: not splashed, around a body with a terrain (stock throws on one
        // without, as Jool).
        private static bool Handles(Vessel vessel)
        {
            return vessel != null && !vessel.Splashed && vessel.mainBody != null && vessel.mainBody.pqsController != null;
        }

        // A vessel that never left the ground since it was launched stays PRELAUNCH, through saves and loads. Stock
        // never lets one skip its ground positioning: at every load and every warp exit, Vessel.GoOffRails puts it
        // back onto the ground by the lowest point of its parts (Vessel.CheckGroundCollision), measured on its packed
        // shape, the straight one it was built with. A vessel resting on its own parts, its shape bent under its
        // weight, is then raised by the difference (25 cm for a 615 t base), and falls it as soon as it is unpacked.
        // Here, a PRELAUNCH vessel that is not being launched gets the flag stock computes for any other landed
        // vessel: left where it is when its saved terrain detail matches the current one, the guard of the
        // GoOffRails patch then checking it against the ground. A vessel being launched (spawning) keeps the stock
        // ground positioning.
        private static void LetPrelaunchSkipGroundPositioning(Vessel vessel)
        {
            if (vessel.situation != Vessel.Situations.PRELAUNCH || vessel.vesselSpawning
                || vessel.PQSminLevel == 0 || vessel.PQSmaxLevel == 0)
            {
                return;
            }
            PQS pqs = vessel.mainBody.pqsController;
            vessel.skipGroundPositioning = vessel.PQSminLevel == pqs.minLevel && vessel.PQSmaxLevel == pqs.maxLevel;
        }

        // Whether Vessel.GoOffRails will leave the landed vessel where it is, as computed there: not a spawning
        // vessel, the terrain detail saved with the vessel matches the current one, except on the launch pad (made
        // the same by LetPrelaunchSkipGroundPositioning), and the vessel is not a single part, unless a part dropped in
        // EVA construction.
        private static bool StockSkipsGroundPositioning(Vessel vessel)
        {
            if (vessel.vesselSpawning)
            {
                return false;
            }
            bool skip = vessel.skipGroundPositioning;
            PQS pqs = vessel.mainBody.pqsController;
            if (vessel.PQSminLevel != 0 && vessel.PQSmaxLevel != 0 && vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                skip = vessel.PQSminLevel == pqs.minLevel && vessel.PQSmaxLevel == pqs.maxLevel;
            }
            if (vessel.Parts.Count == 1 && !vessel.skipGroundPositioningForDroppedPart)
            {
                skip = false;
            }
            return skip;
        }

        // The height above the root, in metres along the local vertical, of the terrain collider of the vessel's body
        // found under or above it, or NaN when there is none there.
        private static double GroundAboveRoot(Vessel vessel)
        {
            Vector3d root = vessel.vesselTransform.position;
            Vector3d up = FlightGlobals.getUpAxis(vessel.mainBody, root);
            Vector3 origin = root + up * SearchAbove;
            RaycastHit[] hits = Physics.RaycastAll(origin, -up, SearchAbove + SearchBelow, 1 << LocalSceneryLayer,
                QueryTriggerInteraction.Ignore);
            double highest = double.NaN;
            for (int i = 0; i < hits.Length; i++)
            {
                // Statics share the layer: only the quads of this body's terrain count.
                PQ quad = hits[i].collider.GetComponent<PQ>();
                if (quad == null || quad.sphereRoot != vessel.mainBody.pqsController)
                {
                    continue;
                }
                double height = Vector3d.Dot((Vector3d)hits[i].point - root, up);
                if (double.IsNaN(highest) || height > highest)
                {
                    highest = height;
                }
            }
            return highest;
        }

        // Moves the packed vessel up by the given height, in metres.
        private static void Raise(Vessel vessel, double height)
        {
            Vector3d up = FlightGlobals.getUpAxis(vessel.mainBody, vessel.vesselTransform.position);
            vessel.SetPosition(vessel.vesselTransform.position + up * height);
            vessel.altitude += height;
        }

        // Vessel.Load calls this once, for a landed vessel whose parts are loaded, and moves the vessel to the
        // altitude it returns.
        [HarmonyPatch(typeof(Vessel), "getCorrectedLandedAltitude")]
        private static class CorrectedLandedAltitudePatch
        {
            private static bool Prefix(Vessel __instance, double lat, double lon, double alt, CelestialBody body,
                ref double __result)
            {
                if (!Handles(__instance))
                {
                    return true;
                }
                if (Log.IsDebugEnabled)
                {
                    // What stock would have done, for the record: the height the skipped method compares with.
                    double terrain = SurfaceHeight(body.pqsController, body.GetRelSurfaceNVector(lat, lon), true)
                        - body.Radius;
                    Log.Debug($"Landed load fix: '{__instance.GetDisplayName()}' loaded at its saved altitude"
                        + $" {alt:F4} m, which stock would have raised by {Math.Max(0.0, terrain - alt) * 1000.0:F1} mm");
                }
                __result = alt;
                return false;
            }
        }

        // GoOffRails is called at every frame while the vessel waits to be unpacked; it unpacks it once the physics
        // hold is over, or at once when the vessel holds a ground part. The check is made then, and only when stock
        // will not put the vessel back onto the ground itself.
        [HarmonyPatch(typeof(Vessel), nameof(Vessel.GoOffRails))]
        private static class GoOffRailsPatch
        {
            private static void Prefix(Vessel __instance)
            {
                Vessel vessel = __instance;
                if (!vessel.packed || !vessel.loaded || !vessel.Landed || !Handles(vessel))
                {
                    return;
                }
                LetPrelaunchSkipGroundPositioning(vessel);
                if (!StockSkipsGroundPositioning(vessel))
                {
                    return;
                }
                if (vessel.FindPartModuleImplementing<ModuleGroundPart>() != null)
                {
                    // Riveted, its origin at or below the ground: putting it back onto the ground would move it.
                    return;
                }
                if (!PhysicsHoldExpired(vessel))
                {
                    return;
                }

                double ground = GroundAboveRoot(vessel);
                if (double.IsNaN(ground))
                {
                    // No terrain built under it: the stock raise, onto the computed height.
                    CelestialBody body = vessel.mainBody;
                    double terrain = SurfaceHeight(body.pqsController,
                        body.GetRelSurfaceNVector(vessel.latitude, vessel.longitude), true) - body.Radius;
                    double gap = terrain - body.GetAltitude(vessel.vesselTransform.position);
                    if (gap > 0.0)
                    {
                        Raise(vessel, gap);
                    }
                    Log.Info($"Landed load fix: no ground found under '{vessel.GetDisplayName()}' as it goes off rails,"
                        + $" raised as stock raises it, by {Math.Max(0.0, gap) * 1000.0:F1} mm");
                    return;
                }
                if (ground > Tolerance)
                {
                    // The flag is set back so that stock skips its own call right after.
                    vessel.skipGroundPositioning = false;
                    bool putBack = vessel.CheckGroundCollision();
                    vessel.skipGroundPositioning = true;
                    if (putBack)
                    {
                        Log.Info($"Landed load fix: '{vessel.GetDisplayName()}' had its root {ground * 1000.0:F1} mm below"
                            + " the ground as it goes off rails, put back onto the ground");
                        return;
                    }
                    Raise(vessel, ground);
                    Log.Info($"Landed load fix: '{vessel.GetDisplayName()}' had its root {ground * 1000.0:F1} mm below"
                        + " the ground as it goes off rails, and could not be put back onto it: raised to it");
                    return;
                }
                Log.Debug($"Landed load fix: '{vessel.GetDisplayName()}' left where it is as it goes off rails, its root"
                    + $" {-ground * 1000.0:F1} mm above the ground");
            }
        }
    }
}
