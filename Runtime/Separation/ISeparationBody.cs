using Unity.Mathematics;
using UnityEngine;

namespace MiHordeTraffic.Separation
{
    /*
     * The whole contract between the system and whatever is being separated is position in, push out.
     * Nothing here mentions NavMeshAgent, Rigidbody, Transform or a number of dimensions,
     * which is what lets a 3D navmesh scene and a NavMeshPlus 2D scene share one system without either knowing about the other.
     *
     * SeparationIndex is stored on the body rather than looked up in a dictionary because the system does a swap remove
     * on unregister and would otherwise need a hash lookup per agent per frame just to find where its push force landed.
     *
     * GroupSettings doubles as the group identity. Bodies handing back the same settings asset end up in the same group and push against each other;
     * bodies handing back different assets never meet.
     * Using the asset itself rather than a name or an id means there is no registry to keep in sync and no way to typo a body into a group that does not exist.
     */
    /// <summary>
    /// Something the SeparationSystem can push apart from its neighbours.
    /// </summary>
    public interface ISeparationBody
    {

        /// <summary>
        /// Slot this body occupies in its group's arrays, or -1 while it is not registered. Only the system writes this.
        /// </summary>
        int SeparationIndex { get; set; }

        /// <summary>
        /// Which group this body separates within, or null to fall back to the system's default settings.
        /// Read once when the body is registered, so it must not change while the body is registered.
        /// </summary>
        SeparationSettings GroupSettings { get; }

        /// <summary>
        /// How much room this body wants around itself. Two bodies overlap once they are closer than the sum of their radii.
        /// </summary>
        float SeparationRadius { get; }

        /// <summary>
        /// The transform the system samples positions from and, in the batched hand off, writes back to.
        /// Read once when the body is registered, so it must be the transform the body keeps for its whole life.
        /// </summary>
        Transform SeparationTransform { get; }

        /// <summary>
        /// Whether this body is currently trying to advance towards its goal. A body that says no settles outright
        /// and stops shoving its neighbours, and the bodies behind it settle in turn. This is read rather than
        /// inferred because the body knows the answer and the system can only guess at it.
        /// </summary>
        bool WantsToMove { get; }

        /// <summary>
        /// Whether this body is actually heading somewhere. A body that is not never counts as blocked,
        /// since there is no goal for its neighbours to be standing between it and.
        /// </summary>
        bool HasSeparationGoal { get; }

        /// <summary>
        /// Where this body is trying to get to, used to work out whether the neighbours in its way are between it
        /// and that goal. Only read when HasSeparationGoal is true.
        /// </summary>
        float3 SeparationGoal { get; }

        /// <summary>
        /// Hands the body its push for this frame.
        /// </summary>
        /// <param name="push">Desired push velocity in world units per second, already clamped by the system.</param>
        /// <param name="deltaTime">Frame time to scale the push by, so the result does not change with framerate.</param>
        void ApplySeparationPush(float3 push, float deltaTime);

        /// <summary>
        /// Tells the body it has settled, because it said it was not advancing or because it is walled in by
        /// neighbours that already settled. Only called when the value changes, never every frame.
        /// </summary>
        /// <param name="settled">True when the body should stop trying to advance.</param>
        void SetSeparationSettled(bool settled);

    }
}
