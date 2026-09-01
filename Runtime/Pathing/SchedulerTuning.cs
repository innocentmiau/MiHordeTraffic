using System;
using UnityEngine;

namespace MiHordeTraffic.Pathing
{
    /*
     * The scheduler's half of a quality preset. Repathing and separation trade against each other from the same
     * frame budget, so measuring one while the other stays fixed tells you very little about what the crowd
     * actually costs at a given quality.
     */
    /// <summary>
    /// A snapshot of the HordePathScheduler settings that are safe to change at runtime.
    /// </summary>
    [Serializable]
    public struct SchedulerTuning
    {

        [Min(1)] public int RepathsPerFrame;
        [Min(0f)] public float NearInterval;
        [Min(0f)] public float FarInterval;
        [Min(0f)] public float NearDistance;
        [Min(0f)] public float FarDistance;
        [Min(0f)] public float GuaranteedInterval;

    }
}
