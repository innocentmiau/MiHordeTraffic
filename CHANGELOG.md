# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0]

### Upgrading

The grid no longer grows past the edge of the navmesh, which means it covers less ground than it did and covers the right ground. Re-bake and expect **WalkableCells to fall**, by roughly whatever part of a cell was hanging off the navmesh at every edge in the level.

A corridor that was only a cell or two wide may lose its middle to that, because it was only ever wide enough with the invented ground included. The answer there is a smaller cell size, not a looser tolerance: a looser tolerance does not recover a corridor that is genuinely too narrow, it fabricates one. **Sample Accuracy** is on the driver if you need the old behaviour, and zero is exactly it.

Bodies standing on cells that stop being walkable take the recovery path on the first frame after the bake and walk themselves out to real ground, which reads as a one off shuffle along walls.

### Added

- **Sample Accuracy** on `HordeFlowFieldDriver`, from zero to one, deciding how closely a cell has to sit on the navmesh to be baked as walkable. One is on it or not; zero is the half cell of slack this had before. The bake log reports which was used.

### Fixed

- **The grid invented walkable ground around every obstacle in the level, and bodies stood inside walls on it.** The bake asks the navmesh for the nearest surface to each cell centre, and that call answers however far away it has to look: a cell centre buried in a wall finds the navmesh at the wall's foot and reports a hit. Measured against half a cell that hit was accepted, so a strip of ground half a cell wide appeared around everything solid, with its height taken from the edge it found. The question is whether a cell is on the navmesh, not whether the navmesh is near it, and those differ by exactly the amount that puts a body inside a wall.

- **The flow field pulled bodies into walls once a route got busy.** A neighbouring cell that is unwalkable stands in for its own cost with the centre's, so that a wall flattens that side of the slope rather than acting as a hill bodies are pushed down. That is right while the open side is downhill and wrong the moment it is not: an uphill neighbour on one side and a wall on the other reads as the wall being the cheaper way, and the gradient points into it. Congestion is what makes the open side uphill, so this appeared only once a route filled up and got worse the busier it got, as bodies peeling off towards a blocked way, being refused at its face and drifting back. A blocked side may still flatten its axis and can no longer win it, so a crowd walking along a wall still hugs it and is no longer pulled in.

## [0.3.0]

### Upgrading

The field expansion no longer happens on the main thread. It is scheduled, runs across however many frames it needs, and is promoted when it finishes, so the crowd reads the previous field while the next one is built. Nothing has to change for that, but two things read differently because of it: `AverageBuildMilliseconds` is now how long an expansion takes to arrive rather than what it cost the frame, and the `Pathing.FlowField` row of the phases table, which is what it costs the frame, should be close to zero.

A field whose goal has not moved and whose costs have not changed is no longer expanded at all. On a static target with congestion off that is every rebuild the driver would ever do. Anything changing the grid from outside has to say so with `MarkDirty`, which the obstacles below do for you.

Bodies close to the goal steer at the goal itself rather than by the direction stored in the cell they stand in, and bodies that have arrived now turn to follow a target moving around them.

### Added

- **`HordeObstacle`**, which takes the ground under it out of the grid while it is enabled and gives it back when it is not, so a building can be put down without re-baking anything. Blocking is an overlay on the bake rather than an edit of it, held as a count per cell, so overlapping footprints and demolition both come out right. It has no Update and no per frame cost of any kind.

- **`HordeMovingObstacle`**, deliberately a separate component so a wall that will never move keeps costing nothing. It rechecks its footprint a few times a second rather than every frame, skips entirely when it still covers the same cells, and by default asks for no expansion at all: bodies cannot walk into it because every step is tested against walkability, they are simply not steered around it. **Update Routing** turns that on for something large enough that sliding along it does not get a body past.

- **`HordeSpawn.TryFind(position, maximumDistance, out spawn)`**, which answers where a body can actually be put down: on the grid, reachable, and not already packed, nearest first, returned on the ground and scattered inside its cell so a wave does not stack on one spot. Sampling the navmesh from outside the package does not answer this, because the bake erodes for clearance and rejects ground the navmesh is perfectly happy with.

- **The worst frames of a run** in the session report, each with the phase breakdown of that frame rather than an average. A single spike of a tenth of a second across a couple of thousand frames moves every row of the averages by four hundredths of a millisecond, which means the report you would go to looking for it is the one report guaranteed not to show it. Set by **Worst Frames Tracked** on the scheduler, four by default, zero to switch it off. It records nothing unless a frame turns out to be among the slowest, and allocates nothing at all.

- A **bake warning** naming the cell count and suggesting a cell size when a grid is large enough for the expansion to run across several frames. Cell Size looks like a quality setting and behaves like a quadratic cost, and that is not something anyone should have to find out with a profiler.

- `HordeFlowMovement.ActiveCongestionCells` and `PeakCongestionCells`, reported beside the field as **congestion N cells tracked, M at peak, against B bodies**. What the congestion pass walks is not the crowd, it is every cell the crowd has stood in recently, and the gap between those two numbers is the only way to see that. It was fifteen cells per body when it was first measured.

