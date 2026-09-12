# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.7.1]

### Fixed

- **The advisor told you to raise Cell Size because the grid was large.** It claimed congestion walks every cell every frame, which stopped being true when the pass became a sparse active set, and it fired on the cell count alone however small the crowd was. So it argued against the change that made a metre of cells affordable, to exactly the people who had just made it, while the report two lines below said `3 cells tracked`. It now measures the cells congestion actually tracks against the bodies, and only speaks when the trail the crowd leaves is most of the work.

### Changed

- **The session report leaves out techniques that never ran.** Both blocks exist to compare `NAVMESH_AGENT` against `FLOW_FIELD`, and outside a benchmark neither is measured, so the report opened with two headings and four `not run` lines before reaching anything true. That reads as something having failed. It now says once that techniques were not measured, and why that is ordinary.

- **The report counts bodies at their peak rather than at quit.** It is written from `OnApplicationQuit`, and a pooled crowd is mostly asleep by then: five hundred agents with one enemy left standing reported `against 1 bodies`, which made every per body figure beside it read as nonsense. Added `HordeFlowMovement.PeakBodyCount` for it.

## [0.7.0]

### Fixed

- **`HordeSpawn.TryFind` threw when called from anywhere but a narrow part of the frame.** It reads the smoothed density to reject crowded cells, and that array is owned by the congestion pass from `Update` until `LateUpdate`. Coroutines, events, button callbacks and wave timers all run inside that window, so a wave spawner threw `InvalidOperationException` naming `HordeCongestionPruneJob`. The first body always landed, because there was no congestion work in flight yet to collide with, which made it look like the second spawn had broken something. It now finishes any movement work still in flight before reading.

- **`HordeFlowMovement.TryGetState` had the same problem**, reading heading and push while the move job still owned them. Gizmos run late enough to be safe by accident; calling it from `Update` threw.

### Added

- **`HordeFlowMovement.CompletePending()`**, which finishes work still in flight so the arrays the jobs write can be read safely. Only needed if you read those arrays yourself, such as `GridFlowField.Density`. It is free when nothing is running and never costs more than bringing `LateUpdate`'s own join forward.

- **Two samples**, installable from the Package Manager and deliberately separate, so whichever one you open is the whole of what that technique costs to set up rather than half of a script that does both.

  **Flow Field Crowd** is a spawner and an optional body script. It shows the part worth copying, asking `HordeSpawn` where a body can stand rather than sampling the navmesh, and says plainly what a body does **not** need: no destination, no separation goal, no wants to move flag, and nothing at all for pooling. `HordeFlowMovement` writes all of those itself every frame, so a script that also writes them is overwriting the mover's answer with a worse one, which is the most common way a crowd ends up behaving strangely with nothing obviously wrong.

  **NavMesh Agent Crowd** is a spawner and no body script, because `HordeEntity` already is one. It holds the `NavMeshAgent`, registers with `HordePathScheduler` so repaths are paced across the crowd, and takes a target. Writing a component beside it is how a project reimplements it without the pacing, which is the part that makes a large crowd affordable.

## [0.6.0]

### Upgrading

**`BlockArea` and `UnblockArea` take the kind as their second argument**, ahead of the routing flag. It used to trail both defaults, which made it the easiest argument in the pair to leave off, and leaving it off on the release half of a gate is silent and permanent: the solid count is decremented instead, floors at zero, and the gate is never given back. Call sites that passed only an area still compile and still mean `SOLID`. Any that passed `updateRouting` positionally will not compile, which is the point.

**`GridFlowField.Schedule` takes a `HordeGoalArea` instead of a `float3`.** `HordeGoalArea.Point(position)` is the old behaviour exactly. Only relevant if you drive the field yourself.

**Bodies with no route to the goal now hold position instead of walking at it.** A straight line at the goal is not a route, and the reason there is no route is usually something solid on exactly that bearing. Set **Unreachable Goal** to `APPROACH` on `HordeFlowMovement` to get the old behaviour back. This only covers ground genuinely sealed off: a target standing inside an obstacle still routes normally, because the expansion seeds from the nearest walkable ground to it.

