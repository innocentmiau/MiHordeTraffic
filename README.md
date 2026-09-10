# MiHordeTraffic

Crowd pathfinding for thousands of agents, where the crowd is aware of itself.

Unity's `NavMeshAgent` gives every body its own A\* search and its own local avoidance. That is a good implementation, and it has a ceiling: avoidance is purely local, so an agent steers around the neighbour in front of it but has no idea the bridge it is walking onto is solid. It will march an entire horde into the same chokepoint and then shove.

This replaces both halves. One flow field expansion serves the whole crowd instead of one search per body, and the cost of each cell is measured from how fast bodies are actually getting through it, so a jammed route becomes expensive and a second route opens on its own.

At 5000 bodies, measured on the same crowd, switching technique at runtime:

| | NavMeshAgent | Flow field |
| --- | --- | --- |
| mean frame | 18.68 ms | **12.08 ms** |
| median | 17.88 ms | **11.53 ms** |
| p95 | 23.28 ms | **15.97 ms** |

Editor numbers with collections checks on, and the baseline was running with a saturated repath budget, which
if anything flatters it.

Youtube video showing the project and difference with Unity's way: https://www.youtube.com/watch?v=vscjLy909bU

---

## Features

- **Flow field pathfinding.** One Dijkstra expansion outward from the goal, every body reads the cell it stands in. No per body path, no repath budget, no path recalculation when the target moves.
- **Obstacles at runtime.** A building put down mid game takes the ground under it out of the grid and gives it back when it comes down, without re-baking the navmesh or the grid. Bodies stop walking into it on the very next frame, and route around it on the next expansion.
- **Congestion aware routing.** Each cell is priced by how fast bodies actually cross it. A jammed bridge costs more to walk through, so an alternative wins the moment it is genuinely cheaper, and the whole crowd gets the same answer on the same frame.
- **Reactive separation.** Jobified, Burst compiled, spatial hashed. Replaces the built in local avoidance.
- **Crowd slowing.** Bodies ease off approaching a full cell, which turns a pile up into a queue.
- **Settling.** The stop spreads outwards ring by ring from the bodies that arrived, so a crowd around a target comes to rest instead of churning against itself forever.
- **Freeze and resume.** A frozen body skips everything that costs but goes on occupying the ground it stands on, so the crowd routes around it rather than walking into it.
- **Runtime technique switching.** Flip between the flow field and stock `NavMeshAgent` on the same crowd and read both sets of numbers, so any claim here can be checked rather than believed.
- **Presets instead of raw numbers** for everything where "is 5 a lot?" has no answer.
- **A scene setup tool and a live advisor** that says which setting is costing the most.
- **Time control with substepping**, so fast forwarding a crowd runs the same simulation faster rather than a coarser one.

---

## Install

Package Manager, **Install package from git URL**:

```
https://github.com/innocentmiau/MiHordeTraffic.git
```

Or add it to `Packages/manifest.json` directly:

```json
"com.andreleandrodev.mihordetraffic": "https://github.com/innocentmiau/MiHordeTraffic.git"
```

To work on it locally, point at a checkout instead, relative to the project's `Packages` folder:

```json
"com.andreleandrodev.mihordetraffic": "file:../../MiHordeTraffic"
```

Unity 6000.3 or newer. Pulls in Burst, Collections, Mathematics and AI Navigation.

---

## Setup

**Tools > MiHordeTraffic > Set Up Scene.** It finds the object holding a `SeparationSystem`, creates one if there is none, and adds whatever is missing to that same object. Then assign the driver's **Target**, which is the one thing no tool can guess.

**Tools > MiHordeTraffic > Check Scene** reports anything wrong, and the same check runs automatically on play.

On an entity you need two components:

| Component | What it does |
| --- | --- |
| your own script | health, damage, whatever the game needs |
| `HordeAgent` | separation and flow movement in one |

A `NavMeshAgent` is **not** required. Under flow movement the separation apply mode is `NONE`, which never touches an agent, so one on every body is a component that is simulated and disabled again for no reason. It is added on demand if you switch to an apply mode or a technique that genuinely needs it.

`HordeEntity` is an optional third component, for per body targets or `NavMeshAgent` pathing.

### Freezing

```csharp
agent.Freeze();   // off the roster, costs nothing per frame
agent.Resume();   // back under whatever is driving the horde
```

