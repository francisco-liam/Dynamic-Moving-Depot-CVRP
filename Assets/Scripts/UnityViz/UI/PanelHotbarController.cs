using UnityEngine;
using UnityEngine.UI;

public sealed class PanelHotbarController : MonoBehaviour
{
    [Header("Buttons")]
    public Button controlPanelButton;
    public Button statsPanelButton;
    public Button eventsPanelButton;

    [Header("Panels")]
    public GameObject controlPanel;
    public GameObject statsPanel;
    public GameObject eventsPanel;

    [Header("Behavior")]
    [Tooltip("If true, opening one panel hides the other two.")]
    public bool singlePanelMode = false;

    [Tooltip("If true, all panels are hidden on start.")]
    public bool hideAllOnStart = false;

    private void OnEnable()
    {
        if (controlPanelButton != null) controlPanelButton.onClick.AddListener(ToggleControlPanel);
        if (statsPanelButton != null) statsPanelButton.onClick.AddListener(ToggleStatsPanel);
        if (eventsPanelButton != null) eventsPanelButton.onClick.AddListener(ToggleEventsPanel);

        if (hideAllOnStart)
        {
            SetPanelVisible(controlPanel, false);
            SetPanelVisible(statsPanel, false);
            SetPanelVisible(eventsPanel, false);
        }
    }

    private void OnDisable()
    {
        if (controlPanelButton != null) controlPanelButton.onClick.RemoveListener(ToggleControlPanel);
        if (statsPanelButton != null) statsPanelButton.onClick.RemoveListener(ToggleStatsPanel);
        if (eventsPanelButton != null) eventsPanelButton.onClick.RemoveListener(ToggleEventsPanel);
    }

    public void ToggleControlPanel()
    {
        TogglePanel(controlPanel);
    }

    public void ToggleStatsPanel()
    {
        TogglePanel(statsPanel);
    }

    public void ToggleEventsPanel()
    {
        TogglePanel(eventsPanel);
    }

    public void ShowOnlyControlPanel()
    {
        SetPanelVisible(controlPanel, true);
        SetPanelVisible(statsPanel, false);
        SetPanelVisible(eventsPanel, false);
    }

    public void ShowOnlyStatsPanel()
    {
        SetPanelVisible(controlPanel, false);
        SetPanelVisible(statsPanel, true);
        SetPanelVisible(eventsPanel, false);
    }

    public void ShowOnlyEventsPanel()
    {
        SetPanelVisible(controlPanel, false);
        SetPanelVisible(statsPanel, false);
        SetPanelVisible(eventsPanel, true);
    }

    private void TogglePanel(GameObject panel)
    {
        if (panel == null)
            return;

        bool newState = !panel.activeSelf;

        if (singlePanelMode && newState)
        {
            SetPanelVisible(controlPanel, false);
            SetPanelVisible(statsPanel, false);
            SetPanelVisible(eventsPanel, false);
        }

        SetPanelVisible(panel, newState);
    }

    private static void SetPanelVisible(GameObject panel, bool visible)
    {
        if (panel != null)
            panel.SetActive(visible);
    }
}