- `HordeFlowFieldDriver.BlockArea`, `UnblockArea` and `MarkDirty`, and `HordeFlowMovement.CellCapacity`.

### Changed

- **The expansion is asynchronous.** It writes into back buffers and swaps them in when it finishes, which is what lets it take as long as it needs. A million cell grid is most of a fifth of a second, and paying that on the main thread was a hitch every rebuild interval; leaving it in flight instead needs the crowd to have something consistent to read in the meantime, and the only thing that is is the field as it was before. The cost array is copied at schedule time for the same reason from the other side, since congestion rewrites it every frame.

- **The congestion pass runs over the cells the crowd is standing in rather than over the grid.** It was four passes on one thread, three of them proportional to the map: a kilometre at one metre was fifteen million memory bound operations a frame, with the mover waiting on all of it. It is four jobs now, three of them on every worker, walking an active set that cells join when a body arrives and leave once their density has decayed back to empty. Five thousand bodies stand in at most five thousand cells, so what this costs stopped depending on how big the map is.

- **The per cell direction array is gone.** Every expansion used to end with a parallel job over the whole grid, turning the integration field into a direction for each cell and writing an array of a million of them, of which a crowd of five thousand ever read five thousand. Nothing waited on it, because the movement reads the previous field while the next is built, and it did not need to: it occupied every worker for the frames it took, so the mover's own chain had nobody to run it and the main thread ended up doing all of that work alone. It showed as the movement wait spiking to sixteen milliseconds against an average of one and a half. A direction is a local slope of the integration field, so `HordeFlowDirection` takes it where it is asked for, at four array reads for the cell a body is standing in. Eight megabytes lighter on a kilometre of map, and the pool stays free.

- **Parallel batches are capped as well as floored.** A batch is the smallest amount of work anything can commit to, and the main thread does not idle while it waits on a job, it takes batches and runs them. Sized only by dividing, a large grid gave thirty thousand cells to a batch, so joining near the end of one meant volunteering for a third of the flow field.

- **Cells leave the congestion set when they stop mattering rather than when they stop being nonzero.** Both thresholds were absolutes far below anything that changes behaviour, against decay that is exponential: density took about seven seconds to reach a thousandth of a body and cost about eight and a half to come back within a thousandth of ordinary ground. Every cell the crowd walked over stayed tracked for that long, so the pass cost the crowd multiplied by the length of its trail. Density is now measured against what a cell holds at rest and the fill where bodies begin easing off, so it means the same thing at any cell size, and cost is allowed a fiftieth either way.

- **The worst frames are ranked by what this package cost, not by how long the frame took.** A run holding four editor stalls or a collection reported those four and nothing else, and the frames where the crowd was genuinely slow never made the list. The frame time is still carried on every entry, so a spike that is mostly not this package still says so.

- A body's position is not written when it has not moved, for the same reason its rotation is not: Unity's transform system does its work for transforms that changed. A settled body is asked for no speed and, once its neighbours are far enough away, handed no push either, so what was left was the same three floats written over themselves for most of the crowd every frame.

- Parallel jobs sized from the machine rather than by a constant, through `HordeJobBatch`. The direction job ran at sixty four cells a batch, which is two and a half thousand dispatches on a large grid and a job that spends much of its life handing out work while everything queued behind it waits for a worker.

- The scheduler skips its whole per body tick when the active technique does not repath bodies, rather than dispatching to five thousand implementations that stand themselves down on their first line.

- Turning is a rotation rather than three arctangents, a sine and a cosine per body per frame. The limit is a rate times the step and therefore the same for every body, so its sine and cosine are worked out once when the job is scheduled.

- A rotation is not written when the body is already facing where it wants to. The saving is the write rather than the arithmetic: Unity's transform system does its work for transforms that changed.

### Fixed

- **A crowd walked through walls whenever the route to the goal was cut.** Standing on ground the grid describes and being able to reach the goal from it were the same flag, and only the first of those earns a body the right to move wherever it likes. That right exists for a body the grid cannot describe at all, spawned off the mesh or on a corner the erosion took, because refusing its steps is what stranded it. A body behind a shut gate is in the opposite situation, and was handed the same exemption.

- **Bodies stopped turning within a cell of the goal.** The goal's own cell integrates to zero, so nothing around it is cheaper and both direction modes hand back a zero vector for it, which the movement job read as having nothing to say and skipped the whole steering block on. A body standing there never updated its heading again whatever the target did. Outside that cell it was the same problem at cell resolution: every body in a cell reads one direction, pointing at the neighbouring cell rather than at the target, so a target moving inside its own cell changed nothing anyone could see. Steering blends to the goal itself across the arrive taper now, and a body that has arrived is allowed to turn without walking.

- The recovery search demanded a route as well as ground to stand on, so a body inside a structure that closed on top of it in a sealed region found nothing, fell back to walking at the goal, and was permitted to do that through everything in the way. It asks for footing now and prefers a route.

- A block or unblock queued before the grid was baked was cleared along with the rest of the queue rather than kept, so an obstacle in a scene that bakes on start looked configured and blocked nothing for the session.

- A pooled body came back still holding the separation push it had when it was despawned, which the mover feeds straight into velocity, so it set off sideways on its first frame before separation had looked at it once.

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
