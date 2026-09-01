using System;
using UnityEngine;

namespace MiHordeTraffic.Separation
{
    /*
     * The subset of SeparationSettings worth changing while the game is running, gathered into one value so it can
     * be snapshotted and put back. Everything here trades how the crowd looks against what it costs; the settings
     * left out of it either describe the world rather than the quality of the simulation, like the plane, or cannot
     * be changed without reallocating, like the initial capacity.
     *
     * Being a plain struct is the point. A benchmark can hold the original, apply a preset, and restore the original
     * afterwards without any chance of having quietly kept a reference to the thing it was meant to be replacing,
     * which for a ScriptableObject in the editor means permanently editing the asset on disk.
     */
    /// <summary>
    /// A snapshot of the separation settings that are safe to change at runtime.
    /// </summary>
    [Serializable]
    public struct SeparationTuning
    {

        [Min(1)] public int UpdateInterval;
        public bool ReusePushBetweenTicks;
        [Min(0f)] public float CellSize;

        public bool DetourEnabled;
        [Min(0f)] public float DetourStrength;
        [Min(0f)] public float DetourDensityWeight;

        public bool BlockingEnabled;
        [Min(1)] public int BlockingNeighbours;
        [Range(0f, 1f)] public float SettledPushScale;

    }
}
