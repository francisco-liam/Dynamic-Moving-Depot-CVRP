#nullable enable
using System.Collections.Generic;

namespace CoreSim.Model
{
    public sealed class SimState
    {
        public float Time { get; set; } = 0f;

        public int Capacity { get; }
        public List<Customer> Customers { get; } = new List<Customer>();
        public DepotCarrier Depot { get; }
        public List<Truck> Trucks { get; } = new List<Truck>();

        public SimState(int capacity, DepotCarrier depot)
        {
            Capacity = capacity;
            Depot = depot;
        }
    }
}