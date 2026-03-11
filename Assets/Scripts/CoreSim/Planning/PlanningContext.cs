#nullable enable

namespace CoreSim.Planning
{
    public sealed class PlanningContext
    {
        public float Now { get; set; } = 0f;
        public int CommitmentLockK { get; set; } = 1;
        public bool RespectCapacity { get; set; } = true;
        public bool RespectReleaseTime { get; set; } = true;

        /// <summary>
        /// How many real seconds the solver is allowed to run. When > 0 this overrides the
        /// planner's own <c>SolverTimeBudgetSeconds</c> property so the budget can be set
        /// dynamically per-replan without mutating the planner object.
        /// </summary>
        public float SolverTimeBudgetSeconds { get; set; } = 0f;
    }
}