A frozen body stays on the mover's roster and the movement job returns early for it. Taking it off the roster is the obvious way to make it free, and it is wrong: **the roster is also what the congestion pass counts**, so an unregistered body stops occupying its cell. Nothing would route around it, nothing would slow for it, and the crowd would walk into a body it cannot see and be stopped only by separation shoving it back.

Returning early costs a transform read and a branch, and skips the field lookup, the turn, the speed resolve, the wall checks, the rotation and the transform write, which is the expensive half of any job touching a hierarchy. The body still reports its position, and reports no speed, so it counts towards **how full its cell is** and not towards how fast anything is crossing it. That is exactly the case the occupancy half of congestion was written for: bodies that are not failing to make progress, they are not attempting any.

Separation keeps running by default, because a frozen body that stops being separated stops holding its ground and the crowd closes over the top of it. `separateWhileFrozen` turns that off, which is cheaper and correct for something that should be walked through, such as a corpse.

`HordeEntity.Freeze()` forwards to the agent, so either entry point works.

---

## Presets

Most numbers in this system cannot be read on their own. Nothing about a rise smoothing of five says whether five is a lot, which way to move it, or which other three fields have to move with it to mean anything. So the unitless ones are grouped under a named dropdown, and the ones with real units (metres, degrees, radians per second) are left as numbers because they describe themselves.

A preset **writes its values into the fields it owns** rather than hiding them, so picking one shows you what it chose and the numbers stay there to be compared. Editing a governed field by hand snaps back on the next validate: while a preset owns a field, the preset is what the field says. Set the dropdown to `CUSTOM` and the fields are yours.

### Turn Style

How fast a body swings onto a new direction. Two rates that have to agree: one turns the heading the body walks along, the other turns the model to face it.

| | `headingTurnRate` | `turnSpeed` | Time to reverse | Feels like |
| --- | --- | --- | --- | --- |
| `HEAVY` | 1.5 | 6 | ~2 s | something large, with momentum |
| `NATURAL` | 3.5 | 12 | ~0.9 s | a person changing their mind mid stride |
| `AGILE` | 7 | 18 | ~0.45 s | something quick and light |
| `INSTANT` | 20 | 40 | immediate | removes turning as a behaviour, can look robotic in a crowd |

**Raising it** makes the crowd react visibly sooner when the field changes, at the cost of the whole crowd turning in lockstep when a route flips. **Lowering it** gives bodies somewhere to be mid decision, so a route that flips back before the turn finishes produces a wobble rather than a pirouette.

### Agility

How quickly a body reaches walking pace and how sharply it gives up when blocked. Deceleration is deliberately harsher than acceleration in every preset: running into a crowd should register at once, leaving one can afford to be gradual.

| | `acceleration` | `deceleration` | `stallFraction` | Time to walking pace |
| --- | --- | --- | --- | --- |
| `SLUGGISH` | 3 | 8 | 0.5 | ~1.2 s |
| `NATURAL` | 10 | 25 | 0.5 | ~0.35 s |
| `BRISK` | 25 | 50 | 0.6 | ~0.14 s |
| `INSTANT` | 200 | 200 | 0.9 | none |

`stallFraction` is the share of its commanded speed a body has to be achieving before it keeps pushing. **Lower** means a body gives up sooner and stops shoving, which is less for separation to fight. **Higher** means it keeps trying.

Note `INSTANT` costs you a signal: with no ramp, nothing ever measures as accelerating, which makes a jam harder to distinguish from a standing start.

### Crowd Pressure

How much a body eases off for the cell it is walking into. Thresholds are **fractions of what a cell can physically hold**, computed each frame from the crowd's own radius and the cell size, so they stay meaningful when either changes.

| | `comfortableFill` | `jamFill` | `minimumSpeedFraction` |
| --- | --- | --- | --- |
| `IGNORE` | off | off | off |
| `POLITE` | 0.75 | 1.1 | 0.5 |
| `BALANCED` | 0.5 | 0.95 | 0.25 |
| `STRICT` | 0.3 | 0.8 | 0.1 |

