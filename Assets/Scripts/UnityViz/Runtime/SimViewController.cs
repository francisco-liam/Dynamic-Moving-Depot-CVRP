using System.IO;
using System.Collections.Generic;
using UnityEngine;
using CoreSim;
using CoreSim.IO;
using CoreSim.Model;
using CoreSim.Planning;
using CoreSim.Sim;

public sealed class SimViewController : MonoBehaviour
{
    [Header("References")]
    public SimBootstrap bootstrap;
    public SimRenderer simRenderer;
    public SimCameraController cameraController;

    [Header("Instance")]
    public string instancePath = "";
    public string defaultInstanceFile = "X-n101-k25.dmdvrp";

    [Header("Run Settings")]
    public int seed = 12345;
    public bool autoPlay = true;
    public bool runStartupReplan = true;
    public float speedMultiplier = 1f;
    public float fixedStep = 0.1f;
    public int maxSimStepsPerFrame = 50;
    public bool logEvents = true;

    [Header("Demo Fleet")]
    public int demoTruckCount = 3;
    public bool autoAssignDemoPlan = false;   // legacy: leave false — routes are owned by the planner
    public int demoTargetsPerTruck = 3;

    [Header("Auto Replan")]
    public bool autoReplan = true;
    public float replanPeriodicInterval = 2f;
    public float replanMinGap = 1f;
    public int commitmentLockK = 1;
    public bool replanRespectCapacity = true;
    public bool replanRespectReleaseTime = true;

    [Header("Planner")]
    public bool useHgsDynamicPlanner = false;
    public string solverExecutablePath = "hgs_dynamic";
    public float solverTimeBudgetSeconds = 1.0f;
    public float processOverheadBufferSeconds = 0.25f;
    public float safetyMarginSeconds = 0.25f;
    public bool enableEarlyLockReplan = true;

    [Header("Batch Control")]
    [Tooltip("Set true by BenchmarkBatchRunner so SimViewController.Start() does not run its own ResetSim.")]
    public bool suppressAutoStart = false;

    public SimState State { get; private set; }
    public Simulation Simulation { get; private set; }
    public ReplanController ReplanController { get; private set; }
    public bool IsPlaying => _isPlaying;

    private bool _isPlaying;
    private float _accumulator;
    private int _lastEventCount;
    private string _resolvedInstancePath = string.Empty;
    private string _resolvedSolverExecutablePath = string.Empty;
    private string _lastLoggedReplanSummary = string.Empty;

    private void Awake()
    {
        if (bootstrap == null)
            bootstrap = FindAnyObjectByType<SimBootstrap>();
        if (simRenderer == null)
            simRenderer = FindAnyObjectByType<SimRenderer>();
        if (cameraController == null)
            cameraController = FindAnyObjectByType<SimCameraController>();
    }

    private void Start()
    {
        if (suppressAutoStart)
        {
            Debug.Log("[SimViewController] suppressAutoStart=true, skipping auto ResetSim. BenchmarkBatchRunner owns init.");
            return;
        }

        ResetSim(seed, instancePath);
        if (autoPlay)
            Play();
    }

    private void Update()
    {
        if (Simulation == null || State == null)
            return;

        if (_isPlaying)
        {
            float dt = Time.deltaTime * Mathf.Max(0f, speedMultiplier);
            _accumulator += dt;

            int steps = 0;
            int stepCap = Mathf.Max(1, maxSimStepsPerFrame);
            while (_accumulator >= fixedStep && steps < stepCap)
            {
                Simulation.Step(fixedStep);
                ReplanController?.Step(Simulation);
                _accumulator -= fixedStep;
                steps += 1;
            }

            if (steps >= stepCap)
                _accumulator = 0f;

            // Echo replan events (start / applied) to the Unity console.
            // ReplanController uses Console.WriteLine internally (no Unity dependency),
            // so we track LastSummary here and forward any change to Debug.Log.
            if (ReplanController != null)
            {
                string summary = ReplanController.LastSummary;
                if (!string.IsNullOrEmpty(summary) && summary != _lastLoggedReplanSummary)
                {
                    _lastLoggedReplanSummary = summary;
                    Debug.Log("[Replan] " + summary);
                }
            }

            LogNewEvents();
        }

        if (simRenderer != null)
            simRenderer.Render(State);
    }

