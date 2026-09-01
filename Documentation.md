# How MiHordeTraffic works

Written for someone who wants to build one of these, not only use one. Each section says what the problem is, what was tried, what actually worked, and where to read more.

---

## 1. Why not one path per agent

Unity's `NavMeshAgent` runs A\* per agent on worker threads, and it is a good implementation. Profiling a crowd of 2000 showed the path search itself at around 0.04 ms. The 1.63 ms sitting under `AIUpdate` was almost entirely transform synchronisation, caused by our own `agent.Move` calls crossing the managed/native boundary once per body per frame.

That reframed the problem. The goal was never to beat Unity's A\*, it was to stop crossing that boundary thousands of times a frame. Everything below follows from that.

- [Unity: NavMeshAgent](https://docs.unity3d.com/Manual/nav-CreateNavMeshAgent.html)
- [Unity: C# Job System](https://docs.unity3d.com/Manual/JobSystem.html) and
  [Burst](https://docs.unity3d.com/Packages/com.unity.burst@latest)

---

## 2. Flow fields

A flow field inverts the question. Instead of asking "how do I get from here to the goal" once per agent, it asks "from every cell, which way is the goal" once for everybody. Cost is `O(cells)` rather than `O(agents x pathlength)`, so it gets cheaper per agent the more agents there are.

Three passes:

1. **Bake.** Sample the navmesh once per cell to decide what is walkable, and record the ground height. `GridFlowField.Bake`.
2. **Integration.** Dijkstra outward from the goal, filling every cell with the cost of reaching it. `GridFlowFieldBuildJob`, a binary heap over a `NativeList`.
3. **Direction.** Turn that cost surface into one vector per cell. `GridFlowDirectionJob`.

### The bake tolerance bug, worth knowing about

`NavMesh.SamplePosition` returns the nearest point on a surface **however far away it is**. Accepting any hit within a full cell invented a metre of walkable ground along every edge in the level, and bodies walked it with half of themselves over the drop. The test has to be that the sample landed *inside the cell*:

```csharp
if (math.distancesq(((float3)hit.position).xz, centre.xz) > halfCell * halfCell) continue;
```

Unity already insets the navmesh by the agent radius when it bakes. Anything looser throws that inset away.

### Gradient rather than nearest neighbour

Stepping to the cheapest of eight neighbours locks the whole crowd onto eight headings, which reads as robotic however smoothly bodies turn: the quantisation is in the field, not in the turning, so no amount of interpolating rotation removes it. Following the slope of the cost surface costs the same and gives any angle.

Blocked neighbours contribute **the centre cell's own cost** rather than a large one. Substituting a large value makes every wall a hill the field pushes bodies down, so crowds peel away from walls they should be walking along. Contributing the centre's cost leaves the slope one sided and sends bodies parallel to the wall.

- [Elijah Emerson, *Crowd Pathfinding and Steering Using Flow Field Tiles*, Game AI Pro](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf)
- [Red Blob Games: Introduction to A\*](https://www.redblobgames.com/pathfinding/a-star/introduction.html) - the clearest explanation of Dijkstra, A\* and why a flow field is the same machinery run backwards
- [Red Blob Games: Grids and graphs](https://www.redblobgames.com/pathfinding/grids/graphs.html)

---

## 3. Congestion aware routing

This is the part that makes the crowd aware of itself, and most of the difficulty.

### Price cells by measured speed, not by headcount

Counting bodies per cell is the obvious version and it is wrong in both directions: a wide corridor packed with bodies can flow freely, and a narrow one with a handful in it can be solid. What matters is how fast bodies actually get through, which is the **fundamental diagram** of traffic flow: speed falls as density rises, and the shape of that curve is a property of the geometry, not of the count.

The expansion multiplies each step by the cell's cost, so a bridge crossed at a third of walking pace costs three times as much to cross. Nothing then has to *notice* a jam or *decide* a detour is worthwhile. Comparing routes is what Dijkstra already does, for every cell at once, so a second bridge wins the moment it is genuinely cheaper and every body gets the same answer on the same frame.

### Measure progress along the route, not distance travelled

A crammed crowd is being shoved by separation at up to the maximum push speed. Bodies churning in place cover plenty of ground and would measure as flowing freely. Projecting movement onto the direction the cell says to walk counts only what got the body closer to where it is going.

### Oscillation, and why the smoothing is asymmetric

Routing everyone onto whatever is cheapest simply moves the jam: the empty bridge fills, becomes expensive, and the crowd swings back. This is **Wardrop equilibrium** and it is the central problem in traffic assignment.

The fix here is that cost **rises fast and falls slowly**. A route that has just been abandoned looks clear within a frame of the last body leaving it, and a field that believes that immediately sends the crowd straight back. Making a route stay clear before it becomes attractive again is the difference between two routes sharing a crowd and one being swapped for the other forever.

- [Wardrop's principles](https://en.wikipedia.org/wiki/John_Glen_Wardrop)
- [Braess's paradox](https://en.wikipedia.org/wiki/Braess%27s_paradox) - adding a route can make everyone slower
- [Fundamental diagram of traffic flow](https://en.wikipedia.org/wiki/Fundamental_diagram_of_traffic_flow)
- [Treuille, Cooper, Popović, *Continuum Crowds*, SIGGRAPH 2006](https://grail.cs.washington.edu/projects/crowd-flows/) - the canonical treatment of crowds as a flow with density dependent speed

### Four bugs this went through, each of which looked like a tuning problem

**Deliberate slowdowns measured as congestion.** Cost was `freeSpeed / measured`. But bodies slow for reasons that have nothing to do with crowding: the acceleration ramp from a standing start, and the arrival taper near the goal. A body that just set off has near zero speed, so its cell priced itself at the ceiling for the thirdof a second the ramp takes. That is literally "an agent steps on a cell and it turns red". Fixed by measuring each body against **what it could have managed there on open ground**, ramped by acceleration but never wound down by being obstructed.

**The stall feedback loop.** The first version of that reference used the body's current speed, which the stall logic deliberately drags *down* toward whatever the body is achieving. So a stuck body reported that it wanted to crawl and managed to crawl, cost 1, jam invisible. The reference has to be a separate ramp that only falls when the body genuinely wants less.

**Shuffling read as movement.** Per frame progress was floored at zero so a body shoved backwards could not make its cell look faster. But flooring each frame counts every forward twitch and discards every backward one, so a body oscillating in place measured as walking at half pace. Fixed by keeping a **signed average** per body over about a quarter second: oscillation cancels however violent it is, real walking accumulates. Only the result is floored.

**Single cell hot spots.** Congestion is a property of a region, not a cell. A one cell detour costs about one cell of distance, so an unsmoothed field bends every route behind a single body around it, and with a crowd those detours interfere and turn into visible weaving. A 3x3 box blur over the cost dilutes a lone hot cell to nothing while leaving a genuine jam hot, so the field only ever has features large enough to be worth walking around.

---

## 4. Separation

Reactive separation, not RVO. Each body is pushed out of whatever it currently overlaps, one subtract and one square root per overlapping pair. Bodies are allowed to overlap for a frame or two, which is what removes the predictive velocity solve that the built in avoidance spends the main thread on.

Neighbours come from a **spatial hash**: a `NativeParallelMultiHashMap` keyed by quantised cell, rebuilt each tick. The alternative is a fixed grid, which costs memory proportional to world size rather than to crowd size.

### The push clamp

`maxPushSpeed` alone is a speed, so how far it moves a body depends on the frame. A crowd whose overlaps are millimetres deep gets shoved centimetres to fix them, arrives on the far side still overlapping, and shoves back. Capping the push at **the fraction of the overlap it would take to undo it this frame** is what stops the correction overshooting the error it is correcting.

Half the overlap rather than all of it, because both bodies in a pair are being pushed apart at once.

### Settling

A crowd converging on one point never resolves: those in front are in the way, separation pushes back, and the pack churns forever because neither side can win. A body settles when the neighbours between it and its goal have settled themselves, and settled bodies are pushed at a fraction of the usual strength.

A fraction rather than nothing at all, because two bodies that settled while overlapping would interpenetrate for good.

The asymmetry is the point: an arriving body is still trying to move and takes the full push, and the crowd already in place takes fifteen percent of it, so the newcomer gives ground rather than the crowd rearranging itself.

- [Reynolds, *Steering Behaviors For Autonomous Characters*](https://www.red3d.com/cwr/steer/gdc99/) - separation is one of the classic three
- [van den Berg et al., *Reciprocal Velocity Obstacles* (RVO/ORCA)](https://gamma.cs.unc.edu/RVO2/) - the predictive alternative, and what Unity's own avoidance resembles
- [Unity: NativeParallelMultiHashMap](https://docs.unity3d.com/Packages/com.unity.collections@latest)

---

## 5. Rotation, and a bug that took a long time to find

Bodies shook. Frame by frame the rotation jumped `-40, -80, 11` degrees, which does not look like steering, because steering has smoothing in it.

The cause was that rotation followed **velocity**:

```csharp
float3 facing = new float3(velocity.x, 0f, velocity.z);   // velocity = push + heading * speed
```

For a body that has arrived, its own speed is zero, so velocity is **purely the separation push**. Neighbours jostle it from a different side every frame, so `LookRotation` was handed a direction that flipped at random. A slerp a fifth of the way toward a target that has flipped 180 degrees is a 36 degree jump, every frame, forever.

The smoothing was working perfectly. It was smoothing noise.

Rotation now follows the **heading**, which is already rate limited and follows the field rather than the crowd, and only while the body is actually trying to move. Being shoved does not turn you around.

The general lesson: when an output is jittering, check whether the *target* is jittering before adding damping. Damping a random target produces a smaller random walk, not stability.

---

## 6. Movement, substepping and time

Bodies are moved by an `IJobParallelForTransform` over a `TransformAccessArray`, which is the only way to write thousands of transforms without paying the managed/native crossing per body.

Walkability is checked on the **destination** rather than the origin, and a blocked diagonal step retries each axis alone, preferring the longer component. Refusing the whole step was what made bodies pile against walls: the flow points diagonally at a gap, one axis of that is into the wall, and refusing the pair stops a body that had a perfectly good sideways move. Taking whichever axis survives is the difference between a queue funnelling through a gap and one jamming solid outside it.

### Why fast forwarding needs substeps

`Time.timeScale` multiplies `deltaTime`, so every rate scales for free. But the step each frame integrates grows with it, and past a point a longer step is not the same simulation run faster:

- walkability is tested where a step ends, so a body covering more than a cell can pass through a wall nothing sampled
- the turn rate and acceleration limiter stop constraining anything once one step exceeds what they allow
- separation is an explicit integration of a spring, stable while the step is small and divergent once it is not

So long steps are cut into ordinary sized ones. Each substep is a normal step, the crowd behaves exactly as it would at normal speed, and it simply arrives sooner, at proportionally more CPU per frame. That is the honest trade, and it is why the slider buys *more simulation per frame* rather than a coarser simulation.

- [Glenn Fiedler, *Fix Your Timestep!*](https://gafferongames.com/post/fix_your_timestep/) - the canonical explanation of why step size is not a free parameter

---

## 7. Update management

A `MonoBehaviour.Update` is dispatched from native code per component. A few thousand of those cost real time before any of them has done anything, and an `Update` that exists only to early out still pays that dispatch.

Bodies register with `HordePathScheduler`, which owns the roster and makes one interface call each. In the profiler, `HordeTestEnemy.Update` disappeared entirely and was replaced by a single `HordePathScheduler.Update`.

The scheduler also paces repathing by distance rather than sorting: each body's interval is a function of how far it is from its goal, so a body ten metres out repaths ten times as often as one sixty metres out without anything ever ranking them. A separate guarantee pass runs first, because distance based pacing alone can starve a body indefinitely if the near ranks always fill the budget, and a body that never repaths walks at where the player used to be.

None of this is used by the flow field, which has no per body work at all. It exists so the `NavMeshAgent` baseline is a fair one.

- [Unity Learn: Optimizing scripts](https://learn.unity.com/tutorial/fixing-performance-problems)

---

## 8. Measuring honestly

The whole comparison harness exists because a claim like "35% faster" is worthless unless it can be checked.

`HordePathScheduler` switches technique at runtime on the same crowd, keeping positions, goals, budget and pacing identical, so the only thing that differs between two readings is the technique. Frame time is recorded per technique, not just the scheduler's own cost, because a technique that saves 0.2 ms here and hands the bodies to something costing 3 ms more is not cheaper.

Three measurement bugs found this way, each of which made the flow field look better than it was:

- **The roster passes were being billed to the flow field.** It has no per body work, but the scheduler still swept 5000 bodies twice a frame asking each whether it wanted a repath. That was 0.72 ms of the 0.825 ms the flow field appeared to cost.
- **The field kept rebuilding during the baseline.** Rebuild ownership sat with the flow field technique, so switching away handed it back to the driver's own timer, which went on expanding a field nothing was reading and adding it to the baseline's frame time.
- **Newly spawned bodies always came up on the flow field**, whatever technique was selected, so a run with spawning was quietly a mixture of both.

Anything measured before those were found should be thrown away. That is the point of writing the harness
rather than trusting a stopwatch.

---

## 9. Techniques considered and not used

**RVO / ORCA.** Predictive avoidance, solving for velocities that avoid collisions before they happen. Better looking than reactive separation and considerably more expensive. Reactive plus a spatial hash was enough here.

**HPA\* (hierarchical A\*).** Abstract the map into clusters, path between clusters, refine locally. The right answer for very large maps with per agent goals. Irrelevant here, since a flow field already amortises across agents.

**D\* Lite / LPA\*.** Incremental replanning, repairing a search rather than repeating it. Still the right answer for the field rebuild, and on the roadmap. Not built yet because a full expansion is currently 1.3 ms and it is not worth optimising something that has not been measured in a build.

**`NavMeshQuery` A\* in jobs.** The plan was per agent A\* on worker threads via `UnityEngine.Experimental.AI`. Four files in, that namespace turned out to be **deprecated without a replacement** in Unity 6. That is the direct reason this is a grid.

- [D\* Lite, Koenig and Likhachev](http://idm-lab.org/bib/abstracts/papers/aaai02b.pdf)
- [Botea et al., *Near Optimal Hierarchical Path-Finding*](https://webdocs.cs.ualberta.ca/~mmueller/ps/hpastar.pdf)

---

## 10. Things that turned out to matter more than expected

**Thresholds have to be derived, not typed.** `jamOccupancy` was set to 6, then to 3.5, both of which are more bodies than a one metre cell can physically contain given a half metre radius, so crowd slowing and half of congestion were unreachable while looking perfectly configured. The fix was to compute capacity from the radius and cell size and express thresholds as fractions of it. Any threshold typed in absolute units silently stops being reachable the moment something else changes.

**A gizmo scale is a claim about the data.** Cost is a ratio, so drawing it linearly from 1 to 30 put half walking pace at three percent of the way to red, indistinguishable from open ground, when half pace is exactly where a detour starts being worth taking. The routing had been working; the display was hiding it. It is drawn on a log scale now.

**Silent failures are the expensive ones.** Nearly every bug here presented as "the crowd stands still" or "the crowd shakes", with nothing logged. That is why there is a scene check that runs on play, an advisor that names which field to change, and why removed features are deleted rather than left in an enum that logs a warning.

**Allocation and disposal want to be structurally paired.** A resize method leaked two arrays because a buffer was added to the allocating half and missed in the freeing half, twice. Rewriting it so every buffer goes through one of two helpers made the mistake unexpressible, which is a better fix than being careful.