- `comfortableFill` is where slowing **starts**. Raising it means bodies stay at full pace longer and only brake late, which looks confident and packs harder. Lowering it makes them cautious in open ground.
- `jamFill` is what counts as **completely full**. Raising it means they never quite reach the slowest speed.
- `minimumSpeedFraction` is the **slowest** a body may be driven. Lowering it forms tighter queues at the cost of a crowd that can look stalled.

### Congestion Response

How readily the crowd reroutes around itself. **Higher is not simply better.** Routing everyone onto whatever is currently cheapest is what makes a crowd swing between two bridges rather than share them, which traffic assignment has known since Wardrop. The presets raise the ceiling and the reaction speed together and keep the decay slow, because the gap between rising fast and falling slowly is what breaks that cycle.

| | `maximumCost` | `riseSmoothing` | `fallSmoothing` | `progressSmoothing` | `costBlurRadius` |
| --- | --- | --- | --- | --- | --- |
| `OFF` | routes purely on distance | | | | |
| `SUBTLE` | 8 | 3 | 1 | 3 | 2 |
| `BALANCED` | 30 | 5 | 2 | 4 | 1 |
| `AGGRESSIVE` | 60 | 8 | 1 | 6 | 1 |

- `maximumCost` is **the longest detour the field can ever justify**, as a multiple of route length. At 8, a route through a solid queue can look at most eight times its length, and any alternative longer than that stays unattractive however bad the jam gets. Real queues are far worse than eight times walking pace.
- `riseSmoothing` is how fast a jam is believed. **Higher reacts sooner**, and overreacts to a momentary press.
- `fallSmoothing` is how fast a cleared route stops looking expensive. **Keep it below the rise.** A route that becomes attractive again the instant the last body leaves is one the crowd refills immediately.
- `progressSmoothing` is the window over which each body's progress is averaged. **Lower cancels shuffling on the spot harder**, higher notices a real jam sooner.
- `costBlurRadius` averages cost over a neighbourhood. Congestion is a property of a region, not a cell: a one cell detour costs about one cell, so an unsmoothed field bends routes around a single body. **Zero disables it.**

### Refresh Rate

*(on `HordeFlowFieldDriver`)* How eagerly the field is expanded, which is the whole of how quickly the crowd notices its target has moved. `rebuildInterval` is a **ceiling**, not a schedule: the field also expands early once the goal has drifted `rebuildDistance`, with `minimumRebuildInterval` as a floor so a sprinting target cannot demand one every frame.

| | `rebuildInterval` | `rebuildDistance` | `minimumRebuildInterval` |
| --- | --- | --- | --- |
| `LAZY` | 0.5 | 3 | 0.2 |
| `BALANCED` | 0.25 | 1 | 0.05 |
| `RESPONSIVE` | 0.15 | 1 | 0.033 |
| `IMMEDIATE` | 0.05 | 0.5 | 0 |

Cheaper settings are not merely slower to react: the crowd walks confidently at where the target used to be, which reads as the pathfinding being wrong rather than stale. Below one cell of `rebuildDistance` there is nothing to redraw, since every body would read the same direction out of the same cell.

---

## Settling

A crowd converging on one point has no stable arrangement to find. Every body steers at the goal, the ones in front are in the way, separation shoves them apart, and the pack churns forever because neither side can win. Left alone that reads as a permanent low amplitude friction wave through the whole crowd, and it is the same thing a jammed granular material does: the pressure from bodies still arriving travels through the contacts and comes back out.

Four things stop it, and all four are on by default.

**Bodies that are stuck say so.** Settling spreads outwards from bodies that report they are not trying to advance, so something has to report it first. Arriving was the only thing that ever did, which quietly meant the only crowd that could settle was one standing on the goal: a jam at a bridge or a doorway contained no body that had arrived, so nothing seeded, nothing spread, and every body in it drove at full speed into the back of the one in front for as long as the jam lasted.

A body that keeps wanting to move and keeps not moving is the second and far more common reason. `stallSettleDelay` is how many seconds it presses before accepting it is not getting through. Progress is measured along its heading against the speed it would have made on open ground, so being shoved sideways by the crowd does not count and a body pushed backwards reads as worse than standing still.

**The stop spreads.** A body settles when enough of the neighbours it is already overlapping are both closer to its goal than it is and have settled themselves. The ring that genuinely arrived settles first, the ring behind it sees settled neighbours ahead and settles, and it moves outwards a ring per tick. This is the approach RTS games have used for decades, and it carries the same information a carved navmesh would without touching the navmesh.

