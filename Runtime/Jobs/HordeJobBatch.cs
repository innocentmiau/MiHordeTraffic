using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace MiHordeTraffic.Jobs
{
    /*
     * How many elements one worker should take at a time, worked out from the machine rather than typed in. A
     * constant cannot be right for this package: the same job runs over a grid of six hundred cells in a test scene
     * and a million in a kilometre of map, and a batch that suits one is two orders of magnitude wrong for the
     * other.
     *
     * Too small is the failure that actually happened. Every batch costs a dispatch and an atomic increment on a
     * counter every worker is contending for, so a million cells at sixty four apiece is fifteen thousand of those,
     * and the job spends a good part of its life handing out work rather than doing it. Too large is the opposite
     * failure and is milder: fewer batches than workers leaves some idle, and nothing can be rebalanced when one
     * chunk turns out heavier than another.
     *
     * A few batches per worker is the middle. Enough to balance an uneven load, large enough that the dispatch is
     * noise beside the work in it.
     *
     * The floor matters as much as the formula. On a small grid the division gives a batch of five, which is the
     * overhead failure again at the other end of the scale, and a job with a hundred elements does not need
     * splitting finely to keep anybody busy.
     */
    /// <summary>
    /// Picks the batch size for a parallel job from how much work there is and how many workers exist to do it.
    /// </summary>
    public static class HordeJobBatch
    {

        private const int BATCHES_PER_WORKER = 4;
        private const int MINIMUM_BATCH = 64;

        /// <summary>
        /// How many elements each worker should take at a time for a job of a given size.
        /// </summary>
        /// <param name="count">How many elements the job runs over.</param>
        /// <returns>A batch size, never below a small floor.</returns>
        public static int For(int count)
        {
            int workers = math.max(JobsUtility.JobWorkerCount, 1);

            return math.max(count / (workers * BATCHES_PER_WORKER), MINIMUM_BATCH);
        }

    }
}
