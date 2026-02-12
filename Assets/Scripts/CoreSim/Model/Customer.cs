#nullable enable
using CoreSim.Math;

namespace CoreSim.Model
{
    public enum CustomerStatus
    {
        Unreleased = 0,
        Waiting = 1,
        InService = 2,
        Served = 3
    }

    public sealed class Customer
    {
        public int Id { get; }
        public Vec2 Pos { get; }
        public int Demand { get; }
        public float ReleaseTime { get; }
        public float ServiceTime { get; set; }

        public int? AssignedTruckId { get; set; } = null;

        public CustomerStatus Status { get; set; } = CustomerStatus.Unreleased;

        public Customer(int id, Vec2 pos, int demand, float releaseTime, float serviceTime = 0f)
        {
            Id = id;
            Pos = pos;
            Demand = demand;
            ReleaseTime = releaseTime;
            ServiceTime = serviceTime;
        }

        public bool IsAvailable(float time)
        {
            return Status == CustomerStatus.Unreleased && time >= ReleaseTime;
        }

        public bool IsServed => Status == CustomerStatus.Served;
    }
}