This is measured against each body's own goal, so `FLOW_FIELD` publishes the driver's target to separation every frame. Nothing has to be wired up for it.

**Settled bodies stop driving, not just stop shoving.** `settledDriveScale` is how hard a body still walks once it has settled, as a fraction of its speed. Set it to `1` and you get the old behaviour: a settled crowd is a pile of bodies all pressing inwards while separation is asked to gently shove them apart, which is a damper bolted to the wrong end of a pressure source. `0` is worse in a different way, because a queue drains from the front and a body that has stopped completely cannot take the room the body ahead of it just vacated. The default of `.25` lets a queue shuffle forward while killing the churn.

A settled body still reports the speed it *would* have managed on open ground, so congestion still prices its cell as solid and bodies further back route around rather than joining the back of it.

**Overlaps below a threshold are left alone.** `overlapTolerance` is how deep two bodies may overlap before anything pushes them apart, as a fraction of their combined radii. At zero, a sub millimetre overlap still generates a full strength push, resolving it creates the next one, and the crowd never reaches a state where nothing needs correcting. Every rigid body solver allows a little penetration for this reason. Raising it packs tighter and goes still sooner; lowering it keeps bodies visibly apart at the cost of more buzz.

It is subtracted from the overlap depth and not from the range a neighbour is found at, so changing it does not quietly loosen the settle spread as well.

There is no knob for how slow counts as stuck. It is derived from `settledDriveScale` and `minimumSpeedFraction`, because the safe range for it is entirely decided by those two: giving up scales a body's drive down, and a threshold above the reduced pace would mean nothing that gave up could ever take it back. A crowd that froze the first time it touched itself is not a setting worth offering.

| Field | Default | Raise it to | Lower it to |
| --- | --- | --- | --- |
| `stallSettleDelay` | `.5` s | Let bodies fight for their route longer before giving up | Calm a jam sooner, at the cost of a crowd that gives up while squeezing past itself. Zero switches it off, and jams away from the goal never settle |
| `settledDriveScale` | `.25` | Keep queues moving, at the cost of more pressure on the crowd ahead | Kill churn harder, at the cost of queues that drain slowly. Zero makes anything that gives up a permanent statue |
| `overlapTolerance` | `.04` | Pack tighter and settle sooner, at the cost of visible interpenetration | Keep bodies apart, at the cost of a crowd that never fully stops |
| `settledPushScale` | `.15` | Let settled bodies ooze apart faster | Hold formation harder. Zero leaves bodies that settled while overlapping stuck inside each other |
| `blockingNeighbours` | `3` | Pack tighter, churn more | Spread the stop further out, pack looser |
| `settledHoldTicks` | `8` | Steadier at the edge of a crowd, slower to set off again | React sooner, at the cost of flickering on the boundary |

---

## Obstacles

Put `HordeObstacle` on anything the crowd has to walk around. It takes the cells under it out of the grid while it is enabled and gives them back when it is not, so a building can go up and come down without re-baking anything. It has no `Update` and no per frame cost at all.

Blocking is an overlay on what the bake found rather than an edit of it, held as a count per cell. Two structures sharing a cell, or one landing inside another's clearance margin, both have to be gone before the ground comes back. Removing a building can never open ground the bake never offered.

The footprint is taken from a `Collider` if the size is left at zero, and it is grown by `edgeClearance` the same way the bake erodes around walls, so **the blocked area is larger than the object**. On a 2.5 m grid a 2 m tower can take a 3x3 block of cells. Turn on the flow field gizmos to see what was actually blocked, which is a common source of "why is it behaving oddly there".

Two speeds of effect, and they are worth telling apart:

- **Walking into it stops immediately.** Every step is tested against walkability, so from the frame the cells go, nobody enters.
- **Routing around it waits for the next expansion**, which is up to `rebuildInterval` plus the expansion itself. Until then bodies still aim through it, are refused at its face, and slide along it.

### Moving obstacles

`HordeMovingObstacle` is a separate component on purpose, so a wall that will never move keeps costing nothing. It rechecks its footprint a few times a second rather than every frame, and skips entirely when it still covers the same cells, since a grid cannot tell apart two positions that round to the same cell rectangle.

