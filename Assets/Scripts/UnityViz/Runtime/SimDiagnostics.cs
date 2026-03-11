// Attach this MonoBehaviour to any GameObject in the scene.
// It auto-runs at start (after 3 frames) and dumps a full diagnostic block.
// You can also right-click the component → "Run Diagnostics" at any time.
// Copy the entire [SIMDIAG] block from the Console and paste it to Copilot.

using System.Text;
using UnityEngine;
using CoreSim.Model;

public sealed class SimDiagnostics : MonoBehaviour
{
    [Header("References (auto-found if blank)")]
    public SimViewController controller;
    public SimRenderer simRenderer;

    [Header("Options")]
    public bool runOnStart = true;
    [Tooltip("How many frames to wait before the auto-run fires, giving other Start()s time to execute.")]
    public int autoRunDelay = 5;

    private int _frameCount;

    private void Awake()
    {
        if (controller == null)  controller  = FindAnyObjectByType<SimViewController>();
        if (simRenderer == null) simRenderer = FindAnyObjectByType<SimRenderer>();
    }

    private void Update()
    {
        if (!runOnStart) return;
        _frameCount++;
        if (_frameCount == autoRunDelay)
            RunDiagnostics();
    }

    [ContextMenu("Run Diagnostics")]
    public void RunDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== [SIMDIAG START] =====");

        // ── 1. Controller & simulation state ────────────────────────────────
        sb.AppendLine("-- Controller --");
        if (controller == null)
        {
            sb.AppendLine("  SimViewController: NULL (not found in scene!)");
        }
        else
        {
            sb.AppendLine($"  GO name         : {controller.gameObject.name}");
            sb.AppendLine($"  enabled         : {controller.enabled}");
            sb.AppendLine($"  IsPlaying       : {controller.IsPlaying}");
            sb.AppendLine($"  suppressAutoStart: {controller.suppressAutoStart}");
            sb.AppendLine($"  autoPlay        : {controller.autoPlay}");
            sb.AppendLine($"  runStartupReplan: {controller.runStartupReplan}");
            sb.AppendLine($"  useHgsDynamic   : {controller.useHgsDynamicPlanner}");
            sb.AppendLine($"  solverPath      : {controller.solverExecutablePath}");
            sb.AppendLine($"  solverBudget    : {controller.solverTimeBudgetSeconds}s");
            sb.AppendLine($"  periodicInterval: {controller.replanPeriodicInterval} (0=off)");
            sb.AppendLine($"  earlyLockReplan : {controller.enableEarlyLockReplan}");
            sb.AppendLine($"  speedMultiplier : {controller.speedMultiplier}");
            sb.AppendLine($"  fixedStep       : {controller.fixedStep}");
            sb.AppendLine($"  maxStepsPerFrame: {controller.maxSimStepsPerFrame}");
            sb.AppendLine($"  demoTruckCount  : {controller.demoTruckCount}");
            sb.AppendLine($"  autoAssignPlan  : {controller.autoAssignDemoPlan}");
            sb.AppendLine($"  instancePath    : {controller.instancePath}");
        }

        // ── 2. Simulation state ──────────────────────────────────────────────
        sb.AppendLine("-- SimState --");
        var state = controller?.State;
        if (state == null)
        {
            sb.AppendLine("  State: NULL");
        }
        else
        {
            sb.AppendLine($"  SimTime         : {state.Time:0.###}");
            sb.AppendLine($"  Customers       : {state.Customers.Count}");
            sb.AppendLine($"  Trucks          : {state.Trucks.Count}");
            sb.AppendLine($"  Depot pos       : {state.Depot.Pos}");
            sb.AppendLine($"  Features        : {state.Features}");

            int waiting = 0, unreleased = 0, inService = 0, served = 0;
            for (int i = 0; i < state.Customers.Count; i++)
            {
                switch (state.Customers[i].Status)
                {
                    case CustomerStatus.Waiting:    waiting++;    break;
                    case CustomerStatus.Unreleased: unreleased++; break;
                    case CustomerStatus.InService:  inService++;  break;
                    case CustomerStatus.Served:     served++;     break;
                }
            }
            sb.AppendLine($"  Cust status     : waiting={waiting} unreleased={unreleased} inService={inService} served={served}");
        }

