"""
estimate_run_times.py
─────────────────────
For each benchmark scenario in this folder, compute the expected sim-time a
single run should take when all trucks move at 1 unit/second (no speedMultiplier).

Methodology
───────────
1.  Load the base .vrp instance (Euclidean coordinates, speed = 1 u/s).
2.  Load the reference .sol static solution.
3.  Simulate each truck's route at speed 1 to find:
      • per-truck finish time  (travel + service for every customer + return to depot)
      • baseline_finish        = max truck finish time across all trucks
4.  From the scenario JSON read:
      • safe_reveal_horizon    = latest time a static customer could appear
      • batch_times            = reveal times of the 3 dynamic batches
      • new_customers          = the dynamic customers (with positions)
5.  For each dynamic customer, estimate extra time:
      • distance from depot to customer position  (worst-case single-leg detour)
      We don't know the actual replanned route, so we use the average distance
      from depot to new customers as a conservative add-on per dynamic customer.
6.  dynamic_overhead = avg_depot_dist_to_new_customer × (new_customer_count / vehicle_count)
7.  recommended_timeout = baseline_finish + dynamic_overhead  (rounded up, + 20% buffer)

Output: a table printed to stdout.  Run from the repo root or this folder.

Usage
─────
    python estimate_run_times.py [--instances_dir ...] [--solutions_dir ...]
"""

from __future__ import annotations

import argparse
import json
import math
import re
from pathlib import Path
from dataclasses import dataclass


# ── data types ────────────────────────────────────────────────────────────────

@dataclass
class NodePos:
    x: float
    y: float


def dist(a: NodePos, b: NodePos) -> float:
    return math.hypot(a.x - b.x, a.y - b.y)


# ── parsers ───────────────────────────────────────────────────────────────────

def parse_vrp(path: Path) -> tuple[NodePos, dict[int, NodePos]]:
    """Return (depot_pos, {node_id: NodePos}) excluding depot."""
    lines = path.read_text(encoding="utf-8").splitlines()
    coords: dict[int, NodePos] = {}
    depot_ids: list[int] = []
    section = "HEADER"

    for raw in lines:
        line = raw.strip()
        if not line:
            continue
        up = line.upper()
        if up.startswith("NODE_COORD_SECTION"):
            section = "NODE"
            continue
        if up.startswith("DEPOT_SECTION"):
            section = "DEPOT"
            continue
        if up.startswith("DEMAND_SECTION") or up.startswith("EOF"):
            section = "OTHER"
            continue

        if section == "NODE":
            parts = line.split()
            if len(parts) >= 3:
                coords[int(parts[0])] = NodePos(float(parts[1]), float(parts[2]))
        elif section == "DEPOT":
            val = int(line.split()[0])
            if val != -1:
                depot_ids.append(val)

    if not depot_ids:
        raise ValueError(f"No depot found in {path}")
    depot_id = depot_ids[0]
    depot = coords.pop(depot_id)
    return depot, coords


def parse_sol(path: Path) -> list[list[int]]:
    """Return list of routes; each route is a list of customer node IDs."""
    routes: list[list[int]] = []
    route_re = re.compile(r"^\s*Route\s*#?\d*\s*:\s*(.*)", re.IGNORECASE)
    for raw in path.read_text(encoding="utf-8").splitlines():
        m = route_re.match(raw.strip())
        if m:
            ids = [int(t) for t in m.group(1).split() if t.lstrip("-").isdigit() and int(t) > 0]
            if ids:
                routes.append(ids)
    if not routes:
        raise ValueError(f"No routes parsed from {path}")
    return routes


# ── core timing logic ─────────────────────────────────────────────────────────

