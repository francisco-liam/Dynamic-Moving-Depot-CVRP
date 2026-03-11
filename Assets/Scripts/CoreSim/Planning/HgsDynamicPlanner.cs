#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CoreSim.Math;
using CoreSim.Model;

namespace CoreSim.Planning
{
    public sealed class HgsDynamicPlanner : IPlanner
    {
        public string SolverExecutablePath { get; set; } = "hgs_dynamic";
        public string InstancePath { get; set; } = string.Empty;
        public float SolverTimeBudgetSeconds { get; set; } = 1.0f;
        public float ProcessOverheadBufferSeconds { get; set; } = 0.25f;
        public float SafetyMarginSeconds { get; set; } = 0.25f;
        public bool IncludeVehicleActive { get; set; } = true;

        // ── Artifact storage ──────────────────────────────────────────────────
        /// <summary>Directory where solver input/output files are written and kept.
        /// Set this from Unity code to an absolute path (e.g. inside Assets/LocalConfig).
        /// Defaults to a 'solver-runs' folder next to the solver executable.</summary>
        public string ArtifactsDir { get; set; } = string.Empty;

        /// <summary>When true, the full command line, stdout, and stderr from the
        /// solver process are written to the Unity Console (Debug.Log → Console.Write).</summary>
        public bool LogSolverOutput { get; set; } = false;

        public PlanResult ComputePlan(SimState snapshot, PlanningContext ctx)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            if (string.IsNullOrWhiteSpace(InstancePath))
                return BuildNoOpResult(snapshot, "HgsDynamicPlanner: missing instance path.");

            if (string.IsNullOrWhiteSpace(SolverExecutablePath))
                return BuildNoOpResult(snapshot, "HgsDynamicPlanner: missing solver executable path.");

            var orderedTrucks = BuildOrderedTrucks(snapshot);
            var truckVehicleIndex = BuildVehicleIndex(orderedTrucks);

            var previousRoutes = new List<List<int>>(orderedTrucks.Count);
            var lockedPrefixLength = new List<int>(orderedTrucks.Count);
            var vehicleActive = new List<bool>(orderedTrucks.Count);

            for (int i = 0; i < orderedTrucks.Count; i++)
            {
                var truck = orderedTrucks[i];
                BuildRemainingRouteAndLock(snapshot, truck, ctx.CommitmentLockK, out var remainingRoute, out int lockLen);
                previousRoutes.Add(remainingRoute);
                lockedPrefixLength.Add(lockLen);
                vehicleActive.Add(true);
            }

            var customerActive = BuildCustomerActive(snapshot, ctx);

            // Detect customers not present in the original VRP file (IDs > original DIMENSION).
            // These need to be appended to a temp copy of the VRP so the solver can route them.
            int originalDimension = ParseDimensionFromVrp(InstancePath);
            var dynamicExtras = new List<Customer>();
            if (originalDimension > 0)
            {
                for (int i = 0; i < snapshot.Customers.Count; i++)
                {
                    var c = snapshot.Customers[i];
                    if (c.Id > originalDimension)
                        dynamicExtras.Add(c);
                }
            }

            string artifactsDir = string.IsNullOrEmpty(ArtifactsDir)
                ? Path.Combine(Path.GetDirectoryName(SolverExecutablePath) ?? Path.GetTempPath(), "solver-runs")
                : ArtifactsDir;
            Directory.CreateDirectory(artifactsDir);

            // Timestamp token: yyyyMMdd_HHmmss_fff so files sort chronologically.
            string runToken = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string jsonPath    = Path.Combine(artifactsDir, $"dynamic_{runToken}.json");
            string solPath     = Path.Combine(artifactsDir, $"dynamic_{runToken}.sol");
            string extVrpPath  = dynamicExtras.Count > 0
                                     ? Path.Combine(artifactsDir, $"extended_{runToken}.vrp")
                                     : null;

            if (extVrpPath != null)
            {
                WriteExtendedVrpFile(InstancePath, extVrpPath, dynamicExtras, originalDimension);
                Console.WriteLine($"[HGS] Extended VRP: dim {originalDimension} → {originalDimension + dynamicExtras.Count}  ({dynamicExtras.Count} dynamic extras)");
            }

            string solverVrpPath = extVrpPath ?? InstancePath;

