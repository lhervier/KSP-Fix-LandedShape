# The fixes

Part of [Landed Shape Fix](../README.md), [the fixes](../README.md#the-fixes): every patch, by switch.

Its switches are in [Settings](../README.md#settings), all on by default.

## `fixLandedLoad`: loaded where it was saved

- A prefix of `Vessel.getCorrectedLandedAltitude` returns the saved altitude for every landed vessel, not splashed,
  around a body with a terrain.
- A prefix of `Vessel.GoOffRails` keeps the guard the stock raise gave against a terrain changed since the save (a
  planet pack updated, for instance), measured against the ground itself: when the vessel leaves the rails, the
  ground is built under it, and if its root is then below that ground, the vessel is put back onto it as stock does
  when the terrain detail has changed. Where no terrain collider is found under it, it gets the stock raise. Vessels
  holding a ground part (a ground anchor, deployed science), riveted to the ground with their origin at or below it,
  skip that guard.
- The same prefix lets a `PRELAUNCH` vessel that is not being launched skip the ground positioning as a `LANDED` one
  does, and goes through the same guard. A vessel being launched keeps the stock ground positioning.

**Why not raise the vessel by the lowest point of its bent shape.** The stock raise and ground positioning guard
against a ground that moved since the save. With the ground built where it was (Terrain Precision Fix), the saved
position is right: checking it against the ground found under the root keeps the guard, without moving a vessel that
is where it should be.

## `keepLandedShape`: brought back in its shape and motion

A vessel module saves, for each part of a landed vessel, its pose relative to the root when it is not its
construction pose, its velocity, the pose of the moving part of a robotic part, and the state of a wheel suspension
(spring, damper, rest compression, whether it touched the ground). It takes them when the game is saved and when the
vessel goes on rails (time warp, leaving the physics range).

- When the vessel leaves the rails (`GameEvents.onVesselGoOffRails`), each part is put back in its saved pose, before
  the first physics step. The attach joints were created on the construction shape, so they get back the strain they
  had.
- A postfix of `ModuleWheelBase.wheelSetup` gives each suspension back its state as soon as the game builds the wheel.
- The parts get the velocities they had as [Physics Easing Fix](https://github.com/lhervier/KSP-Fix-PhysicsEasing)
  lets the vessel go: it gives the vessel its full weight at once, which its joints are bent for, and holds a vessel
  on wheels still until they hold it. The module listens to its `onPhysicsEasingHold` and `onPhysicsEasingLetGo`
  events, found by name (`GameEvents.FindEvent`), without depending on it. A vessel it does not hold gets its
  velocities at the first physics step; without Physics Easing Fix, as soon as its shape is put back.
- A prefix of `Vessel.CheckGroundCollision` drops the saved shape, velocities and suspension states of a vessel the
  game puts back onto the ground as it leaves the rails (a terrain detail changed since the save, for instance): it
  then leaves the rails as it does without the mod. See [Limits](../README.md#limits).

The velocities saved are those of the motion the game shows, measured from the poses of the last physics steps, not
`Rigidbody.velocity`: on a light part held in a stiff chain of heavy ones, a docking port for instance, the physics
engine reports a velocity of its own while the part does not move.

**Why give the velocities back.** A load swinging when the game was saved, or when time warp started, swings on from
where it was. Stopping it dead would be a jolt of its own.

## Without the mod

The shape is saved in the vessel, in a `LandedShapeModule` node; removing the mod drops it, and the vessel loads as it
does without the mod. If a stock method is missing, its patch is not applied, with an error in `KSP.log`.