### Added

- **`HordeTarget`**, which gives a goal a shape: `POINT`, `BOX` or `SPHERE`, with the size read from a `Collider`. The expansion seeds every walkable cell the shape covers rather than the single cell nearest its centre, so a crowd arrives at the nearest part of a building and surrounds it instead of funnelling onto one face. It is a multi source expansion and costs nothing extra, since each cell is still settled exactly once. A target with no component is a point and behaves as it always did. Arrival and the near goal steering blend measure against the shape's edge, so **Arrive Radius** becomes real standoff distance from a wall.

- Shaped targets seed the first walkable ring **around** a target whose own cells are blocked, so a building can carry a `HordeTarget` and a `SOLID` `HordeObstacle` together. That combination is what stops a crowd pushing itself inside the thing it is attacking: a step into blocked ground is refused whether a body walked there or was shoved into it.

- **Unreachable Goal** on `HordeFlowMovement`: `HOLD` or `APPROACH`, for what a crowd does when the goal cannot be reached from where it stands.

- **`IsBakedWalkable`**, **`BlockCountAt`** and **`GateCountAt`** on `GridFlowField`, for telling apart the reasons a cell is shut.

- A warning when a `SOLID` obstacle covers no ground the bake found walkable. It blocks nothing and switching it off opens nothing, which looks exactly like an obstacle refusing to clear. Almost always the building was present when the NavMesh was baked, so Unity carved a hole under it and the grid copied the hole. Editor and development builds only.

### Changed

- Unwalkable cells draw in two colours: **amber** where a runtime obstacle is holding ground, **red** where the bake never found any. Only one of those can be reopened, and they were indistinguishable.

- **Gated cells are drawn.** They stay walkable by design, so they looked identical to open ground, and only the mover ever read the gate count. A stuck gate was the one failure in this package with no visible symptom other than a crowd standing still in front of a doorway the field was happily routing through.

- `HordeObstacle` re-applies when its kind, size or offset is edited during play, instead of needing the object switched off and on again.

### Fixed

- **Obstacles never applied at all on a large grid.** Block and unblock requests waited for a frame with no expansion in flight, because the expansion reads the walkability they edit. When an expansion takes longer than the interval that asks for one, which a million cell grid does, there is no such frame ever: a building switched off never came back, for the rest of the session. The expansion now takes its own copy of walkability and gates, the same way it already copied cost, so blocking is applied the frame it is asked for.

- **A queued block did not make a rebuild due.** Ground appearing or going away waited out the rest of the rebuild interval, and when the expansion was refused for one already being in flight the retry waited out another whole interval rather than coming back the next frame.

- **Changing an obstacle's Kind while it was blocking shut its ground permanently.** Both obstacle components stored the area they blocked and then read the live `kind` field when releasing it, so blocking as a gate and releasing as a solid decremented the wrong counter and the gate count was never given back. Nothing showed it: the expansion routes through gates by design, so the field stayed open and drew open, and only the mover refused the step. They now remember what they blocked with.

- **Attack range was measured from a target's centre** in MiHordeAnimation, so a crowd pressed against a wall of a large target stood half its width out of range and never swung.

## [0.5.0]

### Upgrading

**`SeparationSettings.updateInterval` is gone and every asset loses its value.** It counted frames, so the same asset gave a different crowd at sixty and at two hundred, and a different one in the editor than in a build. It is now **Updates Per Second**, where zero means every frame, which is what an interval of one meant. A float replacing an int cannot carry the old value across, so an asset that had been tuned comes back at the default. An interval of two at sixty frames is thirty a second.

**`edgeClearance` now defaults to zero.** With Sample Accuracy at one the navmesh's own agent radius inset has already done this, from walls and from ledges alike, so eroding again applies the same clearance twice and takes real ground for it. Existing assets keep whatever they had; new drivers get zero. Raise it only when bodies are wider than the agent the navmesh was baked for. The margin around **runtime obstacles** is a separate setting now, because nothing insets a building.

