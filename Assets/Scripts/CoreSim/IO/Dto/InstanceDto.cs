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
        public string Type { get; set; } = "";
        public int Dimension { get; set; }
        public int Capacity { get; set; }
        public string EdgeWeightType { get; set; } = "";

        // --- Your additions ---
        public float TruckSpeed { get; set; } = 1f;
        public float DepotSpeed { get; set; } = 0f; // 0 => stationary default

        // --- EVRP-ish additions (optional) ---
        public float? EnergyCapacity { get; set; } = null;        // battery capacity
        public float? EnergyConsumption { get; set; } = null;     // energy per distance unit
        public int? StationCountHeader { get; set; } = null;      // STATIONS header, if present
        public int? VehiclesHeader { get; set; } = null;          // VEHICLES header, if present

        // --- Parsed node data ---
        public Vec2[] NodePos { get; set; } = new Vec2[0];
        public int[] Demand { get; set; } = new int[0];
        public float[] ReleaseTime { get; set; } = new float[0];

        public List<int> DepotNodeIds { get; set; } = new List<int>();

        // Moving depot candidate stops (your format)
        public List<DepotStopDto> DepotCandidateStops { get; set; } = new List<DepotStopDto>();

        // EVRP-style station ids (CVRPLIB-like EVRP instances often list station node ids)
        public List<int> StationNodeIds { get; set; } = new List<int>();

        // --- Detection results (authoritative; do not trust TYPE) ---
        public ProblemFeatures Features { get; set; } = ProblemFeatures.None;
        public string DetectedProblemKind { get; set; } = "";  // e.g. "C", "CE", "CD", "CDEM"
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