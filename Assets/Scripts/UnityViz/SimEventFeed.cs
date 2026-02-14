using System.Text;
using TMPro;
using UnityEngine;
using CoreSim.Events;

public sealed class SimEventFeed : MonoBehaviour
{
    [Header("References")]
    public SimViewController controller;

    [Header("UI")]
    public TMP_Text feedText;
    public int maxLines = 50;
    public float updateInterval = 0.2f;

    private float _nextUpdateTime;
    private int _lastEventCount;

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
    }

    private void Update()
    {
        if (controller == null || controller.Simulation == null || feedText == null)
            return;

        if (Time.time < _nextUpdateTime)
            return;

        _nextUpdateTime = Time.time + updateInterval;

        var events = controller.Simulation.RecentEvents;
        if (events.Count == _lastEventCount)
            return;

        int start = Mathf.Max(0, events.Count - maxLines);
        var sb = new StringBuilder();

        for (int i = start; i < events.Count; i++)
            sb.AppendLine(FormatEvent(events[i]));

        feedText.text = sb.ToString();
        _lastEventCount = events.Count;
    }

    private static string FormatEvent(SimEvent e)
    {
        switch (e.Type)
        {
            case SimEventType.CustomerInserted:
                return $"[{e.Time:0.##}] CustomerInserted id={e.A} demand={e.B}";
            case SimEventType.CustomerReleased:
                return $"[{e.Time:0.##}] CustomerReleased id={e.A}";
            case SimEventType.TruckArrived:
                return $"[{e.Time:0.##}] TruckArrived truck={e.A} target={e.B}";
            case SimEventType.CustomerServed:
                return $"[{e.Time:0.##}] CustomerServed truck={e.A} customer={e.B}";
            case SimEventType.DepotArrived:
                return $"[{e.Time:0.##}] DepotArrived stop={e.A}";
            case SimEventType.TruckEnergyChanged:
                return $"[{e.Time:0.##}] TruckEnergyChanged truck={e.A} battery={e.B}";
            default:
                return $"[{e.Time:0.##}] {e.Type} (A={e.A}, B={e.B})";
        }
    }
}
