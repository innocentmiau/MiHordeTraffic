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
<center>
Youtube video showing the project and difference with Unity's way.

[![Watch the showcase of this project](https://img.youtube.com/vi/vscjLy909bU/maxresdefault.jpg)](https://www.youtube.com/watch?v=vscjLy909bU)
<i>https://www.youtube.com/watch?v=vscjLy909bU</i></center>

---

## Features

- **Flow field pathfinding.** One Dijkstra expansion outward from the goal, every body reads the cell it stands in. No per body path, no repath budget, no path recalculation when the target moves.
- **Congestion aware routing.** Each cell is priced by how fast bodies actually cross it. A jammed bridge costs more to walk through, so an alternative wins the moment it is genuinely cheaper, and the whole crowd gets the same answer on the same frame.
- **Reactive separation.** Jobified, Burst compiled, spatial hashed. Replaces the built in local avoidance.
- **Crowd slowing.** Bodies ease off approaching a full cell, which turns a pile up into a queue.
- **Settling.** Bodies that have arrived stop pushing, so a crowd around a target settles instead of churning.
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

---

## Diagnostics

**`HordeFlowAdvisor`** samples every few seconds and logs only when something is worth changing, naming the field that makes it cheaper. It never suggests raising anything: a tool that tells you a setting could be higher talks constantly and gets switched off.

It catches rebuilds taking too large a share of frame time, a saturated repath budget, substepping at normal speed (which means bodies cross more than half a cell per frame, and the wall check cannot see through that), a grid that is mostly not walkable, a grid large enough that congestion dominates, and a crowd dense enough that the grid can no longer tell one part of it from another.

**`HordePathScheduler`** prints a session report on quit with per technique frame times, so the two techniques can be compared on the same crowd.

**`HordeFlowFieldGizmos`** draws the field. Cost is drawn on a log scale, because cost is a ratio: half pace is a cost of 2, and on a linear scale to 30 that is three percent of the way to red and indistinguishable from open ground, when half pace is exactly where a detour starts being worth taking.

It is a separate component so it can be removed. Drawing tens of thousands of cells is expensive enough to change the frame rate you are measuring at.

**Substepping** is what makes fast forwarding honest, and it lives in the mover rather than in any tool. `Time.timeScale` alone is not enough: the step each frame integrates grows with it, and past a point a longer step is not the same simulation run faster. Walkability is tested where a step ends, so a body covering more than a cell can pass through a wall nothing sampled. Long steps are cut into ordinary sized ones, so raising the time scale buys more simulation per rendered frame rather than a coarser simulation, at proportionally more CPU. Set `Time.timeScale` from anywhere and it just works.

A ready made slider component is not shipped here, because the only interesting version of one reads the keyboard and that would make the Input System a hard dependency of a crowd package. It is a dozen lines: set `Time.timeScale`, scale `Time.fixedDeltaTime` with it, and put it back to 1 in `OnDisable` so a session that ended at eight times speed does not leave the next one starting there.

---

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