#nullable enable
namespace CoreSim.Events
{
    public readonly struct SimEvent
    {
        public readonly float Time;
        public readonly SimEventType Type;

        // Generic payload (keep it simple for now)
        public readonly int A;
        public readonly int B;

        public SimEvent(float time, SimEventType type, int a = 0, int b = 0)
        {
            Time = time;
            Type = type;
            A = a;
            B = b;
        }

        public override string ToString()
        {
            return $"{Time:0.###} {Type} (A={A}, B={B})";
        }
    }
}