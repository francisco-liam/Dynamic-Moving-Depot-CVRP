#nullable enable
using System.Collections.Generic;
using CoreSim.Math;

namespace CoreSim.Model
{
    public sealed class DepotCarrier
    {
        public Vec2 Pos { get; set; }
        public float Speed { get; set; }

        // Option B: discrete candidate rendezvous positions
        public List<DepotCandidateStop> CandidateStops { get; } = new List<DepotCandidateStop>();

        public DepotCarrier(Vec2 startPos, float speed)
        {
            Pos = startPos;
            Speed = speed;
        }
    }
}