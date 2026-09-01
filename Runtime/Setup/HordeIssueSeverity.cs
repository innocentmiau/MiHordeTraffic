namespace MiHordeTraffic.Setup
{
    /// <summary>
    /// How much a reported problem matters, from something that stops the system working to a passing suggestion.
    /// </summary>
    public enum HordeIssueSeverity
    {
        BLOCKING, // the crowd will not work at all until this is dealt with
        WARNING, // it will run, but something is set up in a way that will bite
        ADVICE // it works, and there is a cheaper or better way to have it
    }
}
