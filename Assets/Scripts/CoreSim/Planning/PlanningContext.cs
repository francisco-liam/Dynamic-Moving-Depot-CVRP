#nullable enable

namespace CoreSim.Planning
{
    public sealed class PlanningContext
    {
        public float Now { get; set; } = 0f;
        public int CommitmentLockK { get; set; } = 1;
        public bool RespectCapacity { get; set; } = true;
        public bool RespectReleaseTime { get; set; } = true;
    }
}
