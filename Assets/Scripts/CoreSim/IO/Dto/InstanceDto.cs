#nullable enable
using System.Collections.Generic;
using CoreSim.Math;

namespace CoreSim.IO
{
    public sealed class InstanceDto
    {
        // --- Standard-ish TSPLIB headers ---
        public string Name { get; set; } = "";
        public string Comment { get; set; } = "";
        public string Type { get; set; } = "";          // e.g., "DMDVRP"
        public int Dimension { get; set; }              // number of nodes (including depot node(s))
        public int Capacity { get; set; }
        public string EdgeWeightType { get; set; } = ""; // e.g., "EUC_2D"

        // --- Added for your variant ---
        public float TruckSpeed { get; set; } = 1f;
        public float DepotSpeed { get; set; } = 1f;

        // Node ids are 1..Dimension. Index 0 unused for convenience.
        public Vec2[] NodePos { get; set; } = new Vec2[0];
        public int[] Demand { get; set; } = new int[0];
        public float[] ReleaseTime { get; set; } = new float[0];

        // Usually [1]
        public List<int> DepotNodeIds { get; set; } = new List<int>();

        // Candidate stops for the mobile depot (Option B)
        public List<DepotStopDto> DepotCandidateStops { get; set; } = new List<DepotStopDto>();
    }

    public readonly struct DepotStopDto
    {
        public readonly int StopId;
        public readonly Vec2 Pos;

        public DepotStopDto(int stopId, Vec2 pos)
        {
            StopId = stopId;
            Pos = pos;
        }
    }
}