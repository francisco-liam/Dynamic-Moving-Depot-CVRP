#nullable enable
using CoreSim.Math;

namespace CoreSim.Model
{
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

        public Vec2? TargetPos { get; set; } = null;
        public int TargetId { get; set; } = -1; // e.g., customer id 

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
    }
}