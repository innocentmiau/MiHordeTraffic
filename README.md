# MiHordeTraffic

Crowd pathfinding for thousands of agents, where the crowd is aware of itself.

`NavMeshAgent` gives every body its own A\* search and its own local avoidance. Avoidance is local, so a body steers around its neighbour but has no idea the bridge ahead is solid, and the whole horde marches into the same chokepoint and shoves.

This replaces both halves. One flow field expansion serves the whole crowd, and each cell is priced by how fast bodies actually cross it, so a jammed route becomes expensive and a second one opens on its own.

At 5000 bodies, same crowd, switching technique at runtime:

| | NavMeshAgent | Flow field |
| --- | --- | --- |
| mean frame | 18.68 ms | **12.08 ms** |
| median | 17.88 ms | **11.53 ms** |
| p95 | 23.28 ms | **15.97 ms** |

Editor numbers with collections checks on.

Video: https://www.youtube.com/watch?v=vscjLy909bU

---

## Features

- **Flow field pathfinding.** One Dijkstra expansion from the goal; every body reads the cell it stands in.
- **Congestion aware routing.** Jammed routes get expensive and the crowd reroutes itself.
- **Reactive separation.** Jobified, Burst, spatial hashed. Replaces built in local avoidance.
- **Settling.** A crowd around a target comes to rest instead of churning.
- **Runtime obstacles and gates.** Buildings block ground without re-baking; gates hold a queue until they open.
- **Terrain height.** Hills cost more than flat ground; ledges can be refused.
- **Freeze and resume.** A frozen body costs nothing and still occupies its ground.
- **Runtime technique switching**, so you can measure this against stock `NavMeshAgent` on the same crowd.
- **Presets** for everything where "is 5 a lot?" has no answer, plus a scene setup tool and a live advisor.

---

## Install

Package Manager, **Install package from git URL**:

```
https://github.com/innocentmiau/MiHordeTraffic.git
```

Unity 6000.3 or newer. Pulls in Burst, Collections, Mathematics and AI Navigation.

---

## Setup

1. Bake a NavMesh.
2. **Tools > MiHordeTraffic > Set Up Scene** — adds everything missing to one manager object.
3. Assign the driver's **Target**.
4. Put **`HordeAgent`** on your entity prefab. That is the only component it needs.

**Tools > MiHordeTraffic > Check Scene** reports anything wrong, and runs automatically on play.

A `NavMeshAgent` is **not** required. `HordeEntity` is optional, for `NavMeshAgent` pathing instead of the field.

```csharp
HordeFlowFieldDriver.Instance.SetTarget(transform);   // move the whole crowd's goal
agent.Freeze();                                       // stop one, keep its place in the crowd
agent.Resume();
agent.SetSpeed(4f);
```

---

## Spawning

```csharp
if (HordeSpawn.TryFind(wanted, 5f, out Vector3 spawn))
    pool.Spawn(spawn, rotation);
```

Returns the nearest cell that is on the grid, reachable, not gated and not already packed, as a point on the ground scattered inside its cell. Sampling the NavMesh yourself does not answer this — the grid rejects ground the NavMesh accepts.

An overload takes how full a cell may already be, as a fraction of what it holds at rest.

---

## Obstacles

Put **`HordeObstacle`** on a building. It takes the ground under it out of the grid while enabled and gives it back when disabled. No `Update`, no per frame cost. Size comes from a `Collider` if you leave **Size** at zero.

Two speeds of effect: bodies stop walking into it **immediately**, and route around it on the **next expansion** (up to `rebuildInterval`).

**`HordeMovingObstacle`** is a separate component for things that move. It rechecks a few times a second and, by default, asks for no expansion at all — bodies still cannot enter it, they are just not steered around it. Turn **Update Routing** on for something large enough that sliding along it will not get a body past.

### Gates

Set **Kind** to `GATE` and the ground stays routable but not walkable. The crowd paths *to* the gate and queues against it, then pours through when it opens.

Without this a closed route is simply no route, so the crowd steers straight at the goal and piles against whatever is in the way — often a wall nowhere near the door.

**Gate Penalty** on the driver is how many cells of detour a shut gate is worth. High enough that an open route always wins, finite so that when everything is shut they still queue at the nearest door.

---

## Height

A step costs the real distance between two cell centres, so a hill is dearer than walking around it. Nothing to configure.

**Cost cannot forbid a climb, only price one** — a 5 m cliff costs about 5 and will always beat a 20 m detour. **Maximum Slope** is the refusal: rise over run, enforced by both the routing and the movement. `1` is 45°, matching Unity's NavMesh Max Slope. `0` (default) connects everything and only prices the climb.

It is off by default because turning it on can disconnect ground an existing map relied on.

---

## Presets

Every preset writes its values into the fields it owns, so you can see what it chose. `CUSTOM` hands them back.

<details>
<summary><b>Turn Style</b> — how fast bodies turn onto a new direction</summary>

| | `headingTurnRate` | `turnSpeed` | Time to reverse | Feels like |
| --- | --- | --- | --- | --- |
| `HEAVY` | 1.5 | 6 | ~2 s | something large, with momentum |
| `NATURAL` | 3.5 | 12 | ~0.9 s | a person changing their mind mid stride |
| `AGILE` | 7 | 18 | ~0.45 s | something quick and light |
| `INSTANT` | 20 | 40 | immediate | can look robotic in a crowd |

</details>

<details>
<summary><b>Agility</b> — how quickly bodies reach walking pace and give up when blocked</summary>

