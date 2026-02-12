using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class SimUI : MonoBehaviour
{
    [Header("Controller")]
    public SimViewController controller;

    [Header("UI References")]
    public TMP_InputField seedInput;
    public TMP_InputField instancePathInput;
    public TMP_Text playPauseLabel;
    public TMP_Dropdown speedDropdown;
    public Button playPauseButton;
    public Button stepButton;
    public Button resetButton;
    public Toggle showRoutesToggle;

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
    }

    private void OnEnable()
    {
        if (playPauseButton != null) playPauseButton.onClick.AddListener(OnPlayPauseClicked);
        if (stepButton != null) stepButton.onClick.AddListener(OnStepClicked);
        if (resetButton != null) resetButton.onClick.AddListener(OnResetClicked);
        if (speedDropdown != null) speedDropdown.onValueChanged.AddListener(OnSpeedChanged);
        if (showRoutesToggle != null) showRoutesToggle.onValueChanged.AddListener(OnShowRoutesChanged);

        RefreshLabels();
    }

    private void OnDisable()
    {
        if (playPauseButton != null) playPauseButton.onClick.RemoveListener(OnPlayPauseClicked);
        if (stepButton != null) stepButton.onClick.RemoveListener(OnStepClicked);
        if (resetButton != null) resetButton.onClick.RemoveListener(OnResetClicked);
        if (speedDropdown != null) speedDropdown.onValueChanged.RemoveListener(OnSpeedChanged);
        if (showRoutesToggle != null) showRoutesToggle.onValueChanged.RemoveListener(OnShowRoutesChanged);
    }

    private void OnPlayPauseClicked()
    {
        if (controller == null) return;
        controller.TogglePlayPause();
        RefreshLabels();
    }

    private void OnStepClicked()
    {
        controller?.StepOnce();
    }

    private void OnResetClicked()
    {
        if (controller == null) return;

        int seed = controller.seed;
        if (seedInput != null && int.TryParse(seedInput.text, out var parsed))
            seed = parsed;

        string path = instancePathInput != null ? instancePathInput.text : controller.instancePath;
        controller.ResetSim(seed, path);
        RefreshLabels();
    }

    private void OnSpeedChanged(int index)
    {
        if (controller == null) return;
        controller.SetSpeedMultiplier(GetSpeedFromDropdown(index));
    }

    private void OnShowRoutesChanged(bool value)
    {
        if (controller == null || controller.simRenderer == null) return;
        controller.simRenderer.SetShowRoutes(value);
    }

    private void RefreshLabels()
    {
        if (controller == null || playPauseLabel == null) return;
        playPauseLabel.text = controller.IsPlaying ? "Pause" : "Play";
    }

    private static float GetSpeedFromDropdown(int index)
    {
        switch (index)
        {
            case 0: return 1f;
            case 1: return 5f;
            case 2: return 20f;
            default: return 1f;
        }
    }
}
