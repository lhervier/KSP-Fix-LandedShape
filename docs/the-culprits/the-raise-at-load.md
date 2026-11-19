# A landed vessel is raised to the height the terrain is computed at

Part of [Landed Shape Fix](../../README.md), [the culprits](../../README.md#the-culprits): a raise at every load,
and the reason this mod goes with Terrain Precision Fix.

At every load, `Vessel.getCorrectedLandedAltitude` raises the vessel's root up to the height the terrain should have
at its latitude and longitude, and never lowers it:

```csharp
// Vessel.getCorrectedLandedAltitude (KSP 1.12)
double val = body.pqsController.GetSurfaceHeight(relSurfaceNVector, overrideQuadBuildCheck: true) - body.Radius;
return Math.Max(alt, val);
```

The ground the vessel actually rests on is the terrain's collider, made of flat triangles between points at that
height: on a summit, a ridge or the edge of a flattened area, those triangles pass below the computed height. A
vessel resting there is saved below that height, loaded above its own position, and falls the whole gap.

## A patch against vessels inside the ground

This raise looks like a patch Squad put in to keep vessels from ending up inside the ground at load (the changelog of
KSP 0.19.1 announces *"Improved detection/response to vessels below terrain"*). The ground is not always rebuilt at
the same height from one load to the next, so a vessel saved resting on it could load partly inside it; Squad
probably did not see that the ground itself moved, and raised such vessels up to the height the ground was supposed
to have.

This is also why this mod makes no sense without
[Terrain Precision Fix](https://github.com/lhervier/KSP-Fix-TerrainPrecision): it removes the raise and loads a
vessel exactly where it was saved, which is only right if the ground is rebuilt where it was, as Terrain Precision
Fix makes it.
