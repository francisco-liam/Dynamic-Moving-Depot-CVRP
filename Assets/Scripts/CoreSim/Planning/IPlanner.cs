#nullable enable
using CoreSim.Model;

namespace CoreSim.Planning
{
    public interface IPlanner
    {
        PlanResult ComputePlan(SimState snapshot, PlanningContext ctx);
    }
}
