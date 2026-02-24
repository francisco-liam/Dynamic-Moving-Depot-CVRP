#nullable enable
using System.Collections.Generic;
using CoreSim.Model;

namespace CoreSim.Planning
{
    public sealed class PlanResult
    {
        public Dictionary<int, List<TargetRef>> TruckPlans { get; } = new Dictionary<int, List<TargetRef>>();
        public string DebugSummary { get; set; } = string.Empty;
    }
}
