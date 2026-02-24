#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using CoreSim.Math;
using CoreSim.Model;

namespace CoreSim.Planning
{
    public sealed class BaselinePlanner : IPlanner
    {
        private const float CostEpsilon = 1e-6f;

        public PlanResult ComputePlan(SimState snapshot, PlanningContext ctx)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            var result = new PlanResult();

            var customerById = BuildCustomerMap(snapshot);
            var trucks = BuildOrderedTrucks(snapshot);
            var workingPlans = BuildWorkingPlans(snapshot, trucks, customerById);
            var alreadyAssigned = BuildAssignedCustomerSet(workingPlans);
            var availableCustomers = BuildAvailableCustomerList(snapshot, ctx, alreadyAssigned);

            var unassigned = new List<int>();

            for (int i = 0; i < availableCustomers.Count; i++)
            {
                var customer = availableCustomers[i];

                bool assigned = TryAssignCustomerGreedy(customer, workingPlans, customerById, ctx);
                if (!assigned)
                    unassigned.Add(customer.Id);
            }

            for (int i = 0; i < workingPlans.Count; i++)
            {
                var wp = workingPlans[i];
                var fullPlan = new List<TargetRef>(wp.HistoryPrefix.Count + wp.Plan.Count);
                fullPlan.AddRange(wp.HistoryPrefix);
                fullPlan.AddRange(wp.Plan);
                result.TruckPlans[wp.TruckId] = fullPlan;
            }

            result.DebugSummary = BuildDebugSummary(availableCustomers.Count, workingPlans, unassigned);
            return result;
        }

        private static Dictionary<int, Customer> BuildCustomerMap(SimState snapshot)
        {
            var map = new Dictionary<int, Customer>();
            for (int i = 0; i < snapshot.Customers.Count; i++)
                map[snapshot.Customers[i].Id] = snapshot.Customers[i];
            return map;
        }

        private static List<Truck> BuildOrderedTrucks(SimState snapshot)
        {
            var trucks = new List<Truck>(snapshot.Trucks);
            trucks.Sort((a, b) => a.Id.CompareTo(b.Id));
            return trucks;
        }