def normalise_route_ids(routes: list[list[int]], customer_nodes: dict[int, NodePos]) -> list[list[int]]:
    """
    Some solution files use 0-based or 1-offset customer IDs.
    Try direct match, then try +1 shift.
    """
    all_ids = [cid for r in routes for cid in r]
    if all(cid in customer_nodes for cid in all_ids):
        return routes
    shifted = [[cid + 1 for cid in r] for r in routes]
    all_shifted = [cid for r in shifted for cid in r]
    if all(cid in customer_nodes for cid in all_shifted):
        return shifted
    unknown = sorted(set(all_ids) - customer_nodes.keys())
    raise ValueError(f"Route IDs don't match instance (sample unknown: {unknown[:5]})")


def compute_route_finish_time(
    route: list[int],
    depot: NodePos,
    nodes: dict[int, NodePos],
    speed: float = 1.0,
    service_time_per_customer: float = 0.0,
) -> float:
    """Travel time from depot → route → depot at given speed, with optional service time."""
    if not route:
        return 0.0
    t = 0.0
    prev = depot
    for cid in route:
        c = nodes[cid]
        t += dist(prev, c) / speed
        t += service_time_per_customer
        prev = c
    t += dist(prev, depot) / speed   # return to depot
    return t


def compute_baseline(
    routes: list[list[int]],
    depot: NodePos,
    nodes: dict[int, NodePos],
    speed: float = 1.0,
) -> tuple[float, list[float]]:
    """Return (max_finish_time, per_truck_finish_times)."""
    times = [compute_route_finish_time(r, depot, nodes, speed) for r in routes]
    return max(times) if times else 0.0, times


# ── main ──────────────────────────────────────────────────────────────────────

