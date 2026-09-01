using UnityEngine;

namespace MiHordeTraffic.Movement
{
    /*
     * The one thing anything spawning a horde needs from an entity, kept as an interface so the package does not
     * have to know what an entity is. Naming a concrete type here instead meant the benchmark could only chase a
     * target on the entity types that happened to ship with it, and a project's own enemy class, which is the
     * normal case, silently got no target and stood still.
     */
    /// <summary>
    /// Something that can be told what to walk towards, so spawners and benchmarks can target any entity type.
    /// </summary>
    public interface IHordeTargetable
    {

        /// <summary>
        /// Tells this entity where to go.
        /// </summary>
        /// <param name="target">The transform to walk towards, or null to stop having one.</param>
        void SetTarget(Transform target);

    }
}
