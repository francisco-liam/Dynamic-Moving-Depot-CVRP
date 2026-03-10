# Dynamic-Moving-Depot-CVRP — HGS Dynamic + Benchmark Runner Context

## Project Snapshot
- Unity project root: `Dynamic-Moving-Depot-CVRP`
- HGS dynamic solver source: `Assets/HGS-Dynamic-CVRP`
- Built solver target: `hgs_dynamic.exe` (from CMake target `bin_dynamic`)
- Date of this context: 2026-03-10

## Primary Goal
Run benchmark scenarios from `Assets/HGS-Dynamic-CVRP/Generated Benchmarks` with this flow:
1. Start each run from static base solution (`Solutions/*.sol`)
2. Insert dynamic customers at scenario reveal times
3. Replan with `hgs_dynamic` using lock-boundary timing
4. Finish when all customers served and trucks returned to depot
5. Export per-run `.sol` with cost/time/feasibility

---

## What Was Implemented

### 1) HGS Dynamic Planner (already in project)
File: `Assets/Scripts/CoreSim/Planning/HgsDynamicPlanner.cs`
- Extracts state from `SimState`
- Builds `previousRoutes`, `lockedPrefixLength`, `customerActive`
- Writes dynamic JSON input for solver
- Calls external solver via `ProcessStartInfo`
- Parses solver `.sol` output into `PlanResult`
- Added: appends depot return target to each truck route (`AppendDepotReturn`) so routes finish at depot

### 2) Early lock-boundary trigger (already in project)
File: `Assets/Scripts/CoreSim/Sim/ReplanController.cs`
- Computes per-truck `time_to_next_lock`
- Uses trigger `min_time_to_next_lock <= planner_lead_time`
- `planner_lead_time = solver_time_budget + process_overhead_buffer + safety_margin`

### 3) Solver path resolution improvement
File: `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- Added automatic resolution for `solverExecutablePath` if user only enters `hgs_dynamic` / `hgs_dynamic.exe`
- Searches common project build locations

### 4) Startup replan control
File: `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- Added `runStartupReplan` bool
- `ResetSim()` now only calls initial `ReplanNow()` when `runStartupReplan == true`
- Needed so batch runner can seed static plan before dynamic replanning

### 5) Benchmark batch runner (new)
File: `Assets/Scripts/UnityViz/Runtime/BenchmarkBatchRunner.cs`
- Loads benchmark scenario JSONs (schema with `base_instance`, `new_customers`)
- Filters out non-scenario JSONs (e.g. `top_x_instances_by_slack.json`)
- Queues all scenarios × `runsPerBenchmark` with random seeds
- Resets sim per run
- Seeds initial truck plans from static `.sol`
- Inserts dynamic customers at `reveal_time`
- Keeps auto replan OFF initially, turns ON at first dynamic insertion, then triggers `ReplanNow()`
- Completion check: all customers served + all trucks idle at depot within tolerance
- Exports run results to `.sol` (routes + cost + sim/wall time + feasible flag)
- Added context menu: `Begin Batch`
- Added extensive logs for debugging route seeding/run activation