def run(
    benchmarks_dir: Path,
    instances_dir: Path,
    solutions_dir: Path,
    speed: float = 1.0,
) -> None:
    scenario_files = sorted(
        p for p in benchmarks_dir.glob("*.json")
        if not p.stem.startswith("top_")        # skip the ranking summary file
    )

    if not scenario_files:
        print("No scenario JSON files found.")
        return

    # column widths
    W_NAME  = 20
    W_VEH   = 5
    W_TIME  = 10
    W_BATCH = 12

    header = (
        f"{'Scenario':<{W_NAME}} "
        f"{'Veh':>{W_VEH}} "
        f"{'Baseline':>{W_TIME}} "
        f"{'Batch-1':>{W_BATCH}} "
        f"{'Batch-2':>{W_BATCH}} "
        f"{'Batch-3':>{W_BATCH}} "
        f"{'SafeHorizon':>{W_BATCH}} "
        f"{'+DynEst':>{W_TIME}} "
        f"{'RecommTimeout':>{W_TIME}} "
        f"  Notes"
    )
    print(header)
    print("─" * len(header))

    for scenario_path in scenario_files:
        try:
            payload = json.loads(scenario_path.read_text(encoding="utf-8"))
        except Exception as exc:
            print(f"  [SKIP] {scenario_path.name}: JSON parse error – {exc}")
            continue

        # Require minimal schema
        if "base_instance" not in payload or "batch_times" not in payload:
            print(f"  [SKIP] {scenario_path.name}: not a scenario file")
            continue

        base_name        = payload["base_instance"]
        vehicle_count    = payload.get("vehicle_count", "?")
        safe_horizon     = payload.get("safe_reveal_horizon", 0.0)
        batch_times_raw  = payload.get("batch_times", {})
        new_customers    = payload.get("new_customers", [])
        total_added_dem  = payload.get("total_added_demand", 0)

        # Sort batch reveal times numerically regardless of string keys
        batch_sorted = sorted(float(v) for v in batch_times_raw.values())
        last_batch   = batch_sorted[-1] if batch_sorted else safe_horizon

        # Locate files
        vrp_path = instances_dir / f"{base_name}.vrp"
        sol_path = solutions_dir / f"{base_name}.sol"

        notes: list[str] = []

        if not vrp_path.exists():
            print(
                f"  {'[MISSING .vrp]':<{W_NAME}} {base_name}")
            continue
        if not sol_path.exists():
            print(
                f"  {'[MISSING .sol]':<{W_NAME}} {base_name}")
            continue

        try:
            depot, nodes = parse_vrp(vrp_path)
            routes_raw   = parse_sol(sol_path)
            routes       = normalise_route_ids(routes_raw, nodes)
        except Exception as exc:
            print(f"  [ERROR] {base_name}: {exc}")
            continue

        baseline, truck_times = compute_baseline(routes, depot, nodes, speed)

        # Dynamic overhead estimate:
        # Average distance from depot to each new customer position × (n_new / n_vehicles)
        # This is a rough lower bound on extra travel the replanner will add.
        if new_customers:
            nc_positions = [NodePos(c["x"], c["y"]) for c in new_customers]
            avg_depot_dist = sum(dist(depot, p) for p in nc_positions) / len(nc_positions)
            n_new = len(new_customers)
            n_veh = max(1, len(routes))
            dyn_overhead = avg_depot_dist * (n_new / n_veh)
        else:
            dyn_overhead = 0.0
            notes.append("no dynamic customers")

        # Recommended timeout: whichever is larger – baseline or last_batch –
        # plus dynamic overhead, then a 20% buffer, ceiled to nearest second.
        raw_estimate   = max(baseline, last_batch) + dyn_overhead
        recommended    = math.ceil(raw_estimate * 1.20)

        # Format batch times (up to 3)
        def fmt_batch(idx: int) -> str:
            return f"{batch_sorted[idx]:.1f}s" if idx < len(batch_sorted) else "—"

        if truck_times:
            min_t, max_t = min(truck_times), max(truck_times)
            if max_t > 0 and (max_t - min_t) / max_t > 0.30:
                notes.append(f"unbalanced routes ({min_t:.0f}–{max_t:.0f}s)")

        note_str = ";  ".join(notes) if notes else ""

        print(
            f"{base_name:<{W_NAME}} "
            f"{vehicle_count:>{W_VEH}} "
            f"{baseline:>{W_TIME}.1f}s "
            f"{fmt_batch(0):>{W_BATCH}} "
            f"{fmt_batch(1):>{W_BATCH}} "
            f"{fmt_batch(2):>{W_BATCH}} "
            f"{safe_horizon:>{W_BATCH}.1f}s "
            f"{dyn_overhead:>{W_TIME}.1f}s "
            f"{recommended:>{W_TIME}}s "
            f"  {note_str}"
        )

    print()
    print("Columns")
    print("  Baseline       : time for the longest static route (depot→customers→depot) at speed=1")
    print("  Batch-1/2/3    : sim-time when each dynamic batch of customers is revealed")
    print("  SafeHorizon    : benchmark's safe_reveal_horizon (= min last-customer-time across routes)")
    print("  +DynEst        : rough extra time for dynamic customers (avg depot-distance × n_new/n_veh)")
    print("  RecommTimeout  : max(Baseline, Batch-3) + DynEst + 20% buffer – use as Unity run timeout")


def main() -> None:
    here = Path(__file__).parent.resolve()
    repo = here.parent.parent.parent      # Generated Benchmarks → HGS-Dynamic-CVRP → Assets → repo root

    parser = argparse.ArgumentParser(description="Estimate sim-time per benchmark run at speed=1.")
    parser.add_argument(
        "--benchmarks_dir",
        default=str(here),
        help="Folder containing scenario JSON files (default: script's own folder)",
    )
    parser.add_argument(
        "--instances_dir",
        default=str(here.parent / "Instances" / "CVRP"),
        help="Folder containing .vrp instance files",
    )
    parser.add_argument(
        "--solutions_dir",
        default=str(here.parent / "Solutions"),
        help="Folder containing .sol static solution files",
    )
    parser.add_argument(
        "--speed",
        type=float,
        default=1.0,
        help="Truck speed in coordinate-units per second (default: 1.0)",
    )
    args = parser.parse_args()

    run(
        benchmarks_dir=Path(args.benchmarks_dir),
        instances_dir=Path(args.instances_dir),
        solutions_dir=Path(args.solutions_dir),
        speed=args.speed,
    )


# Fix: reference W_VEH in the formatting string (typo guard)
W_VEH = 5

if __name__ == "__main__":
    main()