            WriteDynamicJson(jsonPath, customerActive, lockedPrefixLength, previousRoutes, IncludeVehicleActive ? vehicleActive : null);

            float effectiveBudget = (ctx.SolverTimeBudgetSeconds > 0f)
                ? ctx.SolverTimeBudgetSeconds
                : SolverTimeBudgetSeconds;

            double leadTime = System.Math.Max(0.0, effectiveBudget)
                            + System.Math.Max(0.0, ProcessOverheadBufferSeconds)
                            + System.Math.Max(0.0, SafetyMarginSeconds);

            int exitCode;
            bool timedOut;
            string stdOut;
            string stdErr;

            string args = BuildArguments(solverVrpPath, jsonPath, solPath, orderedTrucks.Count, effectiveBudget);
            Console.WriteLine($"[HGS] SolverBudget={effectiveBudget:0.###}s (ctx={ctx.SolverTimeBudgetSeconds:0.###} prop={SolverTimeBudgetSeconds:0.###})");
            Console.WriteLine($"[HGS] CMD: {SolverExecutablePath} {args}");
            Console.WriteLine($"[HGS] ArtifactsDir: {artifactsDir}");
            Console.WriteLine($"[HGS] InputJSON: {Path.GetFileName(jsonPath)}  OutputSOL: {Path.GetFileName(solPath)}");

            try
            {
                RunProcess(SolverExecutablePath, args, leadTime, out exitCode, out timedOut, out stdOut, out stdErr);
            }
            catch (Exception ex)
            {
                return BuildNoOpResult(snapshot, $"HgsDynamicPlanner: solver process failed ({ex.Message}).");
            }

            bool solExists = File.Exists(solPath);
            long solBytes  = solExists ? new FileInfo(solPath).Length : 0;
            Console.WriteLine($"[HGS] exitCode={exitCode} timedOut={timedOut}  sol_exists={solExists} sol_bytes={solBytes}");
            if (LogSolverOutput || exitCode != 0 || timedOut)
            {
                if (!string.IsNullOrWhiteSpace(stdOut)) Console.WriteLine($"[HGS-STDOUT]\n{stdOut.Trim()}");
                if (!string.IsNullOrWhiteSpace(stdErr)) Console.WriteLine($"[HGS-STDERR]\n{stdErr.Trim()}");
            }

            if (timedOut)
            {
                Console.WriteLine($"[HGS] Solver timed out — artifacts kept: {artifactsDir}");
                return BuildNoOpResult(snapshot, "HgsDynamicPlanner: solver timeout.");
            }

            if (exitCode != 0)
            {
                Console.WriteLine($"[HGS] Solver exited non-zero — artifacts kept: {artifactsDir}");
                return BuildNoOpResult(snapshot, BuildFailureSummary("HgsDynamicPlanner: solver exited non-zero.", exitCode, stdOut, stdErr));
            }

            if (!File.Exists(solPath))
            {
                Console.WriteLine($"[HGS] Missing solution output — artifacts kept: {artifactsDir}");
                return BuildNoOpResult(snapshot, "HgsDynamicPlanner: missing solution output.");
            }

            Dictionary<int, List<int>> solvedByVehicle;
            try
            {
                solvedByVehicle = ParseSolutionByVehicle(solPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HGS] Failed to parse solution — artifacts kept: {artifactsDir}");
                return BuildNoOpResult(snapshot, $"HgsDynamicPlanner: failed to parse solution ({ex.Message}).");
            }

            var result = new PlanResult();
            int totalAssignedCustomers = 0;

            for (int i = 0; i < orderedTrucks.Count; i++)
            {
                var truck = orderedTrucks[i];
                int vehicleIndex = truckVehicleIndex[truck.Id];
                solvedByVehicle.TryGetValue(vehicleIndex, out var solvedRoute);
                solvedRoute ??= new List<int>();

                int currentIndex = Clamp(truck.CurrentTargetIndex, 0, truck.Plan.Count);
                var fullPlan = new List<TargetRef>(currentIndex + solvedRoute.Count);

                for (int h = 0; h < currentIndex; h++)
                    fullPlan.Add(truck.Plan[h]);

                for (int r = 0; r < solvedRoute.Count; r++)
                {
                    // Solver outputs internal indices (0-based, depot=0, customers=1..nbClients).
                    // Convert back to Unity customer ID (= VRP node ID = internalIdx + 1).
                    fullPlan.Add(TargetRef.Customer(solvedRoute[r] + 1));
                    totalAssignedCustomers += 1;
                }

                AppendDepotReturn(fullPlan);

                result.TruckPlans[truck.Id] = fullPlan;
            }

