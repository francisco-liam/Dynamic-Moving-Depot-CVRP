#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CoreSim.Math;

namespace CoreSim.IO
{
    public static class InstanceParser
    {
        // --- Sections we recognize ---
        private const string NODE_COORD_SECTION = "NODE_COORD_SECTION";
        private const string DEMAND_SECTION = "DEMAND_SECTION";
        private const string DEPOT_SECTION = "DEPOT_SECTION";
        private const string RELEASE_TIME_SECTION = "RELEASE_TIME_SECTION";

        private const string DEPOT_STOP_SECTION = "DEPOT_STOP_SECTION";
        private const string DEPOT_CANDIDATE_STOP_SECTION = "DEPOT_CANDIDATE_STOP_SECTION";

        // EVRP commonly uses this name but it often just lists station node IDs (coords are already in NODE_COORD_SECTION)
        private const string STATIONS_COORD_SECTION = "STATIONS_COORD_SECTION";

        private const string EOF = "EOF";

        public static InstanceDto ParseFromFile(string filePath)
        {
            string text = File.ReadAllText(filePath);
            var dto = ParseFromText(text);
            // You can store original path on dto later if you want; not required for now.
            return dto;
        }

        public static InstanceDto ParseFromText(string text)
        {
            var dto = new InstanceDto();

            var coords = new Dictionary<int, Vec2>();
            var demands = new Dictionary<int, int>();
            var releaseTimes = new Dictionary<int, float>();

            var depotStops = new List<DepotStopDto>();
            var depotNodeIds = new List<int>();
            var stationNodeIds = new List<int>();

            bool sawDemandSection = false;
            bool sawReleaseSection = false;
            bool sawDepotStopSection = false;
            bool sawStationsSection = false;

            bool sawEnergyHeader = false;

            using var reader = new StringReader(text);
            string? line;
            string currentSection = "";

            static string[] Tok(string s) =>
                s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                // comment stripping (keep simple; supports '#')
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                if (line.Length == 0) continue;

                // Section switches
                if (IsSectionHeader(line, out var sectionName))
                {
                    currentSection = sectionName;
                    if (currentSection == EOF) break;

                    if (currentSection == DEMAND_SECTION) sawDemandSection = true;
                    if (currentSection == RELEASE_TIME_SECTION) sawReleaseSection = true;
                    if (currentSection == DEPOT_STOP_SECTION || currentSection == DEPOT_CANDIDATE_STOP_SECTION) sawDepotStopSection = true;
                    if (currentSection == STATIONS_COORD_SECTION) sawStationsSection = true;

                    continue;
                }

                // Section parsing
                if (currentSection == NODE_COORD_SECTION)
                {
                    // <id> <x> <y>
                    var t = Tok(line);
                    if (t.Length >= 3 && int.TryParse(t[0], out int id))
                    {
                        float x = ParseFloat(t[1]);
                        float y = ParseFloat(t[2]);
                        coords[id] = new Vec2(x, y);
                    }
                    continue;
                }

                if (currentSection == DEMAND_SECTION)
                {
                    // <id> <demand>
                    var t = Tok(line);
                    if (t.Length >= 2 && int.TryParse(t[0], out int id))
                    {
                        int dem = (int)ParseFloat(t[1]);
                        demands[id] = dem;
                    }
                    continue;
                }

                if (currentSection == RELEASE_TIME_SECTION)
                {
                    // <id> <releaseTime> (optional terminator -1)
                    var t = Tok(line);
                    if (t.Length >= 1 && t[0] == "-1") { currentSection = ""; continue; }

                    if (t.Length >= 2 && int.TryParse(t[0], out int id))
                    {
                        float rt = ParseFloat(t[1]);
                        releaseTimes[id] = rt;
                    }
                    continue;
                }

                if (currentSection == DEPOT_STOP_SECTION || currentSection == DEPOT_CANDIDATE_STOP_SECTION)
                {
                    // Option B: <stop_id> <x> <y> (optional terminator -1)
                    var t = Tok(line);
                    if (t.Length >= 1 && t[0] == "-1") { currentSection = ""; continue; }

                    if (t.Length >= 3 && int.TryParse(t[0], out int sid))
                    {
                        float x = ParseFloat(t[1]);
                        float y = ParseFloat(t[2]);
                        depotStops.Add(new DepotStopDto(sid, new Vec2(x, y)));
                    }
                    continue;
                }

                if (currentSection == STATIONS_COORD_SECTION)
                {
                    // Many EVRP files list station node IDs here, one per line (no coords)
                    // e.g.:
                    // STATIONS_COORD_SECTION
                    // 23
                    // 24
                    // ...
                    var t = Tok(line);
                    if (t.Length >= 1)
                    {
                        // Some files may end with -1; tolerate it
                        if (t[0] == "-1") { currentSection = ""; continue; }

                        if (int.TryParse(t[0], out int stationId))
                            stationNodeIds.Add(stationId);
                    }
                    continue;
                }

                if (currentSection == DEPOT_SECTION)
                {
                    // list of depot node ids, terminated by -1
                    var t = Tok(line);
                    if (t.Length >= 1)
                    {
                        if (t[0] == "-1") { currentSection = ""; continue; }
                        if (int.TryParse(t[0], out int depId))
                            depotNodeIds.Add(depId);
                    }
                    continue;
                }

                // Header parsing (not in a section)
                if (TryParseHeader(line, out string key, out string value))
                {
                    ApplyHeader(dto, key, value, ref sawEnergyHeader);
                }
            }

