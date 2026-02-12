#nullable enable
using CoreSim.Math;

namespace CoreSim.Model
{
    public readonly struct DepotStop
    {
        public readonly int StopId;
        public readonly Vec2 Pos;
        public readonly float ArrivalTime;
        public readonly float DepartureTime;

        public DepotStop(int stopId, Vec2 pos, float arrivalTime, float departureTime)
        {
            StopId = stopId;
            Pos = pos;
            ArrivalTime = arrivalTime;
            DepartureTime = departureTime;
        }
    }
}
