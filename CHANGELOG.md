# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0]

### Upgrading

Two of the three signals this package steers on were dead in 0.1.0 and are live now, so a crowd upgrading to this will behave differently without anything in your scene having changed. That is the fix, but it is not a quiet one.

Bodies settle at chokepoints they used to press through forever, and congestion pricing starts actually pricing. If you tuned against 0.1.0 you tuned against numbers that were not reaching anything, so `stallSettleDelay`, `stallFraction`, `settledDriveScale` and the whole congestion block are worth reading again rather than trusting. `blockingEnabled` also defaults to on now.

### Added

- **Overlap Tolerance** on `SeparationSettings`, as a fraction of two bodies' combined radii, defaulting to `.04`. Below it an overlap is left alone. Chasing an exact non overlapping arrangement of a few hundred bodies that are all pressed together is a problem with no stable answer: resolving one pair creates the next, so the crowd never reaches a state where nothing needs correcting and settles into a permanent low amplitude churn instead. Allowing a little overlap gives that search somewhere to stop. It is a fraction rather than a distance so it stays the same proportion of a body whether the crowd is made of rats or of siege engines. Zero restores the old behaviour exactly.

- A **phases table** in the session report, attributing main thread time to each stage of the frame: pathing tick, schedule, flow field build and bake, the movement gather, schedule, move and publish, and separation's tick, complete and apply. The rows deliberately overlap, so the total is a ceiling rather than a sum. This is what tells you whether a slow frame is the crowd at all, and the first thing it was used for said it was not: a test at 5000 bodies reading 30 ms a frame had 2.4 ms of this package in it, and the rest was a prefab that had lost its `LODGroup`. Markers are compiled out of non development builds, and the table says so rather than reporting zero.

- A **Settling** section in the README, and **Read the renderer before you tune the crowd** under Diagnostics, written from that same case. Simulation cost per body is flat and rendering cost per body is not, and a crowd that settles is a crowd that piles into one place, which this package does on purpose.

### Changed

- **Blocking is on by default.** It was off because the only component that ever published a goal was the optional `HordeEntity`, so out of the box it cost a neighbour comparison per overlapping pair to compute a flag that could never become true. The flow field publishes the driver's target as every body's goal now, so it means something.

- The congestion pass is **handed to the movement job as a dependency rather than joined on the spot**. It is a single threaded job over every body and every cell, so joining it parked the main thread for the whole of it, which at 5000 bodies was the most expensive thing the mover did and was spent entirely on waiting. Chaining says the same thing to the scheduler instead of to the clock, and what the join protected is still protected by execution order.

- `HordeEntity` stands its per body tick down while the mover is holding that body, rather than calling out to native for every body on every frame to be told there is nothing to do. It asks about the body rather than about the crowd's mode, because a body can be out of the mover's hands while the crowd around it is not.

- Agents leave the global roster by swapping the last one into their place instead of by `List.Remove`, which was a linear scan whose comparison calls into native. Tearing down a crowd of 5000 was about twelve million of those comparisons in the frame the scene unloads.

- The session report **discards frames over a second** as pauses or loads rather than frames, and says how many it dropped. One 123 second editor pause had been dragging a mean of 10.8 up to 74.7.

- `HordeBenchmark` applies its avoidance mode to the crowd already standing there rather than only to the next spawn, resets its statistics when the mode changes so one mode's frames do not average into the other's, and **warns when the avoidance mode and the pathfinding technique cannot both be what they are**. Any mode other than `SEPARATION_SYSTEM` switches every `HordeAgent` off, and that same component is what puts a body on the flow field's roster, so those modes leave nothing driving the crowd unless the technique is `NAVMESH_AGENT`.

### Fixed

- **The stall path had never once fired, and congestion priced every cell against a tenth of a walking pace.** The movement job cleared `OpenSpeed` and `StallTime` and then read those same slots a few lines further down expecting the previous frame's value, so both reads came back zero every frame. `StallTime` at zero restarted the accumulator every frame, so it could never reach the settle delay and `Stalled` was written as zero for every body on every frame since the day it was added: the only way left to settle a crowd was standing inside the arrive radius, which is exactly the case the stall path exists to replace, since a jam at a doorway or at the back of another crowd has nothing in it that has arrived. `OpenSpeed` at zero left the unobstructed speed ramp one acceleration step wide, about a tenth of a metre per second against a real walking pace of three and a half, and that ramp is the reference congestion compares progress against, so every jam on the map measured as flowing perfectly. It was framerate dependent too: past roughly 200 frames a second the ramp fell under the steering epsilon and bodies stopped turning at all.

- Removing a body from the mover's roster left three of its buffers behind. Every array that survives between frames has to move with the body that is swapped into the freed slot, and the stall timer, the open speed ramp and the congestion progress average did not, so a body taking a despawned body's place inherited its stall state. On a crowd that despawns constantly that was a steady trickle of bodies deciding they had given up for no reason.

- `SeparationGroup` did not carry the push across the same swap, although the flag saying a push was outstanding did carry, so a moved body could arrive claiming one while the slot underneath held the departed body's.

- `SeparationGroup` now refuses a body that is already on its roster, the way the mover's roster already did. A double registration took two slots and the body's index named only the second, leaving the first as a row nothing could remove and a destroyed reference the apply loop called through every frame afterwards.

- A `HordeAgent` disabled while the flow field was driving left its `NavMeshAgent` switched off for good. Flow mode switches the agent off when it takes a body over and the only thing that ever switches it back on is the mode change, which returned early for a disabled component, so switching the crowd back to `NavMeshAgent` movement changed nothing for exactly those bodies. They stood still with neither system driving them and every setting on them reading as correct. Putting the agent back now runs for a disabled component; adding a missing one still does not, since repairing something this component broke is not the same as joining in.

## [0.1.0]

First release.