    public void Play() => _isPlaying = true;

    public void Pause() => _isPlaying = false;

    public void TogglePlayPause() => _isPlaying = !_isPlaying;

    public void StepOnce()
    {
        if (Simulation == null) return;
        Simulation.Step(fixedStep);
        ReplanController?.Step(Simulation);
        LogNewEvents();
        if (simRenderer != null && State != null)
            simRenderer.Render(State);
    }

    public void SetAutoReplan(bool enabled)
    {
        autoReplan = enabled;
        if (ReplanController != null)
            ReplanController.AutoReplanEnabled = enabled;
    }

    public void ReplanNow()
    {
        if (Simulation == null)
            return;

        if (ReplanController == null)
            EnsureReplanController();

        if (ReplanController != null)
            ReplanController.ReplanNow(Simulation);
    }

    public void SetSpeedMultiplier(float value)
    {
        speedMultiplier = Mathf.Max(0f, value);
        if (ReplanController != null)
            ReplanController.SimTimeScale = speedMultiplier;
    }

    public void SetInstancePath(string path)
    {
        instancePath = path;
    }

    public Customer InsertCustomer(CustomerSpec spec)
    {
        if (Simulation == null)
        {
            Debug.LogWarning("Simulation not ready; cannot insert customer.");
            return null;
        }

        return Simulation.InsertCustomer(spec);
    }

    public void ResetSim(int newSeed, string path)
    {
        seed = newSeed;
        string resolvedPath = ResolveInstancePath(path);
        _resolvedInstancePath = resolvedPath;
        _resolvedSolverExecutablePath = ResolveSolverExecutablePath(solverExecutablePath);

        if (bootstrap != null)
            bootstrap.SimReset(seed, resolvedPath);

        var dto = InstanceParser.ParseFromFile(resolvedPath);
        var cfg = new SimConfig
        {
            Seed = seed,
            TimeScale = 1f
        };

        State = InstanceMapper.FromDto(dto, cfg);

        float truckSpeed = cfg.OverrideTruckSpeed ?? dto.TruckSpeed;
        if (demoTruckCount > 0)
            InstanceMapper.CreateDemoFleet(State, demoTruckCount, truckSpeed);

        if (autoAssignDemoPlan)
            AssignDemoPlans(State, demoTargetsPerTruck);

        Simulation = new Simulation(State);
        // Normalize t=0 semantics once and emit one-time initial release events.
        Simulation.InitializeAtCurrentTime(emitInitialReleaseEvents: true);

        EnsureReplanController();
        ReplanController?.Reset(Simulation);
        if (runStartupReplan)
            ReplanController?.ReplanNow(Simulation);
        _accumulator = 0f;
        _lastEventCount = 0;

        if (simRenderer != null)
            simRenderer.SetState(State);

        if (cameraController != null && cameraController.autoFrameOnReset)
            cameraController.FrameState(State);
    }

    private string ResolveInstancePath(string path)
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Instances");

        if (string.IsNullOrEmpty(path))
            return Path.Combine(folder, defaultInstanceFile);

        if (Path.IsPathRooted(path))
            return path;

        string candidate = Path.Combine(folder, path);
        if (File.Exists(candidate))
            return candidate;