            // Finalize dimension
            if (dto.Dimension <= 0)
            {
                foreach (var k in coords.Keys)
                    dto.Dimension = System.Math.Max(dto.Dimension, k);
            }

            dto.NodePos = new Vec2[dto.Dimension + 1];
            dto.Demand = new int[dto.Dimension + 1];
            dto.ReleaseTime = new float[dto.Dimension + 1];

            for (int i = 1; i <= dto.Dimension; i++)
            {
                dto.NodePos[i] = coords.TryGetValue(i, out var p) ? p : new Vec2(0, 0);
                dto.Demand[i] = demands.TryGetValue(i, out var d) ? d : 0;
                dto.ReleaseTime[i] = releaseTimes.TryGetValue(i, out var r) ? r : 0f;
            }

            dto.DepotNodeIds = depotNodeIds.Count > 0 ? depotNodeIds : new List<int> { 1 };

            dto.DepotCandidateStops = depotStops;
            dto.StationNodeIds = stationNodeIds;

            // Convenience default: if depot stops absent, add depot node position as a single candidate stop
            if (dto.DepotCandidateStops.Count == 0 && dto.DepotNodeIds.Count > 0)
            {
                int depotId = dto.DepotNodeIds[0];
                dto.DepotCandidateStops.Add(new DepotStopDto(1, dto.NodePos[depotId]));
            }

            // --- Detect features (authoritative; ignore TYPE correctness) ---
            dto.Features = DetectFeatures(
                dto,
                sawDemandSection: sawDemandSection,
                sawReleaseSection: sawReleaseSection,
                sawDepotStopSection: sawDepotStopSection,
                sawStationsSection: sawStationsSection,
                sawEnergyHeader: sawEnergyHeader
            );

            dto.DetectedProblemKind = GetProblemKindCode(dto.Features);

            return dto;
        }

        private static ProblemFeatures DetectFeatures(
            InstanceDto dto,
            bool sawDemandSection,
            bool sawReleaseSection,
            bool sawDepotStopSection,
            bool sawStationsSection,
            bool sawEnergyHeader
        )
        {
            ProblemFeatures f = ProblemFeatures.None;

            // Capacitated: capacity + demands section (or any nonzero demand)
            bool anyNonzeroDemand = false;
            if (dto.Demand != null)
            {
                for (int i = 1; i < dto.Demand.Length; i++)
                {
                    if (dto.Demand[i] != 0) { anyNonzeroDemand = true; break; }
                }
            }

            if (dto.Capacity > 0 && (sawDemandSection || anyNonzeroDemand))
                f |= ProblemFeatures.Capacitated;

            // Dynamic: release time section present OR any release time > 0
            bool anyRelease = false;
            if (dto.ReleaseTime != null)
            {
                for (int i = 1; i < dto.ReleaseTime.Length; i++)
                {
                    if (dto.ReleaseTime[i] > 0f) { anyRelease = true; break; }
                }
            }
            if (sawReleaseSection || anyRelease)
                f |= ProblemFeatures.Dynamic;

            // Moving depot: explicit depot stop section OR depot speed + more than 1 candidate stop (or any non-depot stop)
            bool hasMultipleStops = dto.DepotCandidateStops != null && dto.DepotCandidateStops.Count > 1;
            bool hasDepotMobilitySignal = (dto.DepotSpeed > 0f) && (sawDepotStopSection || hasMultipleStops);
            if (hasDepotMobilitySignal)
                f |= ProblemFeatures.MovingDepot;

            // Electric: energy headers OR stations section OR nonempty station list
            bool hasStations = sawStationsSection || (dto.StationNodeIds != null && dto.StationNodeIds.Count > 0);
            bool hasEnergy = sawEnergyHeader || dto.EnergyCapacity.HasValue || dto.EnergyConsumption.HasValue;
            if (hasStations || hasEnergy)
                f |= ProblemFeatures.Electric;

            return f;
        }

