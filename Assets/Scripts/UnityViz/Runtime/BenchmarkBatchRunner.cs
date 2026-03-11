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
    [Tooltip("Optional local-only config asset (Assets/LocalConfig/BenchmarkLocalConfig.asset). Gitignored — overrides selected fields without affecting committed scene values.")]
    public BenchmarkLocalConfig localConfig;

    [Header("Run Mode")]
    public bool runOnStart = false;
    [Tooltip("Leave empty to run ALL benchmark scenarios. Add entries (e.g. 'X-n143-k7') to run a specific subset.")]
    public string[] selectedScenarios = new string[0];

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

    [Header("Planner")]
    [Tooltip("Wall-clock seconds given to the HGS solver each replan. Each batch gap is ~52 sim-seconds; 5–10 s is reasonable.")]
    public float runSolverTimeBudgetSeconds = 5f;
    [Tooltip("Disable periodic replanning so the solver only fires on batch-release CustomerReleased events.")]
    public bool replanOnBatchReleaseOnly = true;
    [Tooltip("Log the full solver command, stdout, stderr and keep temp artifacts in %TEMP%/dynamic-cvrp-hgs/ for inspection.")]
    public bool debugSolverOutput = false;

    [Header("Visualization")]
    public bool forceShowRoutes = true;
    [Tooltip("Sim-time multiplier. Set to 1 to run at 1 sim-unit per real second.")]
    public float runSpeedMultiplier = 1f;
    [Tooltip("Speed multiplier applied after the last dynamic batch has been inserted, to finish the run faster. 0 = no change.")]
    public float postInsertSpeedMultiplier = 0f;

    [Header("Performance")]
    public bool disablePerEventLogs = true;
    public float runFixedStep = 0.05f;
    public int runMaxSimStepsPerFrame = 4;
    public bool disableUiOverlaysDuringBatch = true;

    [Header("Vehicle")]
    public float runTruckSpeedOverride = 1f;

    [Header("Completion")]
    public float depotArrivalTolerance = 0.5f;

    private readonly Queue<RunWorkItem> _pendingRuns = new Queue<RunWorkItem>();
    private readonly List<ScenarioCustomerDto> _pendingInserts = new List<ScenarioCustomerDto>();

    private RunWorkItem _activeWorkItem;
    private BenchmarkScenarioDto _activeScenario;
    private int _nextInsertIndex;
    private bool _runActive;
    private bool _dynamicReplanActivated;
    private bool _postInsertSpeedApplied;
    private Stopwatch _wallClock;
    private string _batchTimestamp;

    // ── Speed helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Right-click the component in the Inspector and choose "Reset Speed to 1:1"
    /// to snap all speed/step fields to 1 sim-unit = 1 real second.
    /// Do this once after upgrading from an old serialized scene.
    /// </summary>
    [ContextMenu("Reset Speed to 1:1")]
    public void ResetSpeedToOneToOne()
    {
        runSpeedMultiplier     = 1f;
        runTruckSpeedOverride  = 1f;
        runFixedStep           = 0.05f;
        runMaxSimStepsPerFrame = 4;
        UnityEngine.Debug.Log("[BenchmarkRunner] Speed reset to 1:1 (1 sim-unit = 1 real second).");

        // Apply immediately if a run is active.
        if (controller != null)
        {
            controller.SetSpeedMultiplier(runSpeedMultiplier);
            controller.fixedStep           = runFixedStep;
            controller.maxSimStepsPerFrame = runMaxSimStepsPerFrame;

            if (controller.State != null)
                for (int i = 0; i < controller.State.Trucks.Count; i++)
                    controller.State.Trucks[i].Speed = runTruckSpeedOverride;
        }
    }

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();

        // Tell the controller not to self-init in Start() — we own init.
        if (controller != null)
            controller.suppressAutoStart = true;

        // Warn about multiple controllers that could conflict
        var allControllers = FindObjectsByType<SimViewController>(FindObjectsSortMode.None);
        if (allControllers.Length > 1)
        {
            UnityEngine.Debug.LogWarning($"[BenchmarkRunner] Found {allControllers.Length} SimViewController instances - this could cause conflicts!");
            for (int i = 0; i < allControllers.Length; i++)
                UnityEngine.Debug.Log($"[BenchmarkRunner] SimViewController[{i}]: {allControllers[i].gameObject.name}, enabled={allControllers[i].enabled}");
        }

        // Warn about LoadInstanceDemo that could interfere
        var loadDemo = FindAnyObjectByType<LoadInstanceDemo>();
        if (loadDemo != null)
            UnityEngine.Debug.LogWarning($"[BenchmarkRunner] Found LoadInstanceDemo on {loadDemo.gameObject.name} - this may conflict!");
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

        // Once all batches are consumed, optionally fast-forward to the end.
        if (!_postInsertSpeedApplied
            && postInsertSpeedMultiplier > 0f
            && _pendingInserts.Count > 0
            && _nextInsertIndex >= _pendingInserts.Count)
        {
            _postInsertSpeedApplied = true;
            controller.SetSpeedMultiplier(postInsertSpeedMultiplier);
            UnityEngine.Debug.Log($"[BenchmarkRunner] All batches inserted — speed set to {postInsertSpeedMultiplier}× to finish run faster.");
        }

        // Keep the hint current for periodic/EarlyLock replans that fire outside ProcessDueInsertions.
        if (controller.ReplanController != null)
        {
            float nextSimTime = (_activeScenario != null && _nextInsertIndex < _pendingInserts.Count)
                ? _pendingInserts[_nextInsertIndex].reveal_time
                : float.PositiveInfinity;
            controller.ReplanController.NextScheduledInsertionSimTime = nextSimTime;
        }

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

        // localConfig override takes priority over the Inspector field.
        string[] effectiveScenarios = (localConfig != null
            && localConfig.selectedScenariosOverride != null
            && localConfig.selectedScenariosOverride.Length > 0)
            ? localConfig.selectedScenariosOverride
            : selectedScenarios;

        bool runAll = effectiveScenarios == null || effectiveScenarios.Length == 0;
        if (runAll)
        {
            // No filter — queue every valid scenario JSON in the folder.
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
            // Queue only the explicitly selected scenarios, in order.
            for (int i = 0; i < effectiveScenarios.Length; i++)
            {
                string entry = (effectiveScenarios[i] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(entry))
                    continue;

                // Accept bare name (X-n143-k7), with or without .json extension.
                if (!entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    entry += ".json";

                string scenarioPath = Path.IsPathRooted(entry)
                    ? entry
                    : Path.Combine(benchmarkRoot, entry);

                if (!File.Exists(scenarioPath))
                {
                    UnityEngine.Debug.LogError($"[BenchmarkRunner] Selected scenario not found: {scenarioPath}");
                    continue;
                }

                if (!TryLoadScenario(scenarioPath, out _))
                {
                    UnityEngine.Debug.LogError($"[BenchmarkRunner] Selected scenario has invalid schema: {scenarioPath}");
                    continue;
                }

                scenarioFiles.Add(scenarioPath);
            }
        }

        scenarioFiles.Sort(StringComparer.OrdinalIgnoreCase);

        _batchTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

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
        _postInsertSpeedApplied = false;

        // Restore normal run speed before starting the next scenario.
        float resetSpeed = (localConfig != null && localConfig.speedMultiplierOverride > 0f)
            ? localConfig.speedMultiplierOverride
            : runSpeedMultiplier;
        if (resetSpeed > 0f)
            controller.SetSpeedMultiplier(resetSpeed);

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

        // Disable auto-replan entirely until first batch insertion fires it.
        if (forceAutoReplan)
            controller.autoReplan = false;

        // Batch-release-only mode: no periodic ticks, no early-lock pre-emptive fire.
        if (replanOnBatchReleaseOnly)
        {
            controller.replanPeriodicInterval = 0f;
            controller.enableEarlyLockReplan = false;
        }

        // Wire solver time budget so the planner gets it on ResetSim.
        if (runSolverTimeBudgetSeconds > 0f)
            controller.solverTimeBudgetSeconds = runSolverTimeBudgetSeconds;

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

        // Propagate solver debug flags into the freshly-created HgsDynamicPlanner.
        if (controller.ReplanController?.Planner is CoreSim.Planning.HgsDynamicPlanner hgsPlanner)
        {
            hgsPlanner.LogSolverOutput = debugSolverOutput
                || (localConfig != null && (localConfig.logSolverOutput || localConfig.keepSolverArtifacts));
        }

        if (runTruckSpeedOverride > 0f && controller.State != null)
        {
            for (int i = 0; i < controller.State.Trucks.Count; i++)
                controller.State.Trucks[i].Speed = runTruckSpeedOverride;
        }

        if (disableUiOverlaysDuringBatch)
            DisableUiOverlays();

        float effectiveSpeed = (localConfig != null && localConfig.speedMultiplierOverride > 0f)
            ? localConfig.speedMultiplierOverride
            : runSpeedMultiplier;
        if (effectiveSpeed > 0f)
            controller.SetSpeedMultiplier(effectiveSpeed);

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

        // Effective sim rate: speedMultiplier × truckSpeed tells you how fast trucks
        // actually traverse the map per real second.
        float effectiveTruckRate = controller.speedMultiplier * runTruckSpeedOverride;
        UnityEngine.Debug.Log(
            $"[BenchmarkRunner] START {_activeWorkItem.BaseName} run={_activeWorkItem.RunIndex} seed={_activeWorkItem.Seed}\n" +
            $"  instance     = {instancePath}\n" +
            $"  customers    = {controller.State.Customers.Count}  trucks={controller.State.Trucks.Count}\n" +
            $"  speedMult    = {controller.speedMultiplier}  truckSpeed={runTruckSpeedOverride}  → {effectiveTruckRate:0.###} units/real-sec\n" +
            $"  fixedStep    = {controller.fixedStep}  maxStepsPerFrame={controller.maxSimStepsPerFrame}\n" +
            $"  solverBudget = {runSolverTimeBudgetSeconds}s  batchReleaseOnly={replanOnBatchReleaseOnly}\n" +
            "  NOTE: if units/real-sec != 1.0, right-click component → Reset Speed to 1:1");
        UnityEngine.Debug.Log("[BenchmarkRunner] AutoReplan is OFF until first dynamic insertion.");
    }

    private void ProcessDueInsertions()
    {
        if (_activeScenario == null || _pendingInserts.Count == 0)
            return;

        float now = controller.State.Time;

        bool anyInserted = false;
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
            anyInserted = true;
        }

        // Trigger a single replan for the whole batch after all insertions are done.
        // (Calling ReplanNow inside the loop would invoke the solver once per customer.)
        if (anyInserted && forceAutoReplan)
        {
            // Update the next-insertion hint BEFORE ReplanNow so ComputeDynamicBudget
            // sees the correct future batch time, not the one we just consumed.
            if (controller.ReplanController != null)
            {
                float nextSimTime = (_nextInsertIndex < _pendingInserts.Count)
                    ? _pendingInserts[_nextInsertIndex].reveal_time
                    : float.PositiveInfinity;
                controller.ReplanController.NextScheduledInsertionSimTime = nextSimTime;
            }

            if (!_dynamicReplanActivated)
            {
                controller.SetAutoReplan(true);
                _dynamicReplanActivated = true;
                UnityEngine.Debug.Log($"[BenchmarkRunner] Dynamic replanning activated at sim t={now:0.###}");
            }
            controller.ReplanNow();
            UnityEngine.Debug.Log($"[BenchmarkRunner] Replan triggered for batch of {_nextInsertIndex} inserts at sim t={now:0.###}");
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
            truck.Load = 0;
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
            
            // Enhanced debug: Log truck state after seeding
            UnityEngine.Debug.Log($"[BenchmarkRunner] Truck {truck.Id} state after seeding: Pos={truck.Pos}, Speed={truck.Speed}, CurrentTargetIndex={truck.CurrentTargetIndex}, HasPlanTarget={truck.HasPlanTarget}");
            if (truck.Plan.Count > 0)
            {
                string planStr = "";
                for (int j = 0; j < System.Math.Min(3, truck.Plan.Count); j++)
                {
                    var target = truck.Plan[j];
                    planStr += $"{target.Type}:{target.Id} ";
                }
                UnityEngine.Debug.Log($"[BenchmarkRunner] Truck {truck.Id} first targets: {planStr}");
            }
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
            "{0}_{1}_run{2:00}_seed{3}.sol",
            _activeWorkItem.BaseName,
            _batchTimestamp ?? DateTime.Now.ToString("yyyyMMdd_HHmmss"),
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
