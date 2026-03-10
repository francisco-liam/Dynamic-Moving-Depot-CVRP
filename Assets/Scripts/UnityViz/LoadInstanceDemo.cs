using System.IO;
using UnityEngine;
using CoreSim;
using CoreSim.IO;
using CoreSim.Model;
using CoreSim.Sim;

public sealed class LoadInstanceDemo : MonoBehaviour
{
    public string fileName = "X-n101-k25.dmdvrp";

    [Header("Demo Fleet (optional)")]
    public int demoTruckCount = 5;

    private void Start()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Instances", fileName);
        Debug.Log($"Loading instance: {path}");

        var dto = InstanceParser.ParseFromFile(path);

        var cfg = new SimConfig
        {
            Seed = 12345,
            TimeScale = 1f
        };

        var bootstrap = FindAnyObjectByType<SimBootstrap>();
        if (bootstrap != null)
            bootstrap.SimReset(cfg.Seed, path);

        SimState state = InstanceMapper.FromDto(dto, cfg);

        // Optional: create a demo fleet for visuals (does NOT imply optimal vehicle count)
        float truckSpeed = cfg.OverrideTruckSpeed ?? dto.TruckSpeed;
        if (demoTruckCount > 0)
            InstanceMapper.CreateDemoFleet(state, demoTruckCount, truckSpeed);

        if (state.Trucks.Count > 0)
            AssignDemoPlan(state);

        var sim = new Simulation(state);
        for (int i = 0; i < 10; i++)
            sim.Step(1f);

        string snapshotPath = Path.Combine(Application.persistentDataPath, "snapshot.txt");
        var snapshot = SnapshotSerializer.CreateFromState(state, sim.Queue, cfg.Seed);
        SnapshotSerializer.WriteToFile(snapshotPath, snapshot);
        var loaded = SnapshotSerializer.ReadFromFile(snapshotPath);

        Debug.Log($"Loaded {dto.Name} TYPE={dto.Type} DIM={dto.Dimension} CAP={dto.Capacity}");
        Debug.Log($"Speeds: truck={truckSpeed} depot={(cfg.OverrideDepotSpeed ?? dto.DepotSpeed)}");
        Debug.Log($"Customers: {state.Customers.Count}, DepotStops: {state.Depot.CandidateStops.Count}, Trucks(demo): {state.Trucks.Count}");
        Debug.Log($"Snapshot written: {snapshotPath}");
        Debug.Log($"Snapshot loaded: customers={loaded.Customers.Count} trucks={loaded.Trucks.Count} events={loaded.Events.Count}");
        for (int i = 0; i < Mathf.Min(loaded.Events.Count, 5); i++)
            Debug.Log($"Event[{i}] {loaded.Events[i]}");
    }

    private static void AssignDemoPlan(SimState state)
    {
        var truck = state.Trucks[0];
        truck.Plan.Clear();
        truck.CurrentTargetIndex = 0;
        truck.LockedPrefixCount = 0;
        truck.TargetPos = null;
        truck.TargetId = -1;
        truck.State = TruckState.Idle;

        int count = Mathf.Min(3, state.Customers.Count);
        for (int i = 0; i < count; i++)
        {
            var c = state.Customers[i];
            if (c.ServiceTime <= 0f)
                c.ServiceTime = 1f;

            truck.Plan.Add(TargetRef.Customer(c.Id));
        }
    }
}