        /// <summary>
        /// Returns a compact stable code like:
        /// C, CD, CE, CDE, CDM, CEM, CDEM
        /// (Always starts with C if capacitated; else U for unknown.)
        /// </summary>
        public static string GetProblemKindCode(ProblemFeatures f)
        {
            if (!f.HasFlag(ProblemFeatures.Capacitated))
                return "U";

            string code = "C";
            if (f.HasFlag(ProblemFeatures.Electric)) code += "E";
            if (f.HasFlag(ProblemFeatures.Dynamic)) code += "D";
            if (f.HasFlag(ProblemFeatures.MovingDepot)) code += "M";
            return code;
        }

        /// <summary>
        /// Recommended instances subfolder name given detected features.
        /// Example: Instances/CDEM/...
        /// </summary>
        public static string GetSuggestedSubfolder(ProblemFeatures f)
        {
            // You can change naming here without touching parsing logic.
            return GetProblemKindCode(f);
        }

        private static bool IsSectionHeader(string line, out string sectionName)
        {
            sectionName = line.Trim().ToUpperInvariant();

            switch (sectionName)
            {
                case NODE_COORD_SECTION:
                case DEMAND_SECTION:
                case DEPOT_SECTION:
                case RELEASE_TIME_SECTION:
                case DEPOT_STOP_SECTION:
                case DEPOT_CANDIDATE_STOP_SECTION:
                case STATIONS_COORD_SECTION:
                case EOF:
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseHeader(string line, out string key, out string value)
        {
            key = "";
            value = "";

            int idx = line.IndexOf(':');
            if (idx < 0) return false;

            key = line.Substring(0, idx).Trim();
            value = line.Substring(idx + 1).Trim();
            return key.Length > 0;
        }

        private static void ApplyHeader(InstanceDto dto, string key, string value, ref bool sawEnergyHeader)
        {
            key = key.Trim().ToUpperInvariant();

            switch (key)
            {
                case "NAME":
                    dto.Name = value;
                    break;
                case "COMMENT":
                    dto.Comment = value;
                    break;
                case "TYPE":
                    dto.Type = value;
                    break;
                case "DIMENSION":
                    dto.Dimension = (int)ParseFloat(value);
                    break;
                case "CAPACITY":
                    dto.Capacity = (int)ParseFloat(value);
                    break;
                case "EDGE_WEIGHT_TYPE":
                    dto.EdgeWeightType = value;
                    break;

                // your additions
                case "TRUCK_SPEED":
                    dto.TruckSpeed = ParseFloat(value);
                    break;
                case "DEPOT_SPEED":
                    dto.DepotSpeed = ParseFloat(value);
                    break;

                // EVRP-ish additions
                case "ENERGY_CAPACITY":
                    dto.EnergyCapacity = ParseFloat(value);
                    sawEnergyHeader = true;
                    break;
                case "ENERGY_CONSUMPTION":
                    dto.EnergyConsumption = ParseFloat(value);
                    sawEnergyHeader = true;
                    break;
                case "STATIONS":
                    dto.StationCountHeader = (int)ParseFloat(value);
                    // implies electric in detection
                    break;
                case "VEHICLES":
                    dto.VehiclesHeader = (int)ParseFloat(value);
                    break;

                default:
                    // ignore unknown headers for forwards compatibility
                    break;
            }
        }

        private static float ParseFloat(string s)
        {
            return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}