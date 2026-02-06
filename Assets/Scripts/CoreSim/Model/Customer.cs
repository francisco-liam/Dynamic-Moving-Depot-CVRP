#nullable enable
using CoreSim.Math;

namespace CoreSim.Model
{
    public enum CustomerStatus
    {
        Unreleased = 0,
        Available = 1,
        Served = 2
    }

    public sealed class Customer
    {
        public int Id { get; }
        public Vec2 Pos { get; }
        public int Demand { get; }
        public float ReleaseTime { get; }

        public CustomerStatus Status { get; set; } = CustomerStatus.Unreleased;

        public Customer(int id, Vec2 pos, int demand, float releaseTime)
        {
            Id = id;
            Pos = pos;
            Demand = demand;
            ReleaseTime = releaseTime;
        }
    }
}