### 6) Runtime performance controls
Files:
- `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- `Assets/Scripts/UnityViz/Runtime/BenchmarkBatchRunner.cs`

Changes:
- Added `maxSimStepsPerFrame` in `SimViewController` to cap sim substeps per frame
- In benchmark runner, added perf-oriented settings:
  - disable per-event logs during runs
  - `runFixedStep`
  - `runMaxSimStepsPerFrame`
  - optional disabling of heavy UI overlays (`SimUI`, `SimStatsPanel`, `SimEventFeed`)
- Added truck speed override during benchmark runs (`runTruckSpeedOverride`) to make movement visible on CVRP coordinate scale

---

## Build Notes for hgs_dynamic
CMake file: `Assets/HGS-Dynamic-CVRP/CMakeLists.txt`
- Executable target exists: `bin_dynamic` with output `hgs_dynamic`

Working Windows build command used:
```powershell
cmd /c "call ""C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"" -arch=x64 -host_arch=x64 & cd /d ""C:\Users\liamf.UNR\Desktop\UnityProjects\Dynamic-Moving-Depot-CVRP\Assets\HGS-Dynamic-CVRP"" & ""C:\Program Files\CMake\bin\cmake.exe"" -S . -B build-win -G ""NMake Makefiles"" -DCMAKE_BUILD_TYPE=Release & ""C:\Program Files\CMake\bin\cmake.exe"" --build build-win --target bin_dynamic"
```

Expected output:
- `Assets/HGS-Dynamic-CVRP/build-win/hgs_dynamic.exe`

---

## Environment Blocker Encountered
On admin-managed machine, executable launch was blocked by endpoint policy (`hgs_dynamic.exe` flagged as risky).
- This is not a code failure.
- Workaround used: move to non-admin PC.
- Alternative future path: in-process DLL integration (P/Invoke to `hgscvrp` library) if EXE remains blocked.

---

## Current Issue at Handoff
Latest user-reported behavior before machine switch:
- Benchmark runner logs show seeding (`Truck X seeded targets=...`), but trucks still appeared not moving.
- FPS remained low in some runs, though step caps/perf toggles were added.

Potential reasons still to verify on new PC:
1. Scene has multiple controllers/scripts competing (`LoadInstanceDemo`, extra `SimViewController`, UI reset interactions)
2. Camera/renderer not following active state object
3. Seeded plan exists but targets not resolving (ID mapping edge case)
4. Truck speed still too low in effective state (override not applied or overwritten)

---

## Required Unity Scene Setup (for next session)
1. Ensure only one active `SimViewController` controls the sim.
2. Disable/remove `LoadInstanceDemo` in benchmark scene.
3. Add `BenchmarkBatchRunner` component and assign `controller`.
4. Suggested runner values:
   - `runOnStart = true`
   - `runAllBenchmarks = true`
   - `runsPerBenchmark = 10`
   - `benchmarksFolder = Assets/HGS-Dynamic-CVRP/Generated Benchmarks`
   - `instancesFolder = Assets/HGS-Dynamic-CVRP/Instances/CVRP`
   - `solutionsFolder = Assets/HGS-Dynamic-CVRP/Solutions`
   - `outputFolder = Assets/HGS-Dynamic-CVRP/Run Results`
   - `forceHgsPlanner = true`
   - `forceAutoReplan = true`
   - `runTruckSpeedOverride = 12` (or higher)
   - `runSpeedMultiplier = 20`
   - `runFixedStep = 0.2`
   - `runMaxSimStepsPerFrame = 20`
   - `disablePerEventLogs = true`
   - `disableUiOverlaysDuringBatch = true`
5. In `SimViewController`:
   - `useHgsDynamicPlanner = true`
   - solver path can be just `hgs_dynamic.exe` (auto-resolver added)

---

## High-Value Debug Checks to Run Next
If trucks still do not move on new PC:
1. Confirm logs:
   - `Active instance=...`
   - `State customers=... trucks=...`
   - `Truck speed override=...`
   - `Truck X seeded targets=...`
2. Add temporary log of per-truck first target resolution after seeding:
   - truck id, current target type/id, target exists?, speed, state
3. Verify only one simulation update loop is active (no duplicate controllers).

---

## Files Most Recently Modified
- `Assets/Scripts/CoreSim/Planning/HgsDynamicPlanner.cs`
- `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- `Assets/Scripts/UnityViz/Runtime/BenchmarkBatchRunner.cs`

---

## Request for Next Copilot Session
Continue from this state and prioritize:
1. Resolve “seeded but no movement” definitively
2. Validate benchmark runs produce output `.sol` files for all scenarios × 10 seeds
3. Keep BaselinePlanner compatibility intact
4. Preserve existing HGS dynamic JSON + time-budget argument wiring
