using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CoreSim.Math;
using CoreSim.Model;
using UnityEngine;

public sealed class BenchmarkBatchRunner : MonoBehaviour
{
    [Header("References")]
    public SimViewController controller;

    [Header("Run Mode")]
    public bool runOnStart = false;
    public bool runAllBenchmarks = true;
    public string singleScenarioFile = "X-n143-k7.json";

    [Header("Folders")]
    public string benchmarksFolder = "Assets/HGS-Dynamic-CVRP/Generated Benchmarks";
    public string instancesFolder = "Assets/HGS-Dynamic-CVRP/Instances/CVRP";
    public string solutionsFolder = "Assets/HGS-Dynamic-CVRP/Solutions";
    public string outputFolder = "Assets/HGS-Dynamic-CVRP/Run Results";

    [Header("Batch Settings")]
    public int runsPerBenchmark = 10;
    public int randomSeedBase = 12345;
    public bool forceHgsPlanner = true;
    public bool forceAutoReplan = true;

    [Header("Dynamic Insert Settings")]
    public float insertedCustomerServiceTime = 1f;

    [Header("Visualization")]
    public bool forceShowRoutes = true;
    public float runSpeedMultiplier = 20f;

    [Header("Performance")]
    public bool disablePerEventLogs = true;
    public float runFixedStep = 0.2f;
    public int runMaxSimStepsPerFrame = 20;
    public bool disableUiOverlaysDuringBatch = true;

    [Header("Vehicle")]
    public float runTruckSpeedOverride = 12f;

    [Header("Completion")]
    public float depotArrivalTolerance = 0.5f;

    private readonly Queue<RunWorkItem> _pendingRuns = new Queue<RunWorkItem>();
    private readonly List<ScenarioCustomerDto> _pendingInserts = new List<ScenarioCustomerDto>();

