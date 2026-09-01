using MiHordeTraffic.Pathing.FlowField;
using Unity.Mathematics;
using UnityEngine;

namespace MiHordeTraffic.Tuning
{
    /*
     * Split off the driver so that looking at the field and maintaining it are two different components.
     * Six inspector fields that exist only to draw were sitting above the ones that decide how the crowd behaves,
     * and none of them does anything in a build. Kept together they also invited the mistake of tuning by what the
     * gizmo looked like, when what the gizmo looked like was a property of the gizmo.

     * Separate also means removable. Drawing tens of thousands of cells is expensive enough to change the frame
     * rate it is drawn at, so being able to take the component off, rather than remembering which toggle turned it
     * all off, is the difference between measuring the crowd and measuring the drawing.
     */
    /// <summary>
    /// Draws the horde flow field in the scene view. Purely a debugging aid, and safe to remove entirely.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeFlowFieldGizmos : MonoBehaviour
    {

        [SerializeField] private HordeFlowFieldDriver driver;

        [Tooltip("What the cells are coloured by. Cost is the one to watch while tuning congestion.")]
        [SerializeField] private FlowGizmoMode gizmoMode = FlowGizmoMode.FLOW;
        [Tooltip("The cost drawn as fully red, on a log scale. Match it to the mover's Maximum Cost.")]
        [SerializeField, Min(1f)] private float costScale = 30f;
        [SerializeField] private bool drawFlow = true;
        [Tooltip("Also draw cells the expansion never reached, which is how a bake problem becomes visible.")]
        [SerializeField] private bool drawUnreachable = false;

        /*
         * Sparse by default and worth keeping sparse. A grid worth having is tens of thousands of cells, and one
         * gizmo each drops the editor hard enough that the simulation itself becomes unstable at the resulting
         * frame rate, which reads as the crowd being broken rather than the drawing being expensive.
         */
        [Tooltip("Draw every Nth cell on both axes. One is every cell and is very expensive on a large grid.")]
        [SerializeField, Min(1)] private int drawEvery = 4;
        [Tooltip("Metres around the target to draw at all.")]
        [SerializeField, Min(0f)] private float drawRange = 60f;

        private void Reset() => driver = GetComponent<HordeFlowFieldDriver>();

        /*
         * Drawn sparsely by default. One arrow per cell over a grid worth having is tens of thousands of gizmo
         * lines, which stalls the editor badly enough to look like the field itself is slow.
         */
        private void OnDrawGizmosSelected()
        {
            if (!drawFlow || !driver || !driver.Field.IsBaked) return;

            GridFlowField field = driver.Field;
            Transform target = driver.Target;

            HordeGridInfo grid = field.Grid;
            float3 origin = target ? (float3)target.position : (float3)transform.position;
            float rangeSquared = drawRange * drawRange;
            float half = grid.CellSize * .45f;

            /*
             * Strided per axis rather than over the flat index. Stepping the flat index skips cells along X only,
             * because that is the axis the index runs fastest in, so the grid draws as solid rows separated by
             * gaps and reads as though the cells themselves were rectangles. They are square; only the sampling
             * was lopsided.
             */
            for (int z = 0; z < grid.Height; z += drawEvery)
            for (int x = 0; x < grid.Width; x += drawEvery)
            {
                int2 cell = new int2(x, z);
                int i = grid.IndexOf(cell);
                float3 centre = grid.CentreOf(cell);
                centre.y = field.IsWalkable(i) ? field.HeightAt(i) + .1f : centre.y;

                if (math.distancesq(centre.xz, origin.xz) > rangeSquared) continue;

                if (!field.IsWalkable(i))
                {
                    if (!drawUnreachable) continue;

                    Gizmos.color = new Color(1f, 0f, 0f, .25f);
                    Gizmos.DrawWireCube(centre, new Vector3(grid.CellSize * .8f, 0f, grid.CellSize * .8f));
                    continue;
                }

                if (gizmoMode != FlowGizmoMode.FLOW)
                {
                    /*
                     * Grey rather than the cold end of the scale when nothing has expanded the field. An unwritten
                     * cost array is all zeros, which lands on blue, and blue means "measured, and found empty".
                     * That is a different claim from "never measured", and while a technique that does not use the
                     * field is running it is the wrong one: the grid sits there looking like a verdict on a crowd
                     * nothing has ever looked at.
                     */
                    if (!field.IsBuilt)
                    {
                        Gizmos.color = new Color(.5f, .5f, .5f, .12f);
                        Gizmos.DrawCube(centre, new Vector3(grid.CellSize * .9f, .02f, grid.CellSize * .9f));
                        continue;
                    }

                    /*
                     * Drawn as filled cells rather than arrows, because what matters here is the value and not a
                     * direction. Cost is the one to watch while tuning congestion: if a jammed bridge is not
                     * visibly hotter than open ground, the field has no reason to route around it and no amount of
                     * changing the detour length will make one appear.
                     */
                    /*
                     * Cost is a ratio, so it has to be drawn on a ratio scale. Spread linearly between one and the
                     * ceiling, everything worth seeing is squashed into the bottom of the ramp: a bridge crossed at
                     * half pace is a cost of two, which on a linear scale to thirty is three percent of the way to
                     * red and indistinguishable from open ground. Yet half pace is exactly the point at which a
                     * detour starts being worth taking.
                     *
                     * On a log scale the colours land where the decisions are. Half pace reads a fifth of the way
                     * up, a fifth of pace lands in the yellows, a tenth in the oranges, and only a body that has
                     * genuinely stopped reaches red.
                     */
                    float value = gizmoMode == FlowGizmoMode.COST
                        ? math.log(math.max(field.Cost[i], 1f)) / math.log(math.max(costScale, 1.01f))
                        : field.Integration[i] / math.max(drawRange, .001f);

                    if (field.Integration[i] == float.MaxValue) value = 1f;

                    Gizmos.color = Heat(value);
                    Gizmos.DrawCube(centre, new Vector3(grid.CellSize * .9f, .02f, grid.CellSize * .9f));
                    continue;
                }

                float2 flow = field.Flow[i];

                if (flow.Equals(float2.zero))
                {
                    if (!drawUnreachable) continue;

                    Gizmos.color = new Color(1f, .5f, 0f, .4f);
                    Gizmos.DrawWireCube(centre, new Vector3(grid.CellSize * .5f, 0f, grid.CellSize * .5f));
                    continue;
                }

                Gizmos.color = Color.Lerp(Color.cyan, Color.magenta, math.saturate(field.Integration[i] / math.max(drawRange, .001f)));
                Gizmos.DrawLine(centre, centre + new float3(flow.x, 0f, flow.y) * half);
            }

            /*
             * The grid extent is drawn whether or not anything else is, because every confusing result so far has
             * come from the bounds not being where they were assumed to be, and a wireframe answers that instantly.
             */
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(
                new Vector3(grid.Origin.x + grid.Width * grid.CellSize * .5f, grid.Origin.y, grid.Origin.z + grid.Height * grid.CellSize * .5f),
                new Vector3(grid.Width * grid.CellSize, 0f, grid.Height * grid.CellSize));

            if (!target) return;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(target.position, 1f);
        }

        /*
         * Three stops rather than two, because a straight blue to red fade has no readable middle: a cell that has
         * slowed a little and a cell that has stopped dead differ only by how purple they look. Putting yellow in
         * the middle is what makes "slower than open ground" and "jammed" two states you can tell apart at a
         * glance, which is the whole reason to draw the cost field at all.
         */
        private static Color Heat(float value)
        {
            float t = math.saturate(value);

            return t < .5f
                ? Color.Lerp(new Color(0f, .4f, 1f, .3f), new Color(1f, .85f, 0f, .5f), t * 2f)
                : Color.Lerp(new Color(1f, .85f, 0f, .5f), new Color(1f, 0f, 0f, .8f), (t - .5f) * 2f);
        }

    }
}
