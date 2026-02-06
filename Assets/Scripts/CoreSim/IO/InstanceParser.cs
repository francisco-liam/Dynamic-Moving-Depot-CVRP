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
        // Section keywords we recognize
        private const string NODE_COORD_SECTION = "NODE_COORD_SECTION";
        private const string DEMAND_SECTION = "DEMAND_SECTION";
        private const string DEPOT_SECTION = "DEPOT_SECTION";
        private const string RELEASE_TIME_SECTION = "RELEASE_TIME_SECTION";
        private const string DEPOT_STOP_SECTION = "DEPOT_STOP_SECTION";
        private const string DEPOT_CANDIDATE_STOP_SECTION = "DEPOT_CANDIDATE_STOP_SECTION";
        private const string EOF = "EOF";

        public static InstanceDto ParseFromFile(string filePath)
        {
            string text = File.ReadAllText(filePath);
            return ParseFromText(text);
        }

        public static InstanceDto ParseFromText(string text)
        {
            var dto = new InstanceDto();

            // We’ll store node coords/demands in temp dictionaries until we know dimension (or to be safe)
            var coords = new Dictionary<int, Vec2>();
            var demands = new Dictionary<int, int>();
            var releaseTimes = new Dictionary<int, float>();
            var depotStops = new List<DepotStopDto>();
            var depotNodeIds = new List<int>();

            using var reader = new StringReader(text);
            string? line;
            string currentSection = "";

            // helper: split on whitespace/tabs, remove empties
            static string[] Tok(string s) =>
                s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                // Handle comments (TSPLIB doesn't standardize, but many use this)
                // If you use '#', this helps.
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                if (line.Length == 0) continue;

                // Section switches
                if (IsSectionHeader(line, out var sectionName))
                {
                    currentSection = sectionName;
                    if (currentSection == EOF) break;
                    continue;
                }

                // Parse based on section
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
                    // <id> <releaseTime>   (ends on -1 optionally)
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
                    // Option B: <stop_id> <x> <y>
                    // May optionally end with -1, OR end when next section header appears.
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

                // If not in a section: parse header key:value lines
                // Accept "KEY : value" or "KEY: value"
                if (TryParseHeader(line, out string key, out string value))
                {
                    ApplyHeader(dto, key, value);
                }
            }

            // Finalize + allocate arrays based on Dimension
            if (dto.Dimension <= 0)
            {
                // If not present, infer from coords max
                foreach (var k in coords.Keys)
                    dto.Dimension = System.Math.Max(dto.Dimension, k);
            }

            dto.NodePos = new Vec2[dto.Dimension + 1];
            dto.Demand = new int[dto.Dimension + 1];
            dto.ReleaseTime = new float[dto.Dimension + 1];

            // defaults
            for (int i = 1; i <= dto.Dimension; i++)
            {
                dto.NodePos[i] = coords.TryGetValue(i, out var p) ? p : new Vec2(0, 0);
                dto.Demand[i] = demands.TryGetValue(i, out var d) ? d : 0;
                dto.ReleaseTime[i] = releaseTimes.TryGetValue(i, out var r) ? r : 0f;
            }

            dto.DepotNodeIds = depotNodeIds.Count > 0 ? depotNodeIds : new List<int> { 1 };
            dto.DepotCandidateStops = depotStops;

            // If candidate stops are empty, you can optionally add node1 as a default candidate stop
            // (handy for your "stop 1 is the depot" convention)
            if (dto.DepotCandidateStops.Count == 0 && dto.DepotNodeIds.Count > 0)
            {
                int depotId = dto.DepotNodeIds[0];
                dto.DepotCandidateStops.Add(new DepotStopDto(1, dto.NodePos[depotId]));
            }

            return dto;
        }

        private static bool IsSectionHeader(string line, out string sectionName)
        {
            sectionName = line.Trim();
            // TSPLIB often uses exact section headers on a line by itself
            switch (sectionName)
            {
                case NODE_COORD_SECTION:
                case DEMAND_SECTION:
                case DEPOT_SECTION:
                case RELEASE_TIME_SECTION:
                case DEPOT_STOP_SECTION:
                case DEPOT_CANDIDATE_STOP_SECTION:
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

        private static void ApplyHeader(InstanceDto dto, string key, string value)
        {
            // normalize
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

                default:
                    // ignore unknown headers for forwards compatibility
                    break;
            }
        }

        private static float ParseFloat(string s)
        {
            // handle tabs/spaces etc already trimmed
            return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}