        return path;
    }

    private string ResolveSolverExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var nameVariants = BuildExecutableNameVariants(path);

        for (int i = 0; i < nameVariants.Count; i++)
        {
            string variant = nameVariants[i];

            if (Path.IsPathRooted(variant))
            {
                if (File.Exists(variant))
                    return variant;

                continue;
            }

            string cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), variant);
            if (File.Exists(cwdCandidate))
                return cwdCandidate;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                string rootCandidate = Path.Combine(projectRoot, variant);
                if (File.Exists(rootCandidate))
                    return rootCandidate;

                // Search order: the HGS root folder (ships a Windows-native exe),
                // then build-win (MSVC/NMake release), then build (may be a non-Windows binary).
                string[] commonBuildRoots =
                {
                    Path.Combine(projectRoot, "Assets", "HGS-Dynamic-CVRP"),
                    Path.Combine(projectRoot, "Assets", "HGS-Dynamic-CVRP", "build-win"),
                    Path.Combine(projectRoot, "Assets", "HGS-Dynamic-CVRP", "build")
                };

                for (int b = 0; b < commonBuildRoots.Length; b++)
                {
                    string candidate = Path.Combine(commonBuildRoots[b], variant);
                    if (File.Exists(candidate) && IsWindowsExecutable(candidate))
                        return candidate;
                }
            }
        }

        return path;
    }

    private static List<string> BuildExecutableNameVariants(string path)
    {
        var values = new List<string>(2) { path };

        if (Path.GetExtension(path).Length == 0)
            values.Add(path + ".exe");

        return values;
    }

    /// <summary>
    /// Checks that the file starts with the Windows PE magic bytes (MZ header),
    /// so we don't accidentally resolve a Linux ELF binary as the solver.
    /// </summary>
    private static bool IsWindowsExecutable(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var header = new byte[2];
            return fs.Read(header, 0, 2) == 2 && header[0] == 'M' && header[1] == 'Z';
        }
        catch { return false; }
    }

    private static void AssignDemoPlans(SimState state, int targetsPerTruck)
    {
        if (state.Trucks.Count == 0 || state.Customers.Count == 0)
            return;

        int custIndex = 0;
        for (int t = 0; t < state.Trucks.Count; t++)
        {
            var truck = state.Trucks[t];
            truck.Plan.Clear();
            truck.CurrentTargetIndex = 0;
            truck.LockedPrefixCount = 0;
            truck.TargetPos = null;
            truck.TargetId = -1;
            truck.ActiveTarget = null;
            truck.ArrivedOnActiveTarget = false;
            truck.State = TruckState.Idle;

            int added = 0;
            while (added < targetsPerTruck && custIndex < state.Customers.Count)
            {
                var c = state.Customers[custIndex];
                if (c.ServiceTime <= 0f)
                    c.ServiceTime = 1f;

                truck.Plan.Add(TargetRef.Customer(c.Id));
                added += 1;
                custIndex += 1;
            }
        }
    }

    private void LogNewEvents()
    {
        if (!logEvents || Simulation == null) return;

        var events = Simulation.RecentEvents;
        if (events.Count < _lastEventCount)
            _lastEventCount = 0;

        for (int i = _lastEventCount; i < events.Count; i++)
            Debug.Log($"[SimEvent] {events[i]}");

        _lastEventCount = events.Count;
    }

    private void EnsureReplanController()
    {
        IPlanner planner;
        if (useHgsDynamicPlanner)
        {
            string artifactsDir = Path.Combine(Application.dataPath, "LocalConfig", "solver-runs");
            System.IO.Directory.CreateDirectory(artifactsDir);
            planner = new HgsDynamicPlanner
            {
                SolverExecutablePath = _resolvedSolverExecutablePath,
                InstancePath = _resolvedInstancePath,
                SolverTimeBudgetSeconds = Mathf.Max(0f, solverTimeBudgetSeconds),
                ProcessOverheadBufferSeconds = Mathf.Max(0f, processOverheadBufferSeconds),
                SafetyMarginSeconds = Mathf.Max(0f, safetyMarginSeconds),
                ArtifactsDir = artifactsDir,
            };

            Debug.Log($"[HGS] solver path resolved: {_resolvedSolverExecutablePath}");
            Debug.Log($"[HGS] artifacts dir: {artifactsDir}");
        }
        else
        {
            planner = new BaselinePlanner();
        }

        ReplanController = new ReplanController(planner);

        ReplanController.AutoReplanEnabled = autoReplan;
        ReplanController.PeriodicInterval = Mathf.Max(0f, replanPeriodicInterval);
        ReplanController.MinTimeBetweenReplans = Mathf.Max(0f, replanMinGap);
        ReplanController.CommitmentLockK = Mathf.Max(0, commitmentLockK);
        ReplanController.RespectCapacity = replanRespectCapacity;
        ReplanController.RespectReleaseTime = replanRespectReleaseTime;
        ReplanController.EnableEarlyLockReplan = enableEarlyLockReplan;
        ReplanController.SolverTimeBudgetSeconds = Mathf.Max(0f, solverTimeBudgetSeconds);
        ReplanController.ProcessOverheadBufferSeconds = Mathf.Max(0f, processOverheadBufferSeconds);
        ReplanController.SafetyMarginSeconds = Mathf.Max(0f, safetyMarginSeconds);
        ReplanController.SimTimeScale = Mathf.Max(0.001f, speedMultiplier);
    }
}
