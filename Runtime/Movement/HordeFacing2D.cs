namespace MiHordeTraffic.Movement
{
    /*
     * A 3D body turns to face where it walks, and that is never wrong. A 2D body is a sprite, and what it should do
     * depends on how it was drawn: most top down characters are animated per direction and must not be rotated at all,
     * a side view sprite only needs mirroring, and a ship or an insect drawn from above wants to turn like a 3D body.
     */
    /// <summary>
    /// How a body in a 2D scene shows which way it is heading.
    /// </summary>
    public enum HordeFacing2D
    {
        NONE, // the transform is never turned, for sprites animated per direction from the heading
        FLIP_X, // mirrors the local X scale towards the side it is walking, for side view sprites drawn facing right
        ROTATE_FACING_RIGHT, // turns about Z, for sprites drawn pointing along +X
        ROTATE_FACING_UP // turns about Z, for sprites drawn pointing along +Y
    }
}
