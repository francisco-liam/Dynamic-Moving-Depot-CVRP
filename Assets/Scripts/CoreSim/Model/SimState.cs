#nullable enable
using System.Collections.Generic;
using CoreSim.IO;

namespace CoreSim.Model
{
    public sealed class SimState
    {
        public float Time { get; set; } = 0f;

        public ProblemFeatures Features { get; set; } = ProblemFeatures.None;

        public int Capacity { get; }
        public List<Customer> Customers { get; } = new List<Customer>();
        public DepotCarrier Depot { get; }
        public List<Truck> Trucks { get; } = new List<Truck>();

        public float? EnergyCapacity { get; set; } = null;
        public float? EnergyConsumption { get; set; } = null;
        public List<int> StationNodeIds { get; } = new List<int>();

        public SimState(int capacity, DepotCarrier depot)
        {
            Capacity = capacity;
            Depot = depot;
        }
    }
}