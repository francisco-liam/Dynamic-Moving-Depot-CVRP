#nullable enable
using System.Collections.Generic;
using CoreSim.Math;

namespace CoreSim.Model
{
    public enum TruckState
    {
        Idle = 0,
        Traveling = 1,
        Servicing = 2,
        Charging = 3
    }

    public sealed class Truck
    {
        public int Id { get; }
        public Vec2 Pos { get; set; }

        public int Capacity { get; }
        public int Load { get; set; }

        public float Speed { get; set; }

        public float BatteryCapacity { get; }
        public float Battery { get; set; }
        public float EnergyConsumption { get; set; }

        public List<TargetRef> Plan { get; } = new List<TargetRef>();
        public int LockedPrefixCount { get; set; } = 0;
        public int CurrentTargetIndex { get; set; } = 0;

        public TruckState State { get; set; } = TruckState.Idle;
        public float ServiceRemaining { get; set; } = 0f;
        public int ServicingCustomerId { get; set; } = -1;

        public Vec2? TargetPos { get; set; } = null;
        public int TargetId { get; set; } = -1; // e.g., customer id 

        public TargetRef? ActiveTarget { get; set; } = null;
        public bool ArrivedOnActiveTarget { get; set; } = false;

        public Truck(int id, Vec2 startPos, int capacity, float speed, float batteryCapacity = 0f, float energyConsumption = 0f)
        {
            Id = id;
            Pos = startPos;
            Capacity = capacity;
            Speed = speed;
            Load = 0;
            BatteryCapacity = batteryCapacity;
            Battery = batteryCapacity;
            EnergyConsumption = energyConsumption;
        }

        public bool HasPlanTarget => CurrentTargetIndex >= 0 && CurrentTargetIndex < Plan.Count;

        public TargetRef? CurrentTarget => HasPlanTarget ? Plan[CurrentTargetIndex] : (TargetRef?)null;
    }
}