using UnityEngine;
using CoreSim;
using CoreSim.Utils;

public sealed class SimBootstrap : MonoBehaviour
{
    [Header("Run Settings")]
    public int seed = 12345;

    [Tooltip("If true, uses current time as seed (not deterministic).")]
    public bool useRandomSeed = false;

    [Header("Logging")]
    public bool enableLogging = true;
    public LogLevel minLogLevel = LogLevel.Info;

    [Header("Sim")]
    public float simTimeScale = 1f;

    private SimRunContext _run;
    private float _simTime;

    private void Start()
    {
        int finalSeed = seed;

        if (useRandomSeed)
        {
            finalSeed = System.Environment.TickCount;
        }

        var logger = new SimLogger
        {
            Enabled = enableLogging,
            MinLevel = minLogLevel
        };

        _run = new SimRunContext(finalSeed, logger);

        _simTime = 0f;

        // Print initial run info
        _run.Logger.Info($"Simulation started. Seed={_run.Seed}");
        Debug.Log($"[Unity] Simulation started. Seed={_run.Seed}");
    }

    private void Update()
    {
        float dt = Time.deltaTime * simTimeScale;
        _simTime += dt;

        // For Phase 0: print time every ~1 second
        if (Mathf.FloorToInt(_simTime) != Mathf.FloorToInt(_simTime - dt))
        {
            string msg = $"SimTime={_simTime:F2}s";
            _run.Logger.Info(msg);
            Debug.Log($"[Unity] {msg}");
        }
    }
}