What a move costs is not the cell writes. It is that every move makes the field out of date, and an expansion on a large grid is tens of milliseconds of worker time. A cart at sixty updates a second would ask for sixty expansions a second and get nothing for it, because the crowd cannot react faster than the rebuild interval anyway.

So **Update Routing is off by default**. Bodies still cannot walk into it; they are simply not steered around it, which reads correctly for anything small enough that sliding along it gets a body past. Turn it on for something large enough that getting past it needs a route.

### Gates

A gate that seals the only way through works, and it is worth knowing what the crowd does. Everything behind it becomes unreachable, so bodies aim at the goal, are refused at the gate, and queue against it. The stall detector then notices nobody is making progress and settling spreads back through the queue so they stop shoving.

What they will not do is gather **at the gate** specifically. The field cannot express "closed" as opposed to "not there", so there is no route leading to the door and bodies press against whatever wall lies between them and the goal instead.

## Spawning

```csharp
if (HordeSpawn.TryFind(wanted, 5f, out Vector3 spawn))
    pool.Spawn(spawn, rotation);
```

Answers where a body can actually stand: on the grid, reachable, and not already packed, nearest first, within the distance given. It returns a point on the ground, scattered inside its cell so a wave does not stack on one spot.

Sampling the navmesh yourself does not answer this. The bake erodes for clearance, so ground the navmesh is happy with is not always ground this system will walk on, and a body put down on a cell the grid rejects starts off the described map and visibly wanders out to real ground before it sets off.

Reachability is part of the question rather than an extra: a cell can be walkable and cut off, and a body spawned on one has nowhere to go from the moment it arrives.

An overload takes how full a cell may already be, as a fraction of what it holds at rest. That reads the smoothed density, which lags by design, so several spawns inside one frame all see it as it was before any of them landed and can pick the same cell. The scatter keeps them from stacking exactly and separation sorts out the rest, but it is not a reservation.

A spawned body needs `HordeAgent` and nothing else. It registers itself, and no `NavMeshAgent` is required under flow movement.

## Numbers with units

These stay as plain numbers because they describe themselves and depend on your map.

| Field | Unit | Notes |
| --- | --- | --- |
| `cellSize` | metres | The single biggest cost lever. Doubling it quarters the cell count, the rebuild time and the per frame congestion cost |
| `edgeClearance` | metres | How far a cell must be from unwalkable ground to count as walkable |
| `arriveRadius` | metres | Where a body counts as arrived and stops being driven |
| `arriveTaper` | metres | The distance over which it eases to a stop, rather than switching off at a line |
| `separationRadius` | metres | Room a body wants around itself. Kept separate from the navmesh radius, which is also wall clearance |
| `facingTolerance` / `facingLimit` | degrees | Only used when `requireFacing` is on |
| `overlapTolerance` | fraction of combined radii | Not metres, so it stays the same proportion of a body whether the crowd is rats or siege engines |

---

## Diagnostics

**`HordeFlowAdvisor`** samples every few seconds and logs only when something is worth changing, naming the field that makes it cheaper. It never suggests raising anything: a tool that tells you a setting could be higher talks constantly and gets switched off.

It catches rebuilds taking too large a share of frame time, a saturated repath budget, substepping at normal speed (which means bodies cross more than half a cell per frame, and the wall check cannot see through that), a grid that is mostly not walkable, a grid large enough that congestion dominates, and a crowd dense enough that the grid can no longer tell one part of it from another.

**`HordePathScheduler`** prints a session report on quit with per technique frame times, so the two techniques can be compared on the same crowd. Under those it prints a `phases` table: milliseconds of main thread time per frame in each stage, read off the profiler markers the package already carries.

Frames longer than a second are counted separately and left out of the distribution. Pausing the editor, dragging a window and a domain reload all arrive as one sample worth tens of seconds, and the mean is accumulated exactly rather than out of the percentile ring, so a single one of them puts the average of a two thousand frame run an order of magnitude above the median.

The rows overlap on purpose. `Pathing.Schedule` contains the field rebuild, and the `Move` and `Complete` rows are waits on jobs the same frame scheduled, so `counted` is a ceiling rather than a sum, and a low number there means the horde is not what is slow.

### Read the renderer before you tune the crowd

