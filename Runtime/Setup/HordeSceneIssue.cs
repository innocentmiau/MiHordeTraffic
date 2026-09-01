namespace MiHordeTraffic.Setup
{
    /*
     * One problem, in words a person can act on. Every issue carries what to do about it rather than only what is
     * wrong, because a warning that names a symptom and leaves you to find the field is a warning people learn to
     * ignore. The remedy is the whole value of reporting it at all.
     */
    /// <summary>
    /// Something wrong with a scene or a setting, and what to do about it.
    /// </summary>
    public readonly struct HordeSceneIssue
    {

        /// <summary>
        /// How much this one matters.
        /// </summary>
        public HordeIssueSeverity Severity { get; }

        /// <summary>
        /// What is wrong, in one line.
        /// </summary>
        public string Problem { get; }

        /// <summary>
        /// What to change to put it right.
        /// </summary>
        public string Remedy { get; }

        /// <summary>
        /// Builds one issue.
        /// </summary>
        /// <param name="severity">How much it matters.</param>
        /// <param name="problem">What is wrong.</param>
        /// <param name="remedy">What to do about it.</param>
        public HordeSceneIssue(HordeIssueSeverity severity, string problem, string remedy)
        {
            Severity = severity;
            Problem = problem;
            Remedy = remedy;
        }

        /// <summary>
        /// The issue on one line, ready to log.
        /// </summary>
        /// <returns>The formatted issue.</returns>
        public override string ToString() => $"[{Severity}] {Problem}  ->  {Remedy}";

    }
}
