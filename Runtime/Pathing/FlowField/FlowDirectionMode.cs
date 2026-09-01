namespace MiHordeTraffic.Pathing.FlowField
{
    /// <summary>
    /// How a cell's cost field is turned into a direction to walk.
    /// </summary>
    public enum FlowDirectionMode
    {
        NEIGHBOUR_8, // step towards the cheapest of the eight neighbours, so every body walks on one of eight headings
        GRADIENT // follow the slope of the cost field itself, which gives any angle rather than a fixed set of them
    }
}