A crowd that has settled is a crowd standing in one place, and a few thousand characters in one place is a very different thing to draw than the same characters spread over a hundred metres. Everything is in frustum, everything overlaps, everything casts a shadow into the same few metres. A change that improves how the crowd behaves can make the frame slower without a single line of it being slower, and the number that gives it away is the gap between `counted` and the frame time.

The case this was written from: five thousand characters went from eleven milliseconds a frame to thirty on a change to their animation, and `counted` was two and a half of the thirty. The prefab had no `LODGroup`, so every one of them drew its full geometry at every distance, around a hundred and fifty million vertices a frame. Adding one and letting them fall to a cheaper mesh took it to fifteen million and the frame back to eleven.

Simulation cost per body is flat. Rendering cost per body is not: it depends on where the body is, what is in front of it and how much of the screen it covers, and all three of those are things the crowd system changes on purpose. Check `counted` against the frame first, and if it is a small fraction, the crowd is not the thing to tune.

**`HordeFlowFieldGizmos`** draws the field. Cost is drawn on a log scale, because cost is a ratio: half pace is a cost of 2, and on a linear scale to 30 that is three percent of the way to red and indistinguishable from open ground, when half pace is exactly where a detour starts being worth taking.

It is a separate component so it can be removed. Drawing tens of thousands of cells is expensive enough to change the frame rate you are measuring at.

**Substepping** is what makes fast forwarding honest, and it lives in the mover rather than in any tool. `Time.timeScale` alone is not enough: the step each frame integrates grows with it, and past a point a longer step is not the same simulation run faster. Walkability is tested where a step ends, so a body covering more than a cell can pass through a wall nothing sampled. Long steps are cut into ordinary sized ones, so raising the time scale buys more simulation per rendered frame rather than a coarser simulation, at proportionally more CPU. Set `Time.timeScale` from anywhere and it just works.

A ready made slider component is not shipped here, because the only interesting version of one reads the keyboard and that would make the Input System a hard dependency of a crowd package. It is a dozen lines: set `Time.timeScale`, scale `Time.fixedDeltaTime` with it, and put it back to 1 in `OnDisable` so a session that ended at eight times speed does not leave the next one starting there.

---

### The worst frames, not the average

An average cannot show you a spike. One frame of a tenth of a second across a couple of thousand moves every row of the phases table by four hundredths of a millisecond, so the report you would go to looking for it is the one report guaranteed not to show it.

**Worst Frames Tracked** on the scheduler keeps the frames that cost *this package* the most, with the phase breakdown of each, and prints them under the averages. Ranked by the package's own cost rather than by frame time on purpose: a run holding a few editor stalls or a collection would otherwise report those and nothing else, and the frames where the crowd was genuinely slow would never make the list. The frame time is carried on every entry regardless. Four is enough to tell a recurring shape from a one off: four spikes with the same row lit up is a cause, four with four different rows is the editor, or the GPU, or the collector, and the answer is that it is not this. It says so outright when the package accounted for less than half the frame.

It records nothing unless a frame turns out to be among the slowest already kept, which almost every frame fails, and it allocates nothing at all, since a diagnostic that produces garbage lands the collection inside the frames it is measuring.

### What the congestion pass is walking

The report also carries **congestion N cells tracked, M at peak, against B bodies**. The pass runs over the cells the crowd has been in recently, not over the crowd, so a cell joins when a body arrives and leaves once its density has decayed back to empty. What that really counts is bodies multiplied by how long a cell takes to forget them.

Far above the body count means the trail behind the crowd is the cost rather than the crowd. Watch the peak rather than the current figure, since the report prints at quit when the crowd may have settled, and a jam that has cleared holds its cells for as long as its cost takes to come back down.

## Roadmap

Things that are missing, why they are missing, and what it would take. Ordered by how much they open up.

### Layered grid, for worlds with surfaces above each other

**Needed when** a level has walkable ground stacked in the same place: a cave under a hillside, a bridge over a road, two floors of a building.

**Why not yet.** The grid is two dimensional by construction. Cells are indexed `z * Width + x`, and each one holds a single height sampled at bake time, so a column of the world resolves to exactly one surface. The bake calls `NavMesh.SamplePosition`, which returns the nearest surface to the cell centre, so which of two stacked floors survives depends on where the bounds centre happened to land. `SeparationPlane.FULL_3D` does not help: it is a separation setting governing the push's Y component, unrelated to the field.

