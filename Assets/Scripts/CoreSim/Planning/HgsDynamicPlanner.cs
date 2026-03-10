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

            string tempDir = Path.Combine(Path.GetTempPath(), "dynamic-cvrp-hgs");
            Directory.CreateDirectory(tempDir);

            string runToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            string jsonPath = Path.Combine(tempDir, $"dynamic_{runToken}.json");
            string solPath = Path.Combine(tempDir, $"dynamic_{runToken}.sol");

            WriteDynamicJson(jsonPath, customerActive, lockedPrefixLength, previousRoutes, IncludeVehicleActive ? vehicleActive : null);

            double leadTime = System.Math.Max(0.0, SolverTimeBudgetSeconds)
                            + System.Math.Max(0.0, ProcessOverheadBufferSeconds)
                            + System.Math.Max(0.0, SafetyMarginSeconds);

            int exitCode;
            bool timedOut;
            string stdOut;
            string stdErr;

            string args = BuildArguments(InstancePath, jsonPath, solPath, orderedTrucks.Count, SolverTimeBudgetSeconds);
            Console.WriteLine($"[HGS] start exe={SolverExecutablePath} t={SolverTimeBudgetSeconds:0.###}s");

            try
            {
                RunProcess(SolverExecutablePath, args, leadTime, out exitCode, out timedOut, out stdOut, out stdErr);
            }
            catch (Exception ex)
            {
                CleanupArtifacts(jsonPath, solPath);
                return BuildNoOpResult(snapshot, $"HgsDynamicPlanner: solver process failed ({ex.Message}).");
            }

            Console.WriteLine($"[HGS] end exitCode={exitCode} timedOut={timedOut}");

            if (timedOut)
            {
                CleanupArtifacts(jsonPath, solPath);
                return BuildNoOpResult(snapshot, "HgsDynamicPlanner: solver timeout.");
            }

            if (exitCode != 0)
            {
                CleanupArtifacts(jsonPath, solPath);
                return BuildNoOpResult(snapshot, BuildFailureSummary("HgsDynamicPlanner: solver exited non-zero.", exitCode, stdOut, stdErr));
            }

            if (!File.Exists(solPath))
            {
                CleanupArtifacts(jsonPath, solPath);
                return BuildNoOpResult(snapshot, "HgsDynamicPlanner: missing solution output.");
            }

            Dictionary<int, List<int>> solvedByVehicle;
            try
            {
                solvedByVehicle = ParseSolutionByVehicle(solPath);
            }
            catch (Exception ex)
            {
                CleanupArtifacts(jsonPath, solPath);
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
                    fullPlan.Add(TargetRef.Customer(solvedRoute[r]));
                    totalAssignedCustomers += 1;
                }

                result.TruckPlans[truck.Id] = fullPlan;
            }

            result.DebugSummary = $"HgsDynamicPlanner: vehicles={orderedTrucks.Count}, assignedCustomers={totalAssignedCustomers}, exitCode={exitCode}";
            CleanupArtifacts(jsonPath, solPath);
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

                remainingRoute.Add(target.Id);
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
            int maxId = 0;
            for (int i = 0; i < snapshot.Customers.Count; i++)
                maxId = System.Math.Max(maxId, snapshot.Customers[i].Id);

            var active = new bool[maxId + 1];
            if (active.Length > 0)
                active[0] = false;

            for (int i = 0; i < snapshot.Customers.Count; i++)
            {
                var c = snapshot.Customers[i];
                bool isReleasedUnserved = c.Status == CustomerStatus.Waiting;
                bool includeUnreleased = !ctx.RespectReleaseTime && c.Status == CustomerStatus.Unreleased;

                if (c.Id >= 0 && c.Id < active.Length)
                    active[c.Id] = isReleasedUnserved || includeUnreleased;
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
            stdOut = process.StandardOutput.ReadToEnd();
            stdErr = process.StandardError.ReadToEnd();

            int timeoutMs = (int)System.Math.Ceiling((System.Math.Max(2.0, plannerLeadTimeSeconds) + 2.0) * 1000.0);
            bool exited = process.WaitForExit(timeoutMs);

            if (!exited)
            {
                timedOut = true;
                exitCode = -1;
                try { process.Kill(); } catch { }
                return;
            }

            timedOut = false;
            exitCode = process.ExitCode;
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

        private static void CleanupArtifacts(string jsonPath, string solPath)
        {
            try
            {
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
                if (File.Exists(solPath)) File.Delete(solPath);
            }
            catch
            {
                // Best effort cleanup; safe to ignore failures.
            }
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
    }
}