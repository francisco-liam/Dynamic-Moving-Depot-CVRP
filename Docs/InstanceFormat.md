# DMDVRP Instance Format (`.dmdvrp`)

This document defines the instance file format used by this project.

The format is based on TSPLIB / CVRPLIB CVRP instances, extended for:

- **Dynamic customers** via release times
- **Mobile depot** via candidate rendezvous stops (Option B)

This format is designed to be:
- easy to parse
- easy to edit manually
- version-control friendly

---

## 1. File Extension

Instances use the extension:

- `.dmdvrp`

---

## 2. Coordinate Convention

Instance coordinates are 2D.

- The instance uses `(x, y)`
- The simulation uses `(x, z)`
- Unity renders this on the XZ plane with Y-up

So:

- Instance `(x, y)` → Unity `(x, 0, y)`

---

## 3. General Grammar

The file contains:

1. **Header fields** (key/value pairs)
2. **Sections** (block data)
3. Optional `EOF`

Whitespace is flexible.
Tabs or spaces are allowed.

### 3.1 Header Format

Headers are written as:

KEY : VALUE
or
KEY: VALUE

Example:
NAME : X-n101-k25-dynamic
TYPE : DMDVRP
DIMENSION : 101
CAPACITY : 206

---

## 4. Required Headers

### 4.1 `NAME`
String identifier.

Example:
NAME : X-n101-k25

### 4.2 `TYPE`
Must be:
TYPE : DMDVRP

### 4.3 `DIMENSION`
Number of nodes in the instance, including depot nodes.

Nodes are expected to be indexed:

- `1..DIMENSION`

Example:
DIMENSION : 101

### 4.4 `CAPACITY`
Vehicle capacity for CVRP constraints.

Example:
CAPACITY : 206

---

## 5. Recommended Headers

### 5.1 `EDGE_WEIGHT_TYPE`
Currently expected:
- `EUC_2D`

Example:
EDGE_WEIGHT_TYPE : EUC_2D

(Other types may be supported later, but are not required for Phase 1.)

---

## 6. Custom Headers (Extensions)

### 6.1 `TRUCK_SPEED`
Truck travel speed in **distance units per simulated second**.

Example:
TRUCK_SPEED : 8.0

### 6.2 `DEPOT_SPEED`
Depot/carrier travel speed in **distance units per simulated second**.

Example:
DEPOT_SPEED : 5.0

---

## 7. Sections

A section begins with a section keyword on its own line, followed by data lines.

### Supported section keywords
- `NODE_COORD_SECTION`
- `DEMAND_SECTION`
- `RELEASE_TIME_SECTION`
- `DEPOT_STOP_SECTION`
- `DEPOT_CANDIDATE_STOP_SECTION`
- `DEPOT_SECTION`
- `EOF`

---

## 8. NODE_COORD_SECTION (Required)

Defines the coordinates for all nodes.

Format:
NODE_COORD_SECTION
<node_id> <x> <y>
...

Example:
NODE_COORD_SECTION
1 365 689
2 376 725
3 400 800

### Rules
- Node ids are expected in the range `1..DIMENSION`
- Coordinates are parsed as floats (integers are allowed)
- All nodes should be defined

---

## 9. DEMAND_SECTION (Required)

Defines customer demands.

Format:
DEMAND_SECTION
<node_id> <demand>
...

Example:
DEMAND_SECTION
1 0
2 10
3 7

### Rules
- Depot demand should be 0
- Demands are parsed as integers

---

## 10. RELEASE_TIME_SECTION (Optional)

Defines dynamic release times for customers.

Format:
RELEASE_TIME_SECTION
<node_id> <release_time>
...

Example:
RELEASE_TIME_SECTION
2 0
3 120
4 250

### Meaning
A customer is not considered available until:
sim_time >= release_time

### Rules
- If this section is omitted, all release times default to 0.
- Depot release time should be 0.
- `-1` terminator is optional (parser may accept either style).

---

## 11. Depot Candidate Stops (Option B)

The depot is mobile and can choose where to rendezvous.

Instead of defining a fixed depot schedule, the instance defines a set of **candidate stops**.

### 11.1 DEPOT_STOP_SECTION
Alias for candidate stops.

Format:
DEPOT_STOP_SECTION
<stop_id> <x> <y>
...

Example:
DEPOT_STOP_SECTION
1 365 689
2 400 720
3 450 760

### Meaning
Each stop defines a **possible rendezvous location** the depot can travel to.

These stops do NOT define times.

The depot has agency to decide:
- which stop to travel to
- when to travel there
- whether to remain there or move again

### Rules
- If this section is omitted, the depot defaults to a single candidate stop at its initial depot node position.

---

## 12. DEPOT_SECTION (Required)

Defines which node(s) are depots in the original CVRPLIB sense.

Format:
DEPOT_SECTION
<depot_node_id>
...
-1

Example:
DEPOT_SECTION
1
-1

### Rules
- This project currently assumes one depot node (typically node 1).
- Multiple depot nodes may be supported later.

---

## 13. EOF (Optional)

The file may end with:
EOF

---

## 14. Defaults / Error Handling

### Defaults
If a field is missing:
- `RELEASE_TIME_SECTION` → all release times default to 0
- `DEPOT_STOP_SECTION` → defaults to one stop at depot node position

### Required
These must exist for a valid instance:
- `DIMENSION`
- `CAPACITY`
- `NODE_COORD_SECTION`
- `DEMAND_SECTION`
- `DEPOT_SECTION`

---

## 15. Units

This project assumes consistent units:

- coordinates: arbitrary distance units
- speeds: distance units / second
- release times: seconds

The simulation is unitless beyond consistency.

---

## 16. Notes for Future Extensions

This format is designed to be extended with additional optional sections, such as:

- `SERVICE_TIME_SECTION`
- `TIME_WINDOW_SECTION`
- `TRAVEL_TIME_MATRIX_SECTION`
- depot mobility region constraints (Option A)
- stochastic customer arrivals

The parser is intended to ignore unknown headers for forward compatibility.