            result.DebugSummary = $"HgsDynamicPlanner: vehicles={orderedTrucks.Count}, assignedCustomers={totalAssignedCustomers}, exitCode={exitCode}";
            Console.WriteLine($"[HGS] OK — assignedCustomers={totalAssignedCustomers} vehicles={orderedTrucks.Count}");
            Console.WriteLine($"[HGS] Artifacts: {artifactsDir}");
            return result;
        }

        private static List<Truck> BuildOrderedTrucks(SimState snapshot)
        {
            var trucks = new List<Truck>(snapshot.Trucks);
            trucks.Sort((a, b) => a.Id.CompareTo(b.Id));
            return trucks;
        }

        private static Dictionary<int, int> BuildVehicleIndex(List<Truck> orderedTrucks)
        {
            var map = new Dictionary<int, int>(orderedTrucks.Count);
            for (int i = 0; i < orderedTrucks.Count; i++)
                map[orderedTrucks[i].Id] = i;
            return map;
        }

        private static void BuildRemainingRouteAndLock(SimState snapshot, Truck truck, int commitmentLockK, out List<int> remainingRoute, out int lockLen)
        {
            int currentIndex = Clamp(truck.CurrentTargetIndex, 0, truck.Plan.Count);
            int remaining = truck.Plan.Count - currentIndex;
            int lockedTargets = remaining > 0 ? System.Math.Min(remaining, 1 + System.Math.Max(0, commitmentLockK)) : 0;

            remainingRoute = new List<int>(remaining);
            lockLen = 0;

            for (int i = currentIndex; i < truck.Plan.Count; i++)
            {
                var target = truck.Plan[i];
                if (target.Type != TargetType.Customer)
                    continue;

                if (snapshot.GetCustomerById(target.Id) == null)
                    continue;

                // Convert Unity customer ID (VRP node ID) to solver internal index: internalIdx = nodeId - 1
                int internalIdx = target.Id - 1;
                remainingRoute.Add(internalIdx);
                if (i < currentIndex + lockedTargets)
                    lockLen += 1;
            }

            if (remainingRoute.Count > 0 && lockedTargets > 0 && lockLen == 0)
                lockLen = 1;

            if (lockLen > remainingRoute.Count)
                lockLen = remainingRoute.Count;
        }

        private static bool[] BuildCustomerActive(SimState snapshot, PlanningContext ctx)
        {
            // The HGS solver uses 0-based internal indices:
            //   index 0       = depot (VRP node 1, always false)
            //   index k (k≥1) = customer whose VRP node ID is k+1 (internalIdx = vrpNodeId - 1)
            // Array size must be exactly nbClients + 1 = maxUnityCustomerId
            // (since depot=node1, customers=node2..nodeN, nbClients=N-1, maxId=N, size=N=maxId)
            int maxId = 0;
            for (int i = 0; i < snapshot.Customers.Count; i++)
                maxId = System.Math.Max(maxId, snapshot.Customers[i].Id);

            // size = maxId = nbClients + 1 (correct: internalIdx range is 0..maxId-1)
            var active = new bool[maxId];
            // active[0] stays false (depot slot)

            for (int i = 0; i < snapshot.Customers.Count; i++)
            {
                var c = snapshot.Customers[i];
                bool isReleasedUnserved = c.Status == CustomerStatus.Waiting;
                bool includeUnreleased = !ctx.RespectReleaseTime && c.Status == CustomerStatus.Unreleased;

                // Convert VRP node ID to solver internal index: internalIdx = vrpNodeId - 1
                int internalIdx = c.Id - 1;
                if (internalIdx > 0 && internalIdx < active.Length)
                    active[internalIdx] = isReleasedUnserved || includeUnreleased;
            }

            return active;
        }

        private static void WriteDynamicJson(
            string jsonPath,
            bool[] customerActive,
            List<int> lockedPrefixLength,
            List<List<int>> previousRoutes,
            List<bool>? vehicleActive)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("{");
            sb.Append("  \"customerActive\": ");
            AppendBoolArray(sb, customerActive);
            sb.AppendLine(",");

