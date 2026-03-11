using UnityEngine;

/// <summary>
/// Local-only debug settings for BenchmarkBatchRunner.
/// Store an instance of this asset in Assets/LocalConfig/ which is gitignored,
/// so each developer can have their own settings without polluting commits.
///
/// Create via: Assets → Create → CVRP → Benchmark Local Config
/// Then drag the asset into the BenchmarkBatchRunner.localConfig slot.
/// </summary>
[CreateAssetMenu(menuName = "CVRP/Benchmark Local Config", fileName = "BenchmarkLocalConfig")]
public sealed class BenchmarkLocalConfig : ScriptableObject
{
    [Header("Solver Diagnostics")]
    [Tooltip("Log the full solver command, stdout and stderr to the Console after every replan.")]
    public bool logSolverOutput = false;

    [Tooltip("Keep the temp JSON input and .sol output in %TEMP%/dynamic-cvrp-hgs/ after each replan so you can inspect them.")]
    public bool keepSolverArtifacts = false;

    [Header("Speed")]
    [Tooltip("Overrides runSpeedMultiplier on BenchmarkBatchRunner when > 0. Set to 0 to use the runner's own value.")]
    public float speedMultiplierOverride = 0f;

    [Header("Scenario Filter")]
    [Tooltip("Overrides selectedScenarios on BenchmarkBatchRunner when non-empty. Leave empty to use the runner's own list.")]
    public string[] selectedScenariosOverride = new string[0];
}
