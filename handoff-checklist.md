# Handoff Quick Checklist (New PC)

## 1) Build solver
- Open terminal in `Assets/HGS-Dynamic-CVRP`
- Run:
```powershell
cmd /c "call ""C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"" -arch=x64 -host_arch=x64 & cd /d ""<repo>\Assets\HGS-Dynamic-CVRP"" & ""C:\Program Files\CMake\bin\cmake.exe"" -S . -B build-win -G ""NMake Makefiles"" -DCMAKE_BUILD_TYPE=Release & ""C:\Program Files\CMake\bin\cmake.exe"" --build build-win --target bin_dynamic"
```
- Confirm exists: `Assets/HGS-Dynamic-CVRP/build-win/hgs_dynamic.exe`

## 2) Scene sanity
- Disable/remove `LoadInstanceDemo` in benchmark scene
- Ensure only one active `SimViewController`
- Add/enable `BenchmarkBatchRunner` and assign `controller`

## 3) SimViewController values
- `useHgsDynamicPlanner = true`
- `solverExecutablePath = hgs_dynamic.exe` (auto resolver handles path)
- `runStartupReplan = false`

## 4) BenchmarkBatchRunner values
- `runOnStart = true`
- `runAllBenchmarks = true`
- `runsPerBenchmark = 10`
- `benchmarksFolder = Assets/HGS-Dynamic-CVRP/Generated Benchmarks`
- `instancesFolder = Assets/HGS-Dynamic-CVRP/Instances/CVRP`
- `solutionsFolder = Assets/HGS-Dynamic-CVRP/Solutions`
- `outputFolder = Assets/HGS-Dynamic-CVRP/Run Results`
- `forceHgsPlanner = true`
- `forceAutoReplan = true`
- `runTruckSpeedOverride = 12` (increase if movement still hard to see)
- `runSpeedMultiplier = 20`
- `runFixedStep = 0.2`
- `runMaxSimStepsPerFrame = 20`
- `disablePerEventLogs = true`
- `disableUiOverlaysDuringBatch = true`

## 5) Expected startup logs
- `Queued ... runs`
- `Active instance=...`
- `State customers=... trucks=...`
- `Parsed ... route lines from ...sol`
- `Truck X seeded targets=...`
- `AutoReplan is OFF until first dynamic insertion`

## 6) Expected dynamic transition
- At first reveal time:
  - `Dynamic replanning activated at sim t=...`

## 7) Outputs
- Per-run solution files in:
  - `Assets/HGS-Dynamic-CVRP/Run Results`
- Each file contains:
  - `Route #...`
  - `Cost ...`
  - `SimTimeSeconds ...`
  - `WallTimeSeconds ...`
  - `Feasible 0/1`

## 8) If trucks still don’t move
- Check logs for `Truck speed override=...`
- Verify `Truck X seeded targets>0`
- Verify no duplicate controller/script is resetting sim mid-run
- Continue from `context.md` debug priorities
