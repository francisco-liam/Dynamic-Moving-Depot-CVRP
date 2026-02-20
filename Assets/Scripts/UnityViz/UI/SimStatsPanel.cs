using TMPro;
using UnityEngine;
using CoreSim.Model;

public sealed class SimStatsPanel : MonoBehaviour
{
    [Header("References")]
    public SimViewController controller;

    [Header("UI")]
    public TMP_Text simTimeText;
    public TMP_Text speedText;
    public TMP_Text waitingText;
    public TMP_Text unreleasedText;
    public TMP_Text inServiceText;
    public TMP_Text servedText;
    public TMP_Text distanceText;
    public TMP_Text energyText;

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
    }

    private void Update()
    {
        if (controller == null || controller.State == null)
            return;

        var state = controller.State;

        int waiting = 0;
        int unreleased = 0;
        int inService = 0;
        int served = 0;

        for (int i = 0; i < state.Customers.Count; i++)
        {
            var c = state.Customers[i];
            switch (c.Status)
            {
                case CustomerStatus.Unreleased: unreleased++; break;
                case CustomerStatus.Waiting: waiting++; break;
                case CustomerStatus.InService: inService++; break;
                case CustomerStatus.Served: served++; break;
            }
        }

        float distance = 0f;
        float energy = 0f;
        for (int i = 0; i < state.Trucks.Count; i++)
        {
            distance += state.Trucks[i].TotalDistanceTraveled;
            energy += state.Trucks[i].TotalEnergyUsed;
        }

        if (simTimeText != null) simTimeText.text = $"Time: {state.Time:0.##}";
        if (speedText != null) speedText.text = $"Speed: {controller.speedMultiplier:0.##}x";
        if (waitingText != null) waitingText.text = $"Waiting: {waiting}";
        if (unreleasedText != null) unreleasedText.text = $"Unreleased: {unreleased}";
        if (inServiceText != null) inServiceText.text = $"InService: {inService}";
        if (servedText != null) servedText.text = $"Served: {served}";
        if (distanceText != null) distanceText.text = $"Distance: {distance:0.##}";
        if (energyText != null) energyText.text = $"Energy: {energy:0.##}";
    }
}
