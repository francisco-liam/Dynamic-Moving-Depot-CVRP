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

        public Truck(int id, Vec2 startPos, int capacity, float speed)
        {
            Id = id;
            Pos = startPos;
            Capacity = capacity;
            Speed = speed;
            Load = 0;
        }
    }
}