        // ── 3. Per-truck report ──────────────────────────────────────────────
        sb.AppendLine("-- Trucks --");
        if (state == null || state.Trucks.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            for (int i = 0; i < state.Trucks.Count; i++)
            {
                var t = state.Trucks[i];
                string firstTarget = "n/a";
                if (t.HasPlanTarget)
                {
                    var tref = t.CurrentTarget.Value;
                    firstTarget = $"{tref.Type}:{tref.Id}";
                }
                sb.AppendLine($"  T{t.Id,-3} state={t.State,-12} pos=({t.Pos.X:0.#},{t.Pos.Y:0.#})" +
                              $"  speed={t.Speed:0.###}  planSize={t.Plan.Count}" +
                              $"  curIdx={t.CurrentTargetIndex}  hasPlan={t.HasPlanTarget}" +
                              $"  targetPos={(t.TargetPos.HasValue ? $"({t.TargetPos.Value.X:0.#},{t.TargetPos.Value.Y:0.#})" : "null")}" +
                              $"  firstTarget={firstTarget}  load={t.Load}/{t.Capacity}");
            }
        }

        // ── 4. SimRenderer prefab checks ─────────────────────────────────────
        sb.AppendLine("-- SimRenderer --");
        if (simRenderer == null)
        {
            sb.AppendLine("  SimRenderer: NULL (not found in scene!)");
        }
        else
        {
            sb.AppendLine($"  GO name            : {simRenderer.gameObject.name}");
            sb.AppendLine($"  enabled            : {simRenderer.enabled}");
            sb.AppendLine($"  showRoutes         : {simRenderer.showRoutes}");
            sb.AppendLine($"  truckPrefab        : {(simRenderer.truckPrefab    != null ? simRenderer.truckPrefab.name    : "NULL ← trucks won't render")}");
            sb.AppendLine($"  customerPrefab     : {(simRenderer.customerPrefab != null ? simRenderer.customerPrefab.name : "NULL ← customers won't render")}");
            sb.AppendLine($"  depotPrefab        : {(simRenderer.depotPrefab    != null ? simRenderer.depotPrefab.name    : "NULL ← depot won't render")}");
            sb.AppendLine($"  routeLinePrefab    : {(simRenderer.routeLinePrefab        != null ? simRenderer.routeLinePrefab.name        : "NULL ← route LINES won't draw")}");
            sb.AppendLine($"  lockedRouteLinePrefab: {(simRenderer.lockedRouteLinePrefab != null ? simRenderer.lockedRouteLinePrefab.name : "null (ok – only locked lines affected)")}");
        }

        // ── 5. BenchmarkBatchRunner ──────────────────────────────────────────
        sb.AppendLine("-- BenchmarkBatchRunner --");
        var runner = FindAnyObjectByType<BenchmarkBatchRunner>();
        if (runner == null)
        {
            sb.AppendLine("  (not in scene)");
        }
        else
        {
            sb.AppendLine($"  GO name          : {runner.gameObject.name}");
            sb.AppendLine($"  enabled          : {runner.enabled}");
            sb.AppendLine($"  runOnStart       : {runner.runOnStart}");
            sb.AppendLine($"  selectedScenarios: {(runner.selectedScenarios == null || runner.selectedScenarios.Length == 0 ? "(all)" : string.Join(", ", runner.selectedScenarios))}");
            sb.AppendLine($"  forceHgsPlanner  : {runner.forceHgsPlanner}");
            sb.AppendLine($"  forceAutoReplan  : {runner.forceAutoReplan}");
            sb.AppendLine($"  replanBatchOnly  : {runner.replanOnBatchReleaseOnly}");
            sb.AppendLine($"  solverBudget     : {runner.runSolverTimeBudgetSeconds}s");
            sb.AppendLine($"  runTruckSpeed    : {runner.runTruckSpeedOverride}");
            sb.AppendLine($"  runSpeedMult     : {runner.runSpeedMultiplier}");
            sb.AppendLine($"  benchmarksFolder : {runner.benchmarksFolder}");
            sb.AppendLine($"  instancesFolder  : {runner.instancesFolder}");
            sb.AppendLine($"  solutionsFolder  : {runner.solutionsFolder}");
        }

        // ── 6. LoadInstanceDemo conflict check ───────────────────────────────
        sb.AppendLine("-- LoadInstanceDemo --");
        var demo = FindAnyObjectByType<LoadInstanceDemo>();
        if (demo == null)
            sb.AppendLine("  (not found – good)");
        else
            sb.AppendLine($"  FOUND on '{demo.gameObject.name}' enabled={demo.enabled}  ← DISABLE THIS");

        // ── 7. Multiple SimViewControllers ───────────────────────────────────
        sb.AppendLine("-- All SimViewControllers --");
        var allVC = FindObjectsByType<SimViewController>(FindObjectsSortMode.None);
        sb.AppendLine($"  Count: {allVC.Length}{(allVC.Length > 1 ? " ← MULTIPLE – remove extras" : "")}");
        for (int i = 0; i < allVC.Length; i++)
            sb.AppendLine($"  [{i}] {allVC[i].gameObject.name}  enabled={allVC[i].enabled}");

        // ── 8. All SimBootstraps ─────────────────────────────────────────────
        sb.AppendLine("-- All SimBootstraps --");
        var allBS = FindObjectsByType<SimBootstrap>(FindObjectsSortMode.None);
        sb.AppendLine($"  Count: {allBS.Length}{(allBS.Length > 1 ? " ← MULTIPLE" : "")}");
        for (int i = 0; i < allBS.Length; i++)
            sb.AppendLine($"  [{i}] {allBS[i].gameObject.name}  enabled={allBS[i].enabled}");

        // ── 9. Folder existence checks ───────────────────────────────────────
        sb.AppendLine("-- Folder/File Checks --");
        string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? "?";
        sb.AppendLine($"  dataPath         : {Application.dataPath}");
        sb.AppendLine($"  projectRoot      : {projectRoot}");

        string[] foldersToCheck = {
            "Assets/HGS-Dynamic-CVRP/Generated Benchmarks",
            "Assets/HGS-Dynamic-CVRP/Instances/CVRP",
            "Assets/HGS-Dynamic-CVRP/Solutions",
            "Assets/HGS-Dynamic-CVRP/Run Results"
        };
        foreach (var rel in foldersToCheck)
        {
            string abs = System.IO.Path.Combine(projectRoot, rel);
            bool exists = System.IO.Directory.Exists(abs);
            int fileCount = exists ? System.IO.Directory.GetFiles(abs).Length : 0;
            sb.AppendLine($"  {rel}: {(exists ? $"OK ({fileCount} files)" : "MISSING")}");
        }

        // Solver exe
        string[] exePaths = {
            System.IO.Path.Combine(projectRoot, "Assets/HGS-Dynamic-CVRP/hgs_dynamic.exe"),
            System.IO.Path.Combine(projectRoot, "Assets/HGS-Dynamic-CVRP/build/hgs_dynamic.exe"),
            System.IO.Path.Combine(projectRoot, "Assets/HGS-Dynamic-CVRP/build-win/hgs_dynamic.exe"),
        };
        sb.AppendLine("  Solver exe search:");
        foreach (var p in exePaths)
            sb.AppendLine($"    {p}: {(System.IO.File.Exists(p) ? "FOUND" : "missing")}");

        sb.AppendLine("===== [SIMDIAG END] =====");
        Debug.Log(sb.ToString());
    }
}
