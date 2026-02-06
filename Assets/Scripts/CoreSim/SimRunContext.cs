using CoreSim.Utils;

namespace CoreSim
{
    /// <summary>
    /// Bundles seed + RNG + logger for a simulation run.
    /// </summary>
    public sealed class SimRunContext
    {
        public int Seed { get; }
        public DeterministicRng Rng { get; }
        public SimLogger Logger { get; }

        public SimRunContext(int seed, SimLogger logger)
        {
            Seed = seed;
            Logger = logger;
            Rng = new DeterministicRng(seed);
        }
    }
}