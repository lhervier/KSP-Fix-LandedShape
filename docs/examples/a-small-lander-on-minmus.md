# A small lander on Minmus

Part of [Landed Shape Fix](../../README.md), [two examples](../../README.md#two-examples): a light vessel on legs.

A Mk1 pod on a fuel tank and an engine, on four LT-1 landing legs, from the
[SuspensionBounceLegMinmus.sfs](https://github.com/lhervier/KSP-Fix-SuspensionBounce/blob/main/diag/SuspensionBounceLegMinmus.sfs)
save of Suspension Bounce Fix, loaded from the quickload menu (Alt+F9 by default).

Without this mod, the lander leaves the ground as soon as it is loaded, and lands back a few seconds later; saved
again once at rest and loaded, it jumps again, at every load, and the same way every time time warp stops.

<img src="../../imgs/20-lander-minmus-warp-stock.gif" width="640" alt="The lander on Minmus at rest, time warp at x5 for a few seconds, then back to normal time: the lander jumps and lands back">

*KSP 1.12.5 and KSP Community Fixes, without this mod, on Minmus: the save loaded, saved again once the lander was at
rest and loaded again; once at rest, time warp at ×5 for about 3 s, then back to normal time. The recording starts
at the end of the time warp.*

No part of the lander has to be moved: what it lacks when physics starts is its leg suspensions as they were, and its
weight (see [the culprit](../the-culprits/at-rest-weightless-on-soft-suspensions.md)). This mod gives the suspensions
back their state; [Physics Easing Fix](https://github.com/lhervier/KSP-Fix-PhysicsEasing) gives the lander its full
weight at once and holds it still until its legs hold it.

An earlier version of this mod, which skipped the easing and held the lander itself, kept it standing still once it
had been saved with the mod. That work has since moved to Physics Easing Fix.
