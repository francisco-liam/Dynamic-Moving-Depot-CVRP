```markdown
# Dynamic-Moving-Depot-CVRP — HGS Dynamic + Benchmark Runner Context

## Project Snapshot
- Unity project root: `Dynamic-Moving-Depot-CVRP`
- HGS dynamic solver source: `Assets/HGS-Dynamic-CVRP`
- Built solver target: `hgs_dynamic.exe` (from CMake target `bin_dynamic`)
- **Windows-native exe: `Assets/HGS-Dynamic-CVRP/hgs_dynamic.exe`** ← committed, use this
- `Assets/HGS-Dynamic-CVRP/build/hgs_dynamic.exe` is a **Linux ELF** binary — do NOT use on Windows
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

### 1) HGS Dynamic Planner
File: `Assets/Scripts/CoreSim/Planning/HgsDynamicPlanner.cs`
- Extracts state from `SimState`, builds `previousRoutes`/`lockedPrefixLength`/`customerActive`
- Writes dynamic JSON input for solver; calls external solver via `ProcessStartInfo`; parses `.sol` output
- Appends depot return target to each truck route (`AppendDepotReturn`)

**Critical indexing fixes (this session):**
- `BuildCustomerActive`: array size = `maxId` (= `nbClients + 1` = VRP DIMENSION), indexed by `c.Id - 1`. Depot slot 0 stays false.
- `BuildRemainingRouteAndLock`: sends `target.Id - 1` (internal index), not `target.Id`.
- Solution parse loop: `TargetRef.Customer(solvedRoute[r] + 1)` — solver emits 0-based internal indices; +1 for Unity ID.
- Extended VRP generation: dynamic customers (IDs > original DIMENSION) appended to a temp `.vrp` copy before each replan. Helpers: `ParseDimensionFromVrp`, `WriteExtendedVrpFile`.

**Stdout/stderr deadlock fix:**
- Was: sequential `ReadToEnd()` — deadlocks if stderr fills the 64 KB pipe buffer.
- Fixed: both streams read concurrently via `Task.Run` before `WaitForExit`.

**Debug infrastructure:**
- `KeepArtifactsForDebugging` — keeps temp JSON/sol/extended-vrp in `%TEMP%\dynamic-cvrp-hgs\`.
- `LogSolverOutput` — logs full CMD + stdout + stderr.
- Both OR'd with `BenchmarkLocalConfig.logSolverOutput` / `keepSolverArtifacts`.

### 2) Early lock-boundary trigger
File: `Assets/Scripts/CoreSim/Sim/ReplanController.cs`
- Trigger: `min_time_to_next_lock <= planner_lead_time`
- `planner_lead_time = solver_time_budget + process_overhead_buffer + safety_margin`
- `public IPlanner Planner => _planner;` exposed for external cast.

### 3) Solver path resolution
File: `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- Search order: project root → `HGS-Dynamic-CVRP/` → `build-win/` → `build/`
- Added `IsWindowsExecutable(path)`: reads first 2 bytes, checks MZ header — **skips Linux ELF binaries**.
- **Root cause of silent replan failures:** `build/hgs_dynamic.exe` is a Linux ELF. Was found first; Unity threw "not a valid application for this OS platform" (caught silently → NoOp plan). Fixed: HGS root searched first + MZ validation.

