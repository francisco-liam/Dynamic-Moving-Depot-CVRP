#nullable enable
using CoreSim.Math;

namespace CoreSim.Model
{
    public readonly struct DepotCandidateStop
    {
        public readonly int StopId;
        public readonly Vec2 Pos;

        public DepotCandidateStop(int stopId, Vec2 pos)
        {
            StopId = stopId;
            Pos = pos;
        }
    }
}