```markdown
# Handoff Quick Checklist

## 1) Solver exe
- **Use the pre-built Windows exe:** `Assets/HGS-Dynamic-CVRP/hgs_dynamic.exe` — already committed, works on Windows.
- Do NOT use `Assets/HGS-Dynamic-CVRP/build/hgs_dynamic.exe` — that is a **Linux ELF** binary (built in WSL).
- Rebuild only if you changed solver C++ code:

```powershell
cmd /c "call ""C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"" -arch=x64 -host_arch=x64 & cd /d ""<repo>\Assets\HGS-Dynamic-CVRP"" & cmake.exe -S . -B build-win -G ""NMake Makefiles"" -DCMAKE_BUILD_TYPE=Release & cmake.exe --build build-win --target bin_dynamic"
```

## 2) Scene sanity
- Disable/remove `LoadInstanceDemo` in benchmark scene
- Ensure only one active `SimViewController`
- Add/enable `BenchmarkBatchRunner` and assign `controller`

## 3) SimViewController values
- `useHgsDynamicPlanner = true`
- `solverExecutablePath = hgs_dynamic` (auto-resolver finds Windows exe, validates MZ header)
- `runStartupReplan = false`

## 4) BenchmarkBatchRunner values
- `runOnStart = true`
- `runsPerBenchmark = 10`
- `benchmarksFolder = Assets/HGS-Dynamic-CVRP/Generated Benchmarks`
- `instancesFolder = Assets/HGS-Dynamic-CVRP/Instances/CVRP`
- `solutionsFolder = Assets/HGS-Dynamic-CVRP/Solutions`
- `outputFolder = Assets/HGS-Dynamic-CVRP/Run Results`
- `forceHgsPlanner = true`
- `forceAutoReplan = true`
- `replanOnBatchReleaseOnly = true`
- `runSolverTimeBudgetSeconds = 5`
- `runTruckSpeedOverride = 1`
- `runSpeedMultiplier = 60`
- `runFixedStep = 0.05`
- `runMaxSimStepsPerFrame = 4`
- `disablePerEventLogs = true`
- `disableUiOverlaysDuringBatch = true`

## 5) Optional: local debug config (gitignored)
1. Right-click `Assets/LocalConfig/` → Create → CVRP → Benchmark Local Config
2. Name it `BenchmarkLocalConfig`
3. Drag into the `Local Config` slot on `BenchmarkBatchRunner`
4. Tick `Log Solver Output` + `Keep Solver Artifacts` to diagnose replan failures
5. Set `Speed Multiplier Override` or `Selected Scenarios Override` as needed

## 6) Expected startup logs
```
[BenchmarkRunner] Queued N runs
[BenchmarkRunner] START X-n143-k7 run=0 seed=...
[BenchmarkRunner] Parsed N route lines from ...sol
[BenchmarkRunner] Truck X seeded targets=N
[BenchmarkRunner] AutoReplan is OFF until first dynamic insertion
```

## 7) Expected dynamic transition
At first reveal time:
```
[BenchmarkRunner] Dynamic replanning activated at sim t=...
[BenchmarkRunner] Replan triggered for batch of N inserts at sim t=...
[HGS] CMD: ...HGS-Dynamic-CVRP\hgs_dynamic.exe ...   ← must NOT be inside build/
[HGS] OK — assignedCustomers=N vehicles=N
```

## 8) Outputs
Per-run solution files in `Assets/HGS-Dynamic-CVRP/Run Results/`:
```
Route #1: 5 12 7 ...
Cost 12345.6
SimTimeSeconds 3080.0
WallTimeSeconds 12.4
Feasible 1
```

## 9) If replanning fails silently
1. Set `debugSolverOutput = true` on `BenchmarkBatchRunner` → logs full CMD + stdout + stderr
2. Look for `[HGS] exitCode=1` or `EXCEPTION |` in Console
3. Check `[HGS] CMD:` path — must NOT be inside `build/`
4. Enable `keepSolverArtifacts` and inspect `%TEMP%\dynamic-cvrp-hgs\` for JSON + `.sol`
5. Verify `customerActive` array length = VRP DIMENSION (e.g. 143 for X-n143-k7)
```