### 4) Startup replan control
File: `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- `runStartupReplan = false` → `ResetSim()` skips initial `ReplanNow()` (batch runner seeds its own static plan).

### 5) Benchmark batch runner
File: `Assets/Scripts/UnityViz/Runtime/BenchmarkBatchRunner.cs`
- Loads scenario JSONs, queues all × `runsPerBenchmark` with random seeds
- Seeds initial truck plans from static `.sol`; inserts dynamic customers at `reveal_time`
- **Batch replan fix:** was calling `ReplanNow()` per customer inside insertion loop. Fixed: insert all customers first, then one `ReplanNow()` per batch.
- Auto replan OFF initially; activated + one `ReplanNow()` fires per batch.
- Completion check, result export, context menu `Begin Batch`.

### 6) BenchmarkLocalConfig ScriptableObject (gitignored)
File: `Assets/Scripts/UnityViz/Runtime/BenchmarkLocalConfig.cs`
- `[CreateAssetMenu(menuName = "CVRP/Benchmark Local Config")]`
- Fields: `logSolverOutput`, `keepSolverArtifacts`, `speedMultiplierOverride`, `selectedScenariosOverride`
- Create at: `Assets/LocalConfig/BenchmarkLocalConfig.asset` (folder gitignored via `.gitignore`)
- Drag into `Local Config` slot on `BenchmarkBatchRunner` in Inspector
- All fields override their Inspector counterparts when non-zero/non-empty

### 7) Runtime performance controls
- `maxSimStepsPerFrame`, `runFixedStep`, `runMaxSimStepsPerFrame`
- `disablePerEventLogs`, `disableUiOverlaysDuringBatch`
- `runTruckSpeedOverride`, `runSpeedMultiplier`
- Context menu: `Reset Speed to 1:1`

---

## Coordinate Space
- VRP coordinates: 0–1000 integer units, EUC_2D; `UnityVec.ToUnity` = exact 1:1, no scaling
- Truck `Speed = 1f` → 1 VRP unit per sim-second
- Longest route in X-n143-k7 ≈ 3080 units → ~51 min sim-time at speed=1; use `runSpeedMultiplier` to accelerate

---

## HGS Solver Internal Indexing
| Concept | Unity ID | VRP node ID | Solver internal index |
|---|---|---|---|
| Depot | — | 1 | 0 |
| Customer N | N | N | N−1 |

- `customerActive` size = DIMENSION = `nbClients + 1` (slot 0 = depot, always false)
- `previousRoutes` entries: internal indices (Unity ID − 1)
- Solution output: internal indices → add 1 for Unity customer ID
- Dynamic customers (IDs > DIMENSION): appended to temp extended `.vrp`; new DIMENSION = original + count

---

## Build Notes for hgs_dynamic
**Pre-built Windows exe already committed:** `Assets/HGS-Dynamic-CVRP/hgs_dynamic.exe` — use this unless you need solver code changes.

Windows rebuild command:
```powershell
cmd /c "call ""C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"" -arch=x64 -host_arch=x64 & cd /d ""<repo>\Assets\HGS-Dynamic-CVRP"" & cmake.exe -S . -B build-win -G ""NMake Makefiles"" -DCMAKE_BUILD_TYPE=Release & cmake.exe --build build-win --target bin_dynamic"
```
Output: `Assets/HGS-Dynamic-CVRP/build-win/hgs_dynamic.exe`

**WARNING:** `build/hgs_dynamic.exe` is a Linux ELF — the resolver skips it automatically via MZ-header check.

---

## Current Status
All known bugs fixed. End-to-end pipeline:
1. `StartNextRun()` → `ResetSim` → seeds routes from static `.sol`
2. Sim advances → batch customers inserted → one `ReplanNow()` fires
3. `ReplanController` → `HgsDynamicPlanner` → resolver picks Windows exe → solver runs → plan applied
4. Trucks follow updated plan → completion check → results written → next run

---

## Required Unity Scene Setup
1. Only one active `SimViewController`; disable/remove `LoadInstanceDemo`.
2. Add `BenchmarkBatchRunner`, assign `controller`.
3. Suggested values:
   - `runOnStart = true`, `runsPerBenchmark = 10`
   - `forceHgsPlanner = true`, `forceAutoReplan = true`, `replanOnBatchReleaseOnly = true`
   - `runSolverTimeBudgetSeconds = 5`
   - `runTruckSpeedOverride = 1`, `runSpeedMultiplier = 60`
   - `runFixedStep = 0.05`, `runMaxSimStepsPerFrame = 4`
   - `disablePerEventLogs = true`, `disableUiOverlaysDuringBatch = true`
4. In `SimViewController`: `useHgsDynamicPlanner = true`, `solverExecutablePath = hgs_dynamic`, `runStartupReplan = false`.
5. Optional: create `Assets/LocalConfig/BenchmarkLocalConfig.asset`; set `keepSolverArtifacts = true` + `logSolverOutput = true` for diagnosis.

---

## Debug Checklist
If replanning fails:
1. Set `debugSolverOutput = true` → logs full CMD + stdout + stderr.
2. Check `[HGS] CMD:` — path must NOT be inside `build/`.
3. Check `[HGS] exitCode=` — non-zero means solver rejected input; see stderr for `EXCEPTION |`.
4. Inspect `%TEMP%\dynamic-cvrp-hgs\` (requires `keepSolverArtifacts = true`).
5. Verify `customerActive` length in JSON equals VRP DIMENSION (e.g. 143 for X-n143-k7).

---

## Files Most Recently Modified
- `Assets/Scripts/CoreSim/Planning/HgsDynamicPlanner.cs`
- `Assets/Scripts/CoreSim/Sim/ReplanController.cs`
- `Assets/Scripts/UnityViz/Runtime/SimViewController.cs`
- `Assets/Scripts/UnityViz/Runtime/BenchmarkBatchRunner.cs`
- `Assets/Scripts/UnityViz/Runtime/BenchmarkLocalConfig.cs` ← new
- `.gitignore` (added `Assets/LocalConfig/`)
```