| | `acceleration` | `deceleration` | `stallFraction` | Time to walking pace |
| --- | --- | --- | --- | --- |
| `SLUGGISH` | 3 | 8 | 0.5 | ~1.2 s |
| `NATURAL` | 10 | 25 | 0.5 | ~0.35 s |
| `BRISK` | 25 | 50 | 0.6 | ~0.14 s |
| `INSTANT` | 200 | 200 | 0.9 | none |

</details>

<details>
<summary><b>Crowd Pressure</b> — how much bodies ease off approaching a full cell</summary>

| | `comfortableFill` | `jamFill` | `minimumSpeedFraction` |
| --- | --- | --- | --- |
| `IGNORE` | off | off | off |
| `POLITE` | 0.75 | 1.1 | 0.5 |
| `BALANCED` | 0.5 | 0.95 | 0.25 |
| `STRICT` | 0.3 | 0.8 | 0.1 |

</details>

<details>
<summary><b>Congestion Response</b> — how far the crowd will detour around itself</summary>

| | `maximumCost` | `riseSmoothing` | `fallSmoothing` | `progressSmoothing` | `costBlurRadius` |
| --- | --- | --- | --- | --- | --- |
| `OFF` | routes purely on distance | | | | |
| `SUBTLE` | 8 | 3 | 1 | 3 | 2 |
| `BALANCED` | 30 | 5 | 2 | 4 | 1 |
| `AGGRESSIVE` | 60 | 8 | 1 | 6 | 1 |

</details>

<details>
<summary><b>Refresh Rate</b> — how eagerly the field is rebuilt when the target moves</summary>

| | `rebuildInterval` | `rebuildDistance` | `minimumRebuildInterval` |
| --- | --- | --- | --- |
| `LAZY` | 0.5 | 3 | 0.2 |
| `BALANCED` | 0.25 | 1 | 0.05 |
| `RESPONSIVE` | 0.15 | 1 | 0.033 |
| `IMMEDIATE` | 0.05 | 0.5 | 0 |

</details>

---

## Settings reference

<details>
<summary><b>Every field, with its unit</b></summary>

| Field | Unit | What it is |
| --- | --- | --- |
| `cellSize` | metres | The biggest cost lever. Doubling it quarters cell count, rebuild time and per frame congestion cost |
| `sampleAccuracy` | 0 to 1 | How closely a cell must sit on the NavMesh. 1 is on it or not; lower lets bodies clip walls |
| `edgeClearance` | metres | Bake erosion. Usually 0, since the NavMesh agent radius inset already did it |
| `obstacleClearance` | metres | Margin blocked around a runtime obstacle. Roughly a body radius |
| `maximumSlope` | rise / run | Above this two cells stop being connected. 1 is 45°, 0 is no limit |
| `gatePenalty` | cells | How many cells of detour a shut gate is worth |
| `arriveRadius` | metres | Where a body counts as arrived |
| `arriveTaper` | metres | The distance it eases to a stop over |
| `separationRadius` | metres | Room a body wants around itself |
| `overlapTolerance` | fraction of radii | How deep bodies may overlap before anything pushes. Zero never fully settles |
| `updatesPerSecond` | per second | How often separation solves. 0 is every frame |

</details>

<details>
<summary><b>Settling</b> — what to change when a crowd churns or freezes</summary>


| Field | Default | Raise it to | Lower it to |
| --- | --- | --- | --- |
| `stallSettleDelay` | `.5` s | Let bodies fight for their route longer | Calm a jam sooner. Zero switches it off |
| `settledDriveScale` | `.25` | Keep queues moving | Kill churn harder. Zero makes anything that gives up permanent |
| `settledPushScale` | `.15` | Let settled bodies ooze apart faster | Hold formation harder |
| `blockingNeighbours` | `3` | Pack tighter, churn more | Spread the stop further out |
| `settledHoldTicks` | `8` | Steadier at a crowd's edge | React sooner, flicker more |

</details>

---

## Diagnostics

Quitting play mode logs a session report: frame time distribution, field size, rebuild count and latency, and a per phase breakdown of what the package cost the main thread.

**Read the renderer before you tune the crowd.** Simulation cost per body is flat; rendering cost is not, and a crowd that settles is a crowd piled into one place. A test reading 30 ms a frame at 5000 bodies had 2.4 ms of this package in it — the rest was a prefab missing its `LODGroup`.

Three rows worth knowing:

- **`counted`** is what the package cost the frame. Rows overlap, so it is a ceiling, not a sum.
- **`congestion N tracked, M at peak`** is how many cells the congestion pass walks. Far above the body count means the trail behind the crowd is the cost.
- **worst frames** keeps the frames that cost *this package* the most, with each one's breakdown, and says when a spike was mostly not this package. Set by **Worst Frames Tracked** on the scheduler.

Markers are stripped from non development builds, and the report says so rather than reporting zero.

**Tools > MiHordeTraffic > Check Scene** and the live advisor catch the common misconfigurations.

---

## Limitations

- **One walkable surface per column.** No stacked floors or bridges over ground.
- **XZ only.** Separation supports `XY` and `FULL_3D`; the field does not.
- **One goal at a time.** The whole crowd expands from a single target.
- **Baked walkability.** Level geometry changes need a re-bake; runtime obstacles do not.

---

## Roadmap

- Layered grid, for surfaces above each other
- 2D on the XY plane
- Multiple goals, one field each, sharing the grid
- Incremental expansion instead of full rebuilds
- Development build profiling

---

## Licence

MIT. See `LICENSE.md`.
