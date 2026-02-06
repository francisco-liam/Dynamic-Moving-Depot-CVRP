#nullable enable
using System.Collections.Generic;
using CoreSim.Math;
using CoreSim.Model;

namespace CoreSim.IO
{
    public static class InstanceMapper
    {
        /// <summary>
        /// Converts a parsed instance into a runtime SimState.
        /// Does not decide routes or vehicle count.
        /// </summary>
        public static SimState FromDto(InstanceDto dto, CoreSim.SimConfig cfg)
        {
            // Speeds with config overrides
            float truckSpeed = cfg.OverrideTruckSpeed ?? dto.TruckSpeed;
            float depotSpeed = cfg.OverrideDepotSpeed ?? dto.DepotSpeed;

            // Determine primary depot node id
            int depotNodeId = (dto.DepotNodeIds.Count > 0) ? dto.DepotNodeIds[0] : 1;
            Vec2 depotPos = dto.NodePos[depotNodeId];

            var depot = new DepotCarrier(depotPos, depotSpeed);

            // Candidate stops (Option B)
            if (dto.DepotCandidateStops != null)
            {
                foreach (var s in dto.DepotCandidateStops)
                {
                    depot.CandidateStops.Add(new DepotCandidateStop(s.StopId, s.Pos));
                }
            }

            // Ensure at least one candidate stop at the actual depot position
            if (depot.CandidateStops.Count == 0)
            {
                depot.CandidateStops.Add(new DepotCandidateStop(1, depotPos));
            }

            var state = new SimState(dto.Capacity, depot);

            // Customers: all nodes except depot nodes
            var depotSet = new HashSet<int>(dto.DepotNodeIds.Count > 0 ? dto.DepotNodeIds : new List<int> { 1 });

            for (int nodeId = 1; nodeId <= dto.Dimension; nodeId++)
            {
                if (depotSet.Contains(nodeId))
                    continue;

                var c = new Customer(
                    id: nodeId,
                    pos: dto.NodePos[nodeId],
                    demand: dto.Demand[nodeId],
                    releaseTime: dto.ReleaseTime[nodeId]
                );

                // initial status based on time=0
                c.Status = (c.ReleaseTime <= 0f) ? CustomerStatus.Available : CustomerStatus.Unreleased;

                state.Customers.Add(c);
            }

            // Trucks are optional at this stage.
            // If you want "some trucks exist visually", call CreateDemoFleet(...) from Unity.
            return state;
        }

        public static void CreateDemoFleet(SimState state, int truckCount, float truckSpeed)
        {
            state.Trucks.Clear();
            for (int i = 0; i < truckCount; i++)
            {
                state.Trucks.Add(new Truck(id: i + 1, startPos: state.Depot.Pos, capacity: state.Capacity, speed: truckSpeed));
            }
        }
    }
}