        private static HashSet<int> BuildAssignedCustomerSet(List<WorkingTruckPlan> plans)
        {
            var assigned = new HashSet<int>();

            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i].Plan;
                for (int j = 0; j < plan.Count; j++)
                {
                    if (plan[j].Type == TargetType.Customer)
                        assigned.Add(plan[j].Id);
                }
            }

            return assigned;
        }

        private static List<Customer> BuildAvailableCustomerList(SimState snapshot, PlanningContext ctx, HashSet<int> alreadyAssigned)
        {
            var available = new List<Customer>();

            for (int i = 0; i < snapshot.Customers.Count; i++)
            {
                var c = snapshot.Customers[i];

                if (alreadyAssigned.Contains(c.Id))
                    continue;

                if (c.Status == CustomerStatus.Served || c.Status == CustomerStatus.InService)
                    continue;

                if (c.Status == CustomerStatus.Waiting)
                {
                    available.Add(c);
                    continue;
                }

                if (!ctx.RespectReleaseTime && c.Status == CustomerStatus.Unreleased)
                    available.Add(c);
            }

            available.Sort((a, b) => a.Id.CompareTo(b.Id));
            return available;
        }

        private static List<WorkingTruckPlan> BuildWorkingPlans(
            SimState snapshot,
            List<Truck> orderedTrucks,
            Dictionary<int, Customer> customerById)
        {
            var plans = new List<WorkingTruckPlan>(orderedTrucks.Count);

            for (int i = 0; i < orderedTrucks.Count; i++)
            {
                var truck = orderedTrucks[i];

                int currentIndex = truck.CurrentTargetIndex;
                if (currentIndex < 0)
                    currentIndex = 0;
                if (currentIndex > truck.Plan.Count)
                    currentIndex = truck.Plan.Count;

                int remaining = truck.Plan.Count - currentIndex;
                int lockedCount = Clamp(truck.LockedPrefixCount, 0, remaining);

                var historyPrefix = new List<TargetRef>(currentIndex);
                for (int j = 0; j < currentIndex; j++)
                    historyPrefix.Add(truck.Plan[j]);

                var plan = new List<TargetRef>(lockedCount);
                int plannedDemand = truck.Load;

                for (int j = 0; j < lockedCount; j++)
                {
                    var target = truck.Plan[currentIndex + j];
                    plan.Add(target);

                    if (target.Type == TargetType.Customer && customerById.TryGetValue(target.Id, out var c))
                        plannedDemand += c.Demand;
                }

                plans.Add(new WorkingTruckPlan(
                    truck.Id,
                    truck.Pos,
                    snapshot.Depot.Pos,
                    snapshot.StationPositions,
                    truck.Capacity,
                    historyPrefix,
                    plan,
                    lockedCount,
                    plannedDemand));
            }

            return plans;
        }

        private static bool TryAssignCustomerGreedy(
            Customer customer,
            List<WorkingTruckPlan> workingPlans,
            Dictionary<int, Customer> customerById,
            PlanningContext ctx)
        {
            int bestPlanIndex = -1;
            int bestInsertIndex = -1;
            float bestAddedCost = float.PositiveInfinity;

            for (int p = 0; p < workingPlans.Count; p++)
            {
                var wp = workingPlans[p];

                if (ctx.RespectCapacity && wp.Capacity > 0)
                {
                    if (wp.PlannedDemand + customer.Demand > wp.Capacity)
                        continue;
                }

                for (int insertIndex = wp.LockedPrefixCount; insertIndex <= wp.Plan.Count; insertIndex++)
                {
                    if (!TryComputeAddedDistance(wp, customer, customerById, insertIndex, out float addedCost))
                        continue;

                    bool better = false;
                    if (addedCost < bestAddedCost - CostEpsilon)
                    {
                        better = true;
                    }
                    else if (System.Math.Abs(addedCost - bestAddedCost) <= CostEpsilon)
                    {
                        if (bestPlanIndex < 0 || wp.TruckId < workingPlans[bestPlanIndex].TruckId)
                            better = true;
                        else if (wp.TruckId == workingPlans[bestPlanIndex].TruckId && insertIndex < bestInsertIndex)
                            better = true;
                    }

                    if (better)
                    {
                        bestPlanIndex = p;
                        bestInsertIndex = insertIndex;
                        bestAddedCost = addedCost;
                    }
                }
            }

            if (bestPlanIndex < 0)
                return false;

            var bestPlan = workingPlans[bestPlanIndex];
            bestPlan.Plan.Insert(bestInsertIndex, TargetRef.Customer(customer.Id));
            bestPlan.PlannedDemand += customer.Demand;
            return true;
        }

        private static bool TryComputeAddedDistance(
            WorkingTruckPlan plan,
            Customer customer,
            Dictionary<int, Customer> customerById,
            int insertIndex,
            out float addedCost)
        {
            Vec2 prevPos;
            if (insertIndex == 0)
            {
                prevPos = plan.StartPos;
            }
            else
            {
                var prevTarget = plan.Plan[insertIndex - 1];
                if (!TryResolveTargetPos(plan, prevTarget, customerById, out prevPos))
                {
                    addedCost = 0f;
                    return false;
                }
            }

            Vec2 customerPos = customer.Pos;

            if (insertIndex == plan.Plan.Count)
            {
                addedCost = Vec2.Distance(prevPos, customerPos);
                return true;
            }

            var nextTarget = plan.Plan[insertIndex];
            if (!TryResolveTargetPos(plan, nextTarget, customerById, out Vec2 nextPos))
            {
                addedCost = 0f;
                return false;
            }

            float without = Vec2.Distance(prevPos, nextPos);
            float with = Vec2.Distance(prevPos, customerPos) + Vec2.Distance(customerPos, nextPos);
            addedCost = with - without;
            return true;
        }

        private static bool TryResolveTargetPos(WorkingTruckPlan plan, TargetRef target, Dictionary<int, Customer> customerById, out Vec2 pos)
        {
            if (target.Type == TargetType.Depot)
            {
                pos = plan.DepotPos;
                return true;
            }

            if (target.Type == TargetType.Customer)
            {
                if (customerById.TryGetValue(target.Id, out var c))
                {
                    pos = c.Pos;
                    return true;
                }

                pos = default;
                return false;
            }

            if (target.Type == TargetType.Station)
            {
                if (plan.StationPositions.TryGetValue(target.Id, out var stationPos))
                {
                    pos = stationPos;
                    return true;
                }

                pos = default;
                return false;
            }

            pos = default;
            return false;
        }

        private static string BuildDebugSummary(int availableCount, List<WorkingTruckPlan> plans, List<int> unassigned)
        {
            var sb = new StringBuilder();
            sb.Append("BaselinePlanner: available=").Append(availableCount);
            sb.Append(", trucks=").Append(plans.Count);

            if (unassigned.Count > 0)
            {
                sb.Append(", unassigned=").Append(unassigned.Count).Append(" [");
                for (int i = 0; i < unassigned.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(unassigned[i]);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append(", unassigned=0");
            }

            return sb.ToString();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private sealed class WorkingTruckPlan
        {
            public int TruckId { get; }
            public Vec2 StartPos { get; }
            public Vec2 DepotPos { get; }
            public IReadOnlyDictionary<int, Vec2> StationPositions { get; }
            public int Capacity { get; }
            public List<TargetRef> HistoryPrefix { get; }
            public List<TargetRef> Plan { get; }
            public int LockedPrefixCount { get; }
            public int PlannedDemand { get; set; }

            public WorkingTruckPlan(
                int truckId,
                Vec2 startPos,
                Vec2 depotPos,
                IReadOnlyDictionary<int, Vec2> stationPositions,
                int capacity,
                List<TargetRef> historyPrefix,
                List<TargetRef> plan,
                int lockedPrefixCount,
                int plannedDemand)
            {
                TruckId = truckId;
                StartPos = startPos;
                DepotPos = depotPos;
                StationPositions = stationPositions;
                Capacity = capacity;
                HistoryPrefix = historyPrefix;
                Plan = plan;
                LockedPrefixCount = lockedPrefixCount;
                PlannedDemand = plannedDemand;
            }
        }
    }
}
