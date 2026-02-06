using System.IO;
using UnityEngine;
using CoreSim;
using CoreSim.IO;
using CoreSim.Model;

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

        SimState state = InstanceMapper.FromDto(dto, cfg);

        // Optional: create a demo fleet for visuals (does NOT imply optimal vehicle count)
        float truckSpeed = cfg.OverrideTruckSpeed ?? dto.TruckSpeed;
        if (demoTruckCount > 0)
            InstanceMapper.CreateDemoFleet(state, demoTruckCount, truckSpeed);

        Debug.Log($"Loaded {dto.Name} TYPE={dto.Type} DIM={dto.Dimension} CAP={dto.Capacity}");
        Debug.Log($"Speeds: truck={truckSpeed} depot={(cfg.OverrideDepotSpeed ?? dto.DepotSpeed)}");
        Debug.Log($"Customers: {state.Customers.Count}, DepotStops: {state.Depot.CandidateStops.Count}, Trucks(demo): {state.Trucks.Count}");
    }
}