using System.IO;
using UnityEngine;
using CoreSim;
using CoreSim.IO;
using CoreSim.Model;
using CoreSim.Sim;

public sealed class SimViewController : MonoBehaviour
{
    [Header("References")]
    public SimBootstrap bootstrap;
    public SimRenderer simRenderer;

    [Header("Instance")]
    public string instancePath = "";
    public string defaultInstanceFile = "X-n101-k25.dmdvrp";

    [Header("Run Settings")]
    public int seed = 12345;
    public bool autoPlay = true;
    public float speedMultiplier = 1f;
    public float fixedStep = 0.1f;

    [Header("Demo Fleet")]
    public int demoTruckCount = 3;
    public bool autoAssignDemoPlan = true;
    public int demoTargetsPerTruck = 3;

    public SimState State { get; private set; }
    public Simulation Simulation { get; private set; }
    public bool IsPlaying => _isPlaying;

    private bool _isPlaying;
    private float _accumulator;

    private void Awake()
    {
        if (bootstrap == null)
            bootstrap = FindAnyObjectByType<SimBootstrap>();
        if (simRenderer == null)
            simRenderer = FindAnyObjectByType<SimRenderer>();
    }

    private void Start()
    {
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

            while (_accumulator >= fixedStep)
            {
                Simulation.Step(fixedStep);
                _accumulator -= fixedStep;
            }
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
        if (simRenderer != null && State != null)
            simRenderer.Render(State);
    }

    public void SetSpeedMultiplier(float value)
    {
        speedMultiplier = Mathf.Max(0f, value);
    }

    public void SetInstancePath(string path)
    {
        instancePath = path;
    }

    public void ResetSim(int newSeed, string path)
    {
        seed = newSeed;
        string resolvedPath = ResolveInstancePath(path);

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
        _accumulator = 0f;

        if (simRenderer != null)
            simRenderer.SetState(State);
    }

    private string ResolveInstancePath(string path)
    {
        if (!string.IsNullOrEmpty(path))
            return path;

        string folder = Path.Combine(Application.streamingAssetsPath, "Instances");
        return Path.Combine(folder, defaultInstanceFile);
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
}