            sb.Append("  \"lockedPrefixLength\": ");
            AppendIntArray(sb, lockedPrefixLength);
            sb.AppendLine(",");

            sb.Append("  \"previousRoutes\": ");
            AppendRouteMatrix(sb, previousRoutes);

            if (vehicleActive != null)
            {
                sb.AppendLine(",");
                sb.Append("  \"vehicleActive\": ");
                AppendBoolArray(sb, vehicleActive);
            }
            sb.AppendLine();
            sb.AppendLine("}");

            File.WriteAllText(jsonPath, sb.ToString());
        }

        private static string BuildArguments(string instancePath, string jsonPath, string solPath, int vehicleCount, float timeBudgetSeconds)
        {
            string t = System.Math.Max(0f, timeBudgetSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} -veh {3} -t {4} -log 0",
                Quote(instancePath),
                Quote(jsonPath),
                Quote(solPath),
                vehicleCount,
                t);
        }

        private static void RunProcess(
            string exePath,
            string arguments,
            double plannerLeadTimeSeconds,
            out int exitCode,
            out bool timedOut,
            out string stdOut,
            out string stdErr)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = psi };

            process.Start();

            // Read stdout and stderr concurrently to avoid pipe-buffer deadlock.
            var stdOutTask = System.Threading.Tasks.Task.Run(() => process.StandardOutput.ReadToEnd());
            var stdErrTask = System.Threading.Tasks.Task.Run(() => process.StandardError.ReadToEnd());

            int timeoutMs = (int)System.Math.Ceiling((System.Math.Max(2.0, plannerLeadTimeSeconds) + 2.0) * 1000.0);
            bool exited = process.WaitForExit(timeoutMs);

            if (!exited)
            {
                timedOut = true;
                exitCode = -1;
                try { process.Kill(); } catch { }
                stdOut = string.Empty;
                stdErr = string.Empty;
                return;
            }

            timedOut = false;
            exitCode = process.ExitCode;
            stdOut = stdOutTask.Result;
            stdErr = stdErrTask.Result;
        }

        private static Dictionary<int, List<int>> ParseSolutionByVehicle(string solPath)
        {
            var result = new Dictionary<int, List<int>>();
            var lines = File.ReadAllLines(solPath);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("Route #", StringComparison.OrdinalIgnoreCase))
                    continue;

                int hash = line.IndexOf('#');
                int colon = line.IndexOf(':');
                if (hash < 0 || colon < 0 || colon <= hash + 1)
                    continue;

                string routeIndexText = line.Substring(hash + 1, colon - hash - 1).Trim();
                if (!int.TryParse(routeIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oneBased))
                    continue;

                int vehicleIndex = oneBased - 1;
                var route = new List<int>();
                string rhs = line.Substring(colon + 1).Trim();
                if (!string.IsNullOrEmpty(rhs))
                {
                    string[] tokens = rhs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    for (int t = 0; t < tokens.Length; t++)
                    {
                        if (int.TryParse(tokens[t], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeId))
                            route.Add(nodeId);
                    }
                }

                result[vehicleIndex] = route;
            }

            return result;
        }

        private static PlanResult BuildNoOpResult(SimState snapshot, string summary)
        {
            var result = new PlanResult { DebugSummary = summary };

            for (int i = 0; i < snapshot.Trucks.Count; i++)
            {
                var truck = snapshot.Trucks[i];
                result.TruckPlans[truck.Id] = new List<TargetRef>(truck.Plan);
            }

            return result;
        }

        private static string BuildFailureSummary(string prefix, int exitCode, string stdOut, string stdErr)
        {
            string outLine = FirstNonEmptyLine(stdOut);
            string errLine = FirstNonEmptyLine(stdErr);
            if (string.IsNullOrEmpty(outLine) && string.IsNullOrEmpty(errLine))
                return $"{prefix} exitCode={exitCode}";

            return $"{prefix} exitCode={exitCode}; out='{outLine}'; err='{errLine}'";
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return lines[i].Trim();
            }
            return string.Empty;
        }

        private static void CleanupArtifacts(string jsonPath, string solPath, string extVrpPath = null)
        {
            try
            {
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
                if (File.Exists(solPath)) File.Delete(solPath);
                if (extVrpPath != null && File.Exists(extVrpPath)) File.Delete(extVrpPath);
            }
            catch
            {
                // Best effort cleanup; safe to ignore failures.
            }
        }

        /// <summary>
        /// Reads the DIMENSION value from the header of a TSPLIB .vrp file.
        /// Returns 0 if not found.
        /// </summary>
        private static int ParseDimensionFromVrp(string vrpPath)
        {
            using var reader = new StreamReader(vrpPath);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string t = line.Trim().ToUpperInvariant();
                if (t.StartsWith("DIMENSION", StringComparison.Ordinal))
                {
                    int colon = t.IndexOf(':');
                    string numPart = colon >= 0 ? t.Substring(colon + 1).Trim() : t.Substring(9).Trim();
                    if (int.TryParse(numPart, System.Globalization.NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out int dim))
                        return dim;
                }
                if (t.StartsWith("NODE_COORD", StringComparison.Ordinal) ||
                    t.StartsWith("DEMAND_SECTION", StringComparison.Ordinal))
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Writes a copy of <paramref name="originalVrpPath"/> extended with additional customer nodes
        /// appended to NODE_COORD_SECTION and DEMAND_SECTION.
        /// New nodes receive sequential VRP node IDs starting at originalDimension + 1.
        /// </summary>
        private static void WriteExtendedVrpFile(
            string originalVrpPath,
            string tempVrpPath,
            IReadOnlyList<Customer> dynamicExtras,
            int originalDimension)
        {
            int newDimension = originalDimension + dynamicExtras.Count;
            var lines = File.ReadAllLines(originalVrpPath);
            var sb = new StringBuilder(lines.Length * 32 + dynamicExtras.Count * 64);

            bool insertedCoords  = false;
            bool insertedDemands = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string upper = line.Trim().ToUpperInvariant();

                // Patch DIMENSION header line
                if (upper.StartsWith("DIMENSION", StringComparison.Ordinal) && !insertedCoords)
                {
                    int colon = line.IndexOf(':');
                    sb.AppendLine(colon >= 0
                        ? line.Substring(0, colon + 1) + " " + newDimension
                        : "DIMENSION : " + newDimension);
                    continue;
                }

                // Append extra coord rows just before DEMAND_SECTION
                if (upper == "DEMAND_SECTION" && !insertedCoords)
                {
                    for (int j = 0; j < dynamicExtras.Count; j++)
                    {
                        int nodeId = originalDimension + 1 + j;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "{0}\t{1:0.######}\t{2:0.######}",
                            nodeId, dynamicExtras[j].Pos.X, dynamicExtras[j].Pos.Y));
                    }
                    insertedCoords = true;
                }

                // Append extra demand rows just before DEPOT_SECTION
                if (upper == "DEPOT_SECTION" && !insertedDemands)
                {
                    for (int j = 0; j < dynamicExtras.Count; j++)
                    {
                        int nodeId = originalDimension + 1 + j;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1}", nodeId, dynamicExtras[j].Demand));
                    }
                    insertedDemands = true;
                }

                sb.AppendLine(line);
            }

            File.WriteAllText(tempVrpPath, sb.ToString(), System.Text.Encoding.UTF8);
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";

            if (value.IndexOf(' ') < 0 && value.IndexOf('\t') < 0)
                return value;

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void AppendIntArray(StringBuilder sb, List<int> values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
        }

        private static void AppendBoolArray(StringBuilder sb, bool[] values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i] ? "true" : "false");
            }
            sb.Append(']');
        }

        private static void AppendBoolArray(StringBuilder sb, List<bool> values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i] ? "true" : "false");
            }
            sb.Append(']');
        }

        private static void AppendRouteMatrix(StringBuilder sb, List<List<int>> routes)
        {
            sb.Append('[');
            for (int i = 0; i < routes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendIntArray(sb, routes[i]);
            }
            sb.Append(']');
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static void AppendDepotReturn(List<TargetRef> plan)
        {
            if (plan.Count == 0)
                return;

            var last = plan[plan.Count - 1];
            if (last.Type == TargetType.Depot)
                return;

            plan.Add(TargetRef.Depot());
        }
    }
}