    private RunWorkItem _activeWorkItem;
    private BenchmarkScenarioDto _activeScenario;
    private int _nextInsertIndex;
    private bool _runActive;
    private bool _dynamicReplanActivated;
    private Stopwatch _wallClock;

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
    }

    private void Start()
    {
        if (runOnStart)
        {
            UnityEngine.Debug.Log("[BenchmarkRunner] runOnStart=true, beginning batch.");
            BeginBatch();
        }
        else
        {
            UnityEngine.Debug.Log("[BenchmarkRunner] runOnStart=false. Use component context menu: Begin Batch.");
        }
    }

    private void Update()
    {
        if (!_runActive || controller == null || controller.State == null || controller.Simulation == null)
            return;

        ProcessDueInsertions();

        if (!IsRunComplete())
            return;

        FinalizeActiveRun();
        StartNextRun();
    }

    [ContextMenu("Begin Batch")]
    public void BeginBatch()
    {
        if (controller == null)
        {
            UnityEngine.Debug.LogError("[BenchmarkRunner] Missing SimViewController reference.");
            return;
        }

        _pendingRuns.Clear();
        BuildRunQueue();

        if (_pendingRuns.Count == 0)
        {
            UnityEngine.Debug.LogWarning("[BenchmarkRunner] No runs queued.");
            return;
        }

        StartNextRun();
    }

    private void BuildRunQueue()
    {
        string benchmarkRoot = ResolvePath(benchmarksFolder);
        if (!Directory.Exists(benchmarkRoot))
        {
            UnityEngine.Debug.LogError($"[BenchmarkRunner] Benchmarks folder not found: {benchmarkRoot}");
            return;
        }

        var scenarioFiles = new List<string>();
        if (runAllBenchmarks)
        {
            string[] candidates = Directory.GetFiles(benchmarkRoot, "*.json", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (TryLoadScenario(candidate, out _))
                    scenarioFiles.Add(candidate);
                else
                    UnityEngine.Debug.Log($"[BenchmarkRunner] Skipping non-scenario JSON: {Path.GetFileName(candidate)}");
            }
        }
        else
        {
            string scenarioPath = ResolveScenarioPath(benchmarkRoot, singleScenarioFile);
            if (!File.Exists(scenarioPath))
            {
                UnityEngine.Debug.LogError($"[BenchmarkRunner] Scenario file not found: {scenarioPath}");
                return;
            }

            if (!TryLoadScenario(scenarioPath, out _))
            {
                UnityEngine.Debug.LogError($"[BenchmarkRunner] Scenario JSON schema invalid: {scenarioPath}");
                return;
            }

            scenarioFiles.Add(scenarioPath);
        }

        scenarioFiles.Sort(StringComparer.OrdinalIgnoreCase);

        int runCountPerScenario = System.Math.Max(1, runsPerBenchmark);
        var rng = new System.Random(unchecked(randomSeedBase ^ Environment.TickCount));

        for (int i = 0; i < scenarioFiles.Count; i++)
        {
            string scenarioPath = scenarioFiles[i];
            string baseName = Path.GetFileNameWithoutExtension(scenarioPath);

            for (int runIndex = 1; runIndex <= runCountPerScenario; runIndex++)
            {
                int seed = rng.Next(int.MinValue, int.MaxValue);
                _pendingRuns.Enqueue(new RunWorkItem
                {
                    ScenarioPath = scenarioPath,
                    BaseName = baseName,
                    RunIndex = runIndex,
                    Seed = seed
                });
            }
        }

        UnityEngine.Debug.Log($"[BenchmarkRunner] Queued {_pendingRuns.Count} runs.");
    }

    private void StartNextRun()
    {
        _runActive = false;
        _activeScenario = null;
        _pendingInserts.Clear();
        _nextInsertIndex = 0;
        _dynamicReplanActivated = false;

        if (_pendingRuns.Count == 0)
        {
            UnityEngine.Debug.Log("[BenchmarkRunner] Batch complete.");
            return;
        }

        _activeWorkItem = _pendingRuns.Dequeue();
        if (!TryLoadScenario(_activeWorkItem.ScenarioPath, out _activeScenario))
        {
            UnityEngine.Debug.LogError($"[BenchmarkRunner] Failed to parse scenario: {_activeWorkItem.ScenarioPath}");
            StartNextRun();
            return;
        }

        string instancePath = ResolvePath(Path.Combine(instancesFolder, _activeScenario.base_instance + ".vrp"));
        string staticSolPath = ResolvePath(Path.Combine(solutionsFolder, _activeScenario.base_instance + ".sol"));

        if (!File.Exists(instancePath))
        {
            UnityEngine.Debug.LogError($"[BenchmarkRunner] Missing instance file: {instancePath}");
            StartNextRun();
            return;
        }

        if (!File.Exists(staticSolPath))
        {
            UnityEngine.Debug.LogError($"[BenchmarkRunner] Missing static solution file: {staticSolPath}");
            StartNextRun();
            return;
        }

        if (forceHgsPlanner)
            controller.useHgsDynamicPlanner = true;

        if (forceAutoReplan)
            controller.autoReplan = false;

        if (_activeScenario.vehicle_count > 0)
            controller.demoTruckCount = _activeScenario.vehicle_count;

        controller.autoAssignDemoPlan = false;
        controller.instancePath = instancePath;

        if (disablePerEventLogs)
            controller.logEvents = false;

        if (runFixedStep > 0f)
            controller.fixedStep = runFixedStep;

        if (runMaxSimStepsPerFrame > 0)
            controller.maxSimStepsPerFrame = runMaxSimStepsPerFrame;

        controller.runStartupReplan = false;
        controller.ResetSim(_activeWorkItem.Seed, instancePath);

        if (runTruckSpeedOverride > 0f && controller.State != null)
        {
            for (int i = 0; i < controller.State.Trucks.Count; i++)
                controller.State.Trucks[i].Speed = runTruckSpeedOverride;
        }

        if (disableUiOverlaysDuringBatch)
            DisableUiOverlays();

        if (runSpeedMultiplier > 0f)
            controller.SetSpeedMultiplier(runSpeedMultiplier);

        if (forceShowRoutes && controller.simRenderer != null)
            controller.simRenderer.SetShowRoutes(true);

        if (controller.cameraController != null)
            controller.cameraController.FrameState(controller.State);

        bool seeded = SeedInitialRoutesFromSolution(staticSolPath);
        if (!seeded)
        {
            UnityEngine.Debug.LogError($"[BenchmarkRunner] Failed to seed static routes from: {staticSolPath}");
            StartNextRun();
            return;
        }

        UnityEngine.Debug.Log($"[BenchmarkRunner] Seeded static routes from {staticSolPath}");

        if (_activeScenario.new_customers != null)
        {
            _pendingInserts.AddRange(_activeScenario.new_customers);
            _pendingInserts.Sort((a, b) =>
            {
                int t = a.reveal_time.CompareTo(b.reveal_time);
                if (t != 0) return t;
                return a.id.CompareTo(b.id);
            });
        }

        _wallClock = Stopwatch.StartNew();
        controller.Play();
        _runActive = true;

        UnityEngine.Debug.Log($"[BenchmarkRunner] Active instance={instancePath}");
        UnityEngine.Debug.Log($"[BenchmarkRunner] State customers={controller.State.Customers.Count} trucks={controller.State.Trucks.Count}");
        UnityEngine.Debug.Log($"[BenchmarkRunner] Truck speed override={runTruckSpeedOverride:0.###}");
        UnityEngine.Debug.Log("[BenchmarkRunner] AutoReplan is OFF until first dynamic insertion.");
        UnityEngine.Debug.Log($"[BenchmarkRunner] Start {_activeWorkItem.BaseName} run={_activeWorkItem.RunIndex} seed={_activeWorkItem.Seed}");
    }

    private void ProcessDueInsertions()
    {
        if (_activeScenario == null || _pendingInserts.Count == 0)
            return;

        float now = controller.State.Time;

        while (_nextInsertIndex < _pendingInserts.Count)
        {
            var customer = _pendingInserts[_nextInsertIndex];
            if (customer.reveal_time > now)
                break;

            var spec = new CustomerSpec(new Vec2((float)customer.x, (float)customer.y))
            {
                Demand = customer.demand,
                ReleaseTime = customer.reveal_time,
                ServiceTime = insertedCustomerServiceTime
            };

            controller.InsertCustomer(spec);
            _nextInsertIndex += 1;

            if (!_dynamicReplanActivated && forceAutoReplan)
            {
                controller.SetAutoReplan(true);
                controller.ReplanNow();
                _dynamicReplanActivated = true;
                UnityEngine.Debug.Log($"[BenchmarkRunner] Dynamic replanning activated at sim t={now:0.###}");
            }
        }
    }

    private bool SeedInitialRoutesFromSolution(string solutionPath)
    {
        if (controller.State == null)
            return false;

        var routes = ParseSolutionRoutes(solutionPath);
        if (routes.Count == 0)
            return false;

        UnityEngine.Debug.Log($"[BenchmarkRunner] Parsed {routes.Count} route lines from {solutionPath}");

        var state = controller.State;
        var trucks = new List<Truck>(state.Trucks);
        trucks.Sort((a, b) => a.Id.CompareTo(b.Id));

        var customerIds = new HashSet<int>();
        for (int i = 0; i < state.Customers.Count; i++)
            customerIds.Add(state.Customers[i].Id);

        bool needsShiftByOne = false;
        var allRouteIds = new List<int>();
        for (int i = 0; i < routes.Count; i++)
            allRouteIds.AddRange(routes[i]);

        if (allRouteIds.Count > 0)
        {
            bool directOk = true;
            for (int i = 0; i < allRouteIds.Count; i++)
            {
                if (!customerIds.Contains(allRouteIds[i]))
                {
                    directOk = false;
                    break;
                }
            }

            if (!directOk)
            {
                bool shiftedOk = true;
                for (int i = 0; i < allRouteIds.Count; i++)
                {
                    if (!customerIds.Contains(allRouteIds[i] + 1))
                    {
                        shiftedOk = false;
                        break;
                    }
                }

                if (!shiftedOk)
                    return false;

                needsShiftByOne = true;
            }
        }

        for (int i = 0; i < trucks.Count; i++)
        {
            var truck = trucks[i];
            truck.Plan.Clear();
            truck.CurrentTargetIndex = 0;
            truck.LockedPrefixCount = 0;
            truck.TargetPos = null;
            truck.TargetId = -1;
            truck.ActiveTarget = null;
            truck.ArrivedOnActiveTarget = false;
            truck.State = TruckState.Idle;
            truck.ServiceRemaining = 0f;
            truck.ServicingCustomerId = -1;
        }

        int assignCount = System.Math.Min(trucks.Count, routes.Count);
        for (int i = 0; i < assignCount; i++)
        {
            var truck = trucks[i];
            var route = routes[i];

            for (int j = 0; j < route.Count; j++)
            {
                int id = needsShiftByOne ? route[j] + 1 : route[j];
                if (!customerIds.Contains(id))
                    return false;

                truck.Plan.Add(TargetRef.Customer(id));
            }

            if (truck.Plan.Count > 0)
                truck.Plan.Add(TargetRef.Depot());

            UnityEngine.Debug.Log($"[BenchmarkRunner] Truck {truck.Id} seeded targets={truck.Plan.Count}");
        }

        if (assignCount == 0)
            UnityEngine.Debug.LogWarning("[BenchmarkRunner] No truck routes assigned (assignCount=0).");

        return true;
    }

    private static List<List<int>> ParseSolutionRoutes(string solutionPath)
    {
        var routes = new List<List<int>>();
        string[] lines = File.ReadAllLines(solutionPath);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("Route", StringComparison.OrdinalIgnoreCase))
                continue;

            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            string rhs = line.Substring(colon + 1).Trim();
            var route = new List<int>();
            if (!string.IsNullOrWhiteSpace(rhs))
            {
                string[] tokens = rhs.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                for (int t = 0; t < tokens.Length; t++)
                {
                    if (int.TryParse(tokens[t], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0)
                        route.Add(id);
                }
            }

            routes.Add(route);
        }

        return routes;
    }

    private bool IsRunComplete()
    {
        if (controller.State == null)
            return false;

        var state = controller.State;

        for (int i = 0; i < state.Customers.Count; i++)
        {
            if (state.Customers[i].Status != CustomerStatus.Served)
                return false;
        }

        for (int i = 0; i < state.Trucks.Count; i++)
        {
            var truck = state.Trucks[i];
            if (truck.State != TruckState.Idle)
                return false;

            if (Vec2.Distance(truck.Pos, state.Depot.Pos) > depotArrivalTolerance)
                return false;
        }

        return true;
    }

    private void FinalizeActiveRun()
    {
        if (!_runActive)
            return;

        controller.Pause();
        if (_wallClock != null)
            _wallClock.Stop();

        string outputRoot = ResolvePath(outputFolder);
        Directory.CreateDirectory(outputRoot);

        string fileName = string.Format(
            CultureInfo.InvariantCulture,
            "{0}_run{1:00}_seed{2}.sol",
            _activeWorkItem.BaseName,
            _activeWorkItem.RunIndex,
            _activeWorkItem.Seed);

        string outputPath = Path.Combine(outputRoot, fileName);

        float totalDistance = 0f;
        bool capacityOk = true;
        var trucks = new List<Truck>(controller.State.Trucks);
        trucks.Sort((a, b) => a.Id.CompareTo(b.Id));

        for (int i = 0; i < trucks.Count; i++)
        {
            totalDistance += trucks[i].TotalDistanceTraveled;
            if (trucks[i].Capacity > 0 && trucks[i].Load > trucks[i].Capacity)
                capacityOk = false;
        }

        bool feasible = IsRunComplete() && capacityOk;
        WriteSolutionFile(outputPath, trucks, totalDistance, controller.State.Time, _wallClock != null ? _wallClock.Elapsed.TotalSeconds : 0.0, feasible);

        UnityEngine.Debug.Log($"[BenchmarkRunner] Done {_activeWorkItem.BaseName} run={_activeWorkItem.RunIndex} seed={_activeWorkItem.Seed} -> {outputPath}");
        _runActive = false;
    }

    private static void WriteSolutionFile(string outputPath, List<Truck> trucks, float cost, float simTimeSeconds, double wallSeconds, bool feasible)
    {
        using var writer = new StreamWriter(outputPath, append: false);

        for (int i = 0; i < trucks.Count; i++)
        {
            var truck = trucks[i];
            var route = ExtractCustomerRoute(truck.Plan);
            if (route.Count == 0)
                continue;

            writer.Write("Route #");
            writer.Write(i + 1);
            writer.Write(":");

            for (int c = 0; c < route.Count; c++)
            {
                writer.Write(' ');
                writer.Write(route[c]);
            }

            writer.WriteLine();
        }

        writer.WriteLine($"Cost {cost.ToString("0.###", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"SimTimeSeconds {simTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"WallTimeSeconds {wallSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Feasible {(feasible ? 1 : 0)}");
    }

    private static List<int> ExtractCustomerRoute(List<TargetRef> plan)
    {
        var route = new List<int>();
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan[i].Type == TargetType.Customer)
                route.Add(plan[i].Id);
        }
        return route;
    }

    private static bool TryLoadScenario(string scenarioPath, out BenchmarkScenarioDto dto)
    {
        dto = null;

        if (!File.Exists(scenarioPath))
            return false;

        string json = File.ReadAllText(scenarioPath);
        dto = JsonUtility.FromJson<BenchmarkScenarioDto>(json);

        if (dto == null)
            return false;

        if (string.IsNullOrWhiteSpace(dto.base_instance))
            return false;

        return true;
    }

    private static string ResolveScenarioPath(string benchmarkRoot, string scenarioFile)
    {
        if (string.IsNullOrWhiteSpace(scenarioFile))
            return benchmarkRoot;

        if (Path.IsPathRooted(scenarioFile))
            return scenarioFile;

        return Path.Combine(benchmarkRoot, scenarioFile);
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(projectRoot))
            return path;

        return Path.Combine(projectRoot, path);
    }

    private static void DisableUiOverlays()
    {
        var ui = FindAnyObjectByType<SimUI>();
        if (ui != null) ui.enabled = false;

        var stats = FindAnyObjectByType<SimStatsPanel>();
        if (stats != null) stats.enabled = false;

        var feed = FindAnyObjectByType<SimEventFeed>();
        if (feed != null) feed.enabled = false;
    }

    [Serializable]
    private sealed class BenchmarkScenarioDto
    {
        public string base_instance;
        public int vehicle_count;
        public ScenarioCustomerDto[] new_customers;
    }

    [Serializable]
    private sealed class ScenarioCustomerDto
    {
        public int id;
        public double x;
        public double y;
        public int demand;
        public float reveal_time;
    }

    private struct RunWorkItem
    {
        public string ScenarioPath;
        public string BaseName;
        public int RunIndex;
        public int Seed;
    }
}
