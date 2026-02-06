# Dynamic Mobile-Depot VRP (Unity Simulation)

This project is a Unity-based simulation environment for a **Dynamic Capacitated Vehicle Routing Problem (DCVRP)** with:

- **Dynamic customer insertion** (customers appear while vehicles are already operating)
- A **mobile depot** (e.g., an aircraft carrier / mothership concept)
- A clean separation between:
  - **Core simulation + planning logic** (pure C#, no Unity dependencies)
  - **Unity visualization + interaction**

The long-term goal is to support advanced planners (ex: HGS-style metaheuristics),
but the project is intentionally being built in phases so the environment is stable first.

---

## Coordinate System Convention

This project uses a 2D routing plane mapped onto Unity’s 3D world:

- Routing coordinates are **(x, z)**
- Unity uses:
  - `x` = x
  - `z` = y from the instance file
  - `y` = up (always 0 for routing)

In CoreSim, all routing geometry is stored in a 2D vector:

- `Vec2.X` = x
- `Vec2.Y` = z

---

## Folder Structure

### `Assets/`
Unity project content.

### `Assets/StreamingAssets/Instances/`
Instance files live here. These are loaded via file I/O using:

- `Application.streamingAssetsPath`

This is used instead of `Resources/` so that:
- instances remain plain text in builds
- instances can be swapped/edited without Unity serialization

---

## CoreSim vs UnityViz

### `/CoreSim/`
Pure C# logic. **No UnityEngine types** should appear here.

Contains:
- Instance parsing + DTOs
- Simulation state models
- (Later) event system, validation, planners

### `/UnityViz/`
Unity MonoBehaviours and visualization/UI scripts.

Contains:
- Scene bootstrap
- Loading instances from StreamingAssets
- Debug rendering / UI

---

## Current Phase (Phase 1): Instance Loading + Runtime State

Phase 1 establishes the pipeline:

`.dmdvrp file` → `InstanceParser` → `InstanceDto` → `InstanceMapper` → `SimState`

No simulation movement, routing, or planning is implemented yet.

---

## Instance File Format (`.dmdvrp`)

This format is based on CVRPLIB / TSPLIB CVRP instances, with extra fields for:

- Dynamic customer release times
- Mobile depot candidate rendezvous points

### Supported Headers
- `NAME`
- `COMMENT`
- `TYPE` (should be `DMDVRP`)
- `DIMENSION`
- `EDGE_WEIGHT_TYPE` (currently assumed Euclidean)
- `CAPACITY`

Custom additions:
- `TRUCK_SPEED`
- `DEPOT_SPEED`

### Supported Sections
- `NODE_COORD_SECTION`
- `DEMAND_SECTION`
- `RELEASE_TIME_SECTION` (optional; defaults to 0 for all nodes)
- `DEPOT_STOP_SECTION` OR `DEPOT_CANDIDATE_STOP_SECTION` (optional)
- `DEPOT_SECTION`
- `EOF`

### Depot Stops (Option B: Candidate Stops)
Depot stops are interpreted as **candidate rendezvous locations**.

Format:
DEPOT_STOP_SECTION
<stop_id> <x> <y>
...


These stops do NOT specify times.
The depot has agency to choose where to go during simulation/planning.

---

## Key Source Files (Phase 0–1)

### CoreSim

#### `CoreSim/SimConfig.cs`
Runtime configuration for a simulation run.

This is NOT part of the instance file.
It contains run-time controls and experiment knobs, including:
- `Seed`
- `TimeScale`
- replanning policy settings
- optional speed overrides

#### `CoreSim/Math/Vec2.cs`
Minimal 2D vector used in CoreSim.

This avoids dependency on Unity types (`Vector2`, `Vector3`).

#### `CoreSim/Utils/DeterministicRng.cs`
Deterministic RNG wrapper.
Used instead of `UnityEngine.Random` so runs are reproducible.

#### `CoreSim/Utils/SimLogger.cs`
Simple buffered logger.
Designed to remain Unity-independent.

---

### Instance IO

#### `CoreSim/IO/InstanceDto.cs`
Data Transfer Object (DTO) representation of an instance file.
This is the “raw” loaded instance before converting to runtime objects.

#### `CoreSim/IO/InstanceParser.cs`
Text parser for `.dmdvrp` files.

Responsibilities:
- read headers
- read standard CVRP sections
- read custom dynamic sections
- populate an `InstanceDto`

#### `CoreSim/IO/InstanceMapper.cs`
Converts `InstanceDto` → runtime `SimState`.

Responsibilities:
- apply `SimConfig` overrides
- create `Customer` objects
- create `DepotCarrier` + candidate stops
- optionally create demo trucks for visualization

---

### Runtime Model

#### `CoreSim/Model/Customer.cs`
Represents a customer node in the runtime simulation.

Includes:
- position
- demand
- release time
- status (Unreleased / Available / Served)

#### `CoreSim/Model/Truck.cs`
Represents a truck in the runtime simulation.

Includes:
- position
- capacity + current load
- speed

#### `CoreSim/Model/DepotCarrier.cs`
Represents the mobile depot in the runtime simulation.

Includes:
- current position
- speed
- list of candidate rendezvous stops

#### `CoreSim/Model/SimState.cs`
Top-level simulation state container.

Includes:
- simulation time
- customers
- depot
- trucks
- global capacity

---

## UnityViz Demo Scripts

#### `UnityViz/SimBootstrap.cs`
Minimal simulation bootstrap for Phase 0.
Creates a run context, logs the seed, and prints simulation time.

#### `UnityViz/LoadInstanceDemo.cs`
Loads an instance from StreamingAssets, parses it, maps it into `SimState`,
and prints a summary.

---

## Running the Current Demo

1. Place an instance file in:
   - `Assets/StreamingAssets/Instances/`

2. Create an empty GameObject in the Unity scene.

3. Add:
   - `SimBootstrap`
   - `LoadInstanceDemo`

4. Press Play.

You should see logs confirming:
- seed
- instance loaded
- number of customers
- number of depot candidate stops

---

## Next Phase (Phase 2)

Phase 2 will introduce:
- simulation time advancement (CoreSim-owned clock)
- event queue
- depot movement + truck movement
- customer release events
- truck arrival events

At that point, the environment becomes "live" and ready for dynamic insertion.

---

## Notes / Conventions

- CoreSim should remain Unity-independent so we can run headless experiments later.
- Instances should remain text-based and easy to version control.
- Determinism is prioritized early to support debugging and algorithm evaluation.