**What it takes.** A cell becomes `(x, z, layer)` rather than `(x, z)`, the bake samples every surface in a column instead of the nearest one, and the expansion needs links between layers where they actually connect, since two floors overlapping in XZ are usually not reachable from each other. Bodies then need a cheap answer to which layer they are standing on. This touches `HordeGridInfo`, which every job takes by value, so it is the most invasive change on this list and the one most worth doing.

### 2D on the XY plane

**Needed for** a genuinely 2D game, sprites on XY, of the top down survivors kind.

**Why not yet.** Separation already handles it: `SeparationPlane.XY` masks the Z axis and the whole solve is plane agnostic. The field is not. Three places assume XZ: `HordeGridInfo.CellOf` swizzles `position.xz`, the move job composes velocity as `float3(heading.x, 0f, heading.y)`, and the bake reads walkability from `NavMesh.SamplePosition`.

**What it takes.** The first two are swizzling, and `HordeGridInfo` taking a plane the way separation does would cover them. The bake is the real work, and it depends on the project: with **NavMeshPlus** in the project a 2D navmesh already exists and `SamplePosition` works against it, so that path is close to free. Without it, walkability has to come from somewhere else, a tilemap or a collider overlap test per cell, which is a different bake but not a difficult one.

### Multiple goals

**Needed when** not every body is walking to the same place: two factions, several objectives, a formation moving to different slots.

**Why not yet.** One field expands from one goal, which is the whole reason it is cheap. `HordeFlowFieldDriver` is a singleton with a single `Target`.

**What it takes.** A field per goal and bodies choosing which to read. The expansion cost multiplies by the number of goals, so it is only worth it while goals are few and crowds are large, which is the usual case. Groups of bodies sharing a goal is the natural unit, mirroring how separation already groups by settings asset.

### Incremental expansion

**Needed when** the field rebuild starts showing in the profiler, which is a large grid or a fast target.

**Why not yet.** Every expansion is a full Dijkstra over the grid, however little the goal moved. It is currently around 1.3 ms on a 10k cell grid, which is affordable at four a second and is not at twenty.

**What it takes.** Re-expanding only the region near the change rather than the whole field. The literature is D\* Lite and LPA\*, both of which repair a search rather than repeat it. Worth measuring before building: on a grid this size the full expansion may simply be cheap enough.

### Dynamic obstacles

**Needed when** something closes a route at runtime: a door, a collapsing bridge, a player built wall.

**Why not yet.** Walkability is baked once. Nothing rebakes it, so a closed door is still walkable ground and the crowd walks through where it used to be.

**What it takes.** A way to mark cells unwalkable at runtime and re-expand. The expansion already runs regularly, so the field would recover on its own within a rebuild interval, which makes this mostly a matter of an API to stamp a region and a bake that can be run on part of the grid.

### Flow field cost from terrain

**Needed when** ground should be slower without being impassable: mud, water, stairs.

**Why not yet.** Cost is entirely congestion. A cell's baked cost is always one.

**What it takes.** Little. The expansion already multiplies by a per cell cost, so this is a second cost channel sampled at bake time from navmesh areas and multiplied in alongside the congestion one.

### Development build profiling

**Not a feature, but it gates the rest.** Every number quoted here is from the editor, which carries its own loop plus `ENABLE_UNITY_COLLECTIONS_CHECKS` on every native container access, and that is not cheap in jobs that touch hash maps per body. The ratios may hold or may not. Nothing here should be optimised further before that measurement exists.

---

## Limitations

- **One walkable surface per column.** See the roadmap above.
- **XZ only.** Separation supports `XY` and `FULL_3D` and works in 2D today; the field does not.
- **One goal at a time.** The whole crowd expands from a single target.
- **Baked walkability.** Runtime changes to the level are not seen until the grid is rebaked.
- **`NavMeshQuery` A\* is not available.** `UnityEngine.Experimental.AI` was deprecated without a replacement in Unity 6, which is why this uses a grid.

---

## Learning from this

[Documentation.md](Documentation.md) covers how each part works and why it is built that way, with the papers and articles behind each technique. It is written to be read by someone who wants to build one of these rather than only use it.