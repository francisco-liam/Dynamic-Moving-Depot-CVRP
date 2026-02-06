// CoreSim/SimConfig.cs
#nullable enable

namespace CoreSim
{
    /// <summary>
    /// Run-time configuration for a simulation run.
    /// This is NOT the problem instance (customers/depot/trucks). It controls how we run it.
    /// </summary>
    public sealed class SimConfig
    {
        // --- Determinism / run identity ---
        public int Seed { get; set; } = 12345;

        // --- Simulation control (Unity can drive this) ---
        /// <summary>
        /// Multiplier applied to Unity deltaTime when advancing sim time.
        /// 1.0 = real-time, 2.0 = twice as fast, etc.
        /// </summary>
        public float TimeScale { get; set; } = 1f;

        // --- Planner / replanning knobs (environment policy) ---
        /// <summary>
        /// How many upcoming targets are committed (in addition to the current leg).
        /// </summary>
        public int LockedPrefixCount { get; set; } = 1;

        /// <summary>
        /// Minimum simulated seconds between replans to prevent thrashing.
        /// </summary>
        public float MinSecondsBetweenReplans { get; set; } = 1.0f;

        /// <summary>
        /// Optional periodic replan interval in simulated seconds. Set null to disable.
        /// </summary>
        public float? PeriodicReplanInterval { get; set; } = 5.0f;

        /// <summary>
        /// Soft time budget for planners (especially later for HGS). Units: milliseconds.
        /// </summary>
        public int PlannerTimeBudgetMs { get; set; } = 50;

        // --- Optional overrides (experiment knobs) ---
        /// <summary>
        /// If set, overrides the truck speed defined by the instance.
        /// Units are your instance's distance units per simulated second.
        /// </summary>
        public float? OverrideTruckSpeed { get; set; } = null;

        /// <summary>
        /// If set, overrides the depot/carrier speed defined by the instance.
        /// Units are your instance's distance units per simulated second.
        /// </summary>
        public float? OverrideDepotSpeed { get; set; } = null;

        // --- Optional: travel model toggles (future-proofing) ---
        /// <summary>
        /// If true, travel is computed as Euclidean distance in XZ plane.
        /// Later you can add a graph-based travel model and switch this off.
        /// </summary>
        public bool UseEuclideanTravel { get; set; } = true;
    }
}