**The advisor stops reporting the field as most of your frame.** It was measuring expansion latency against frame time, and since 0.3.0 those are unrelated: a fifth of a second four times a second read as eighty percent of the frame while the frame was paying nothing at all.

### Added

- **Gates.** `HordeObstacle` and `HordeMovingObstacle` take a **Kind** of `SOLID` or `GATE`. A grid could say that ground was not there and could not say it was shut, and a crowd whose way is closed has no route at all, so it falls back to walking at the goal and presses against whatever lies between, which is as likely to be a mountain as the door. A gate stays walkable, so the expansion still reaches through it at a great price and every cell behind one keeps a real cost to the goal, and the mover refuses to step into it. The field leads the crowd to the door and the door holds them, so they queue there and pour through when it opens. **Gate Penalty** on the driver is how many cells of detour a shut gate is worth: high so any genuinely open route wins, finite so that when every route is shut the answer is still the nearest door.

- **Height is part of what a step costs.** The bake has sampled a height per cell since the beginning and nothing had ever read it, so the expansion measured a map drawn flat: a hill cost exactly what going round it cost, and two cells at the top and foot of a ledge were a step apart. The cost is now the real distance between the two cell centres, so up one and along one is the hypotenuse of both and nothing has to be tuned for it.

- **Maximum Slope** on the driver, rise over run above which two cells stop being connected, in the expansion and in the mover alike. Height cost prices a climb and cannot forbid one: climbing grows with height while a detour grows with distance, so a single step is always bounded and a five metre cliff will always beat a twenty metre walk. Only a limit refuses it. One is forty five degrees, which is Unity's own navmesh limit. Zero, the default, leaves everything connected and only prices the climb.

- **Obstacle Clearance**, the margin blocked around a runtime obstacle, separated from the bake's edge clearance because they are unrelated. A building is not inset by anything, so without it a body stands with the last part of a cell between it and a wall.

### Changed

- Ground height is read across the four cells around a body rather than out of the one underfoot. One reading per cell makes the ground a staircase, level within a cell and a step at every boundary, and a body being jostled across one of those boundaries pops between two heights every frame.

- The advisor asks whether an expansion arrives inside the interval that asks for one, which is what latency can genuinely be too slow for, and says outright that it costs the frame nothing.

### Fixed

- **Separation got weaker when it was asked to run less often, rather than just running less often.** A solve is handed a delta that caps how much overlap it may undo, and it was handed one frame's worth however many frames it had been since the last one. Raising the interval left each pass correcting a single frame with a third as many passes, so the crowd went soft rather than choppy, which is why the setting looked like it was working.

- **Edge clearance did nothing at the default cell size.** It measured from a cell's centre to the centre of an unwalkable one, which overstates the gap by half a cell, and at a metre of cells with half a metre of clearance compared one against a half and eroded nothing at all. What a body wants clearance from is where the ground stops.

- **A crowd whose every route was blocked never settled and ground itself into a pile.** Stalling was only ever measured inside the branch that follows a route, so bodies with no route at all reported that they still wanted to move, nothing seeded the settle, and the whole crowd pressed into itself for as long as the way stayed shut.

- **Bodies with no route also never wound down.** That branch skipped the speed resolve entirely, which is what drags a body's speed towards what it is actually achieving, so one pressed against something impassable shoved at its full walking pace forever. Settling reached the push it received and not the drive it applied, which is compression rather than a crowd holding its ground.

- **A body caught inside a gate as it shut had no way out.** Gates stay walkable so the expansion can reach through them, which also meant a body standing in one read as perfectly placed: it followed a flow that points onward through the gate and had every step refused. A gate is ground to route towards and never ground to stand on, so a body in one now has no footing and takes the recovery out, exactly as it does when a solid obstacle closes on it.

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
