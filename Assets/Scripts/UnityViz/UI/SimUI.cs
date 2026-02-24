using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class SimUI : MonoBehaviour
{
    [Header("Controller")]
    public SimViewController controller;
    public SimInputController inputController;

    [Header("UI References")]
    public TMP_InputField seedInput;
    public TMP_InputField instancePathInput;
    public TMP_InputField demandInput;
    public TMP_InputField serviceTimeInput;
    public TMP_Text playPauseLabel;
    public TMP_Text modeLabel;
    public TMP_Dropdown speedDropdown;
    public Button playPauseButton;
    public Button stepButton;
    public Button resetButton;
    public Button replanNowButton;
    public Toggle showRoutesToggle;
    public Toggle insertModeToggle;
    public Toggle autoReplanToggle;

    private bool? _lastIsPlaying;
    private bool? _lastInsertMode;
    private bool? _lastAutoReplan;

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
        if (inputController == null)
            inputController = FindAnyObjectByType<SimInputController>();
    }

    private void OnEnable()
    {
        if (playPauseButton != null) playPauseButton.onClick.AddListener(OnPlayPauseClicked);
        if (stepButton != null) stepButton.onClick.AddListener(OnStepClicked);
        if (resetButton != null) resetButton.onClick.AddListener(OnResetClicked);
        if (replanNowButton != null) replanNowButton.onClick.AddListener(OnReplanNowClicked);
        if (speedDropdown != null) speedDropdown.onValueChanged.AddListener(OnSpeedChanged);
        if (showRoutesToggle != null) showRoutesToggle.onValueChanged.AddListener(OnShowRoutesChanged);
        if (insertModeToggle != null) insertModeToggle.onValueChanged.AddListener(OnInsertModeChanged);
        if (autoReplanToggle != null) autoReplanToggle.onValueChanged.AddListener(OnAutoReplanChanged);

        RefreshLabels();
        SyncToggleState();
        ApplyInsertDefaults();
    }

    private void Update()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
        if (inputController == null)
            inputController = FindAnyObjectByType<SimInputController>();

        bool isPlaying = controller != null && controller.IsPlaying;
        bool insertMode = inputController != null && inputController.insertMode;
        bool autoReplan = controller != null && controller.autoReplan;

        if (_lastIsPlaying != isPlaying || _lastInsertMode != insertMode || _lastAutoReplan != autoReplan)
        {
            RefreshLabels();
            SyncToggleState();
            _lastIsPlaying = isPlaying;
            _lastInsertMode = insertMode;
            _lastAutoReplan = autoReplan;
        }
    }

    private void OnDisable()
    {
        if (playPauseButton != null) playPauseButton.onClick.RemoveListener(OnPlayPauseClicked);
        if (stepButton != null) stepButton.onClick.RemoveListener(OnStepClicked);
        if (resetButton != null) resetButton.onClick.RemoveListener(OnResetClicked);
        if (replanNowButton != null) replanNowButton.onClick.RemoveListener(OnReplanNowClicked);
        if (speedDropdown != null) speedDropdown.onValueChanged.RemoveListener(OnSpeedChanged);
        if (showRoutesToggle != null) showRoutesToggle.onValueChanged.RemoveListener(OnShowRoutesChanged);
        if (insertModeToggle != null) insertModeToggle.onValueChanged.RemoveListener(OnInsertModeChanged);
        if (autoReplanToggle != null) autoReplanToggle.onValueChanged.RemoveListener(OnAutoReplanChanged);
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
        SyncToggleState();
    }

    private void OnReplanNowClicked()
    {
        controller?.ReplanNow();
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

    private void OnInsertModeChanged(bool value)
    {
        if (inputController != null)
            inputController.SetInsertMode(value);

        ApplyInsertDefaults();
        RefreshLabels();
    }

    private void OnAutoReplanChanged(bool value)
    {
        controller?.SetAutoReplan(value);
    }

    private void RefreshLabels()
    {
        if (controller == null || playPauseLabel == null) return;
        playPauseLabel.text = controller.IsPlaying ? "Pause" : "Play";

        if (modeLabel != null)
            modeLabel.text = (inputController != null && inputController.insertMode) ? "Mode: Insert" : "Mode: Normal";
    }

    private void SyncToggleState()
    {
        if (insertModeToggle == null || inputController == null)
        {
            if (autoReplanToggle != null && controller != null)
                autoReplanToggle.SetIsOnWithoutNotify(controller.autoReplan);
            return;
        }

        insertModeToggle.SetIsOnWithoutNotify(inputController.insertMode);

        if (autoReplanToggle != null && controller != null)
            autoReplanToggle.SetIsOnWithoutNotify(controller.autoReplan);
    }

    private void ApplyInsertDefaults()
    {
        if (inputController == null) return;

        if (demandInput != null && int.TryParse(demandInput.text, out var demand))
            inputController.SetDefaultDemand(demand);

        if (serviceTimeInput != null && float.TryParse(serviceTimeInput.text, out var serviceTime))
            inputController.SetDefaultServiceTime(serviceTime);
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
