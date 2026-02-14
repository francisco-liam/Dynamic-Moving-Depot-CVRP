#nullable enable
using CoreSim.Math;

namespace CoreSim.Model
{
    public sealed class CustomerSpec
    {
        public Vec2 Pos { get; set; }
        public int Demand { get; set; } = 1;
        public float ReleaseTime { get; set; } = 0f;
        public float ServiceTime { get; set; } = 1f;

        public CustomerSpec(Vec2 pos)
        {
            Pos = pos;
        }
    }
}
