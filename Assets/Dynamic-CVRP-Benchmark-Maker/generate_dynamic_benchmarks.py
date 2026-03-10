from __future__ import annotations

import argparse
import json
import math
import random
import re
import subprocess
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Optional


@dataclass
class Customer:
    id: int
    x: float
    y: float
    demand: int


@dataclass
class Route:
    vehicle_id: int
    customers: list[int]
    cost: float = 0.0
    arrival_times: dict[int, float] | None = None
    last_customer_time: float = 0.0


@dataclass
class InstanceData:
    name: str
    depot: Customer
    customers: dict[int, Customer]
    vehicle_count: int
    capacity: int


@dataclass
class ScenarioCustomer:
    id: int
    parent_id: int
    x: float
    y: float
    demand: int
    inherited_phase: float
    batch_id: int
    reveal_time: float


@dataclass
class ScenarioMetadata:
    base_instance: str
    seed: int
    vehicle_count: int
    capacity: int
    original_cost: float
    augmented_static_cost: float | None
    safe_reveal_horizon: float
    added_demand_budget: int
    total_added_demand: int
    batch_times: dict[int, float]


def euclidean(a_x: float, a_y: float, b_x: float, b_y: float) -> float:
    return math.hypot(a_x - b_x, a_y - b_y)


def _extract_vehicle_count(name: str) -> Optional[int]:
    # Uchoa files usually include "-kNN" in name, e.g. X-n101-k25.
    match = re.search(r"-k(\d+)", name)
    if match:
        return int(match.group(1))
    return None


def load_uchoa_instance(instance_path: str) -> InstanceData:
    path = Path(instance_path)
    if not path.exists():
        raise FileNotFoundError(f"Instance file not found: {instance_path}")

    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines:
        raise ValueError(f"Instance file is empty: {instance_path}")

    meta: dict[str, str] = {}
    coords: dict[int, tuple[float, float]] = {}
    demands: dict[int, int] = {}
    depot_ids: list[int] = []

    section = "HEADER"
    for raw in lines:
        line = raw.strip()
        if not line:
            continue

        upper = line.upper()
        if upper.startswith("NODE_COORD_SECTION"):
            section = "NODE"
            continue
        if upper.startswith("DEMAND_SECTION"):
            section = "DEMAND"
            continue
        if upper.startswith("DEPOT_SECTION"):
            section = "DEPOT"
            continue
        if upper.startswith("EOF"):
            break

        if section == "HEADER":
            if ":" in line:
                key, value = line.split(":", 1)
                meta[key.strip().upper()] = value.strip().strip('"')
            continue

        if section == "NODE":
            parts = line.split()
            if len(parts) < 3:
                raise ValueError(f"Malformed NODE_COORD_SECTION line: {line}")
            node_id = int(parts[0])
            x = float(parts[1])
            y = float(parts[2])
            coords[node_id] = (x, y)
            continue

        if section == "DEMAND":
            parts = line.split()
            if len(parts) < 2:
                raise ValueError(f"Malformed DEMAND_SECTION line: {line}")
            node_id = int(parts[0])
            demand = int(parts[1])
            demands[node_id] = demand
            continue

        if section == "DEPOT":
            val = int(line.split()[0])
            if val == -1:
                continue
            depot_ids.append(val)
            continue

    name = meta.get("NAME", path.stem)
    capacity_raw = meta.get("CAPACITY")
    if capacity_raw is None:
        raise ValueError("CAPACITY not found in instance header")
    capacity = int(capacity_raw)

    if not depot_ids:
        raise ValueError("DEPOT_SECTION missing depot id")
    depot_id = depot_ids[0]

    if depot_id not in coords:
        raise ValueError(f"Depot id {depot_id} missing coordinates")
    if depot_id not in demands:
        raise ValueError(f"Depot id {depot_id} missing demand")

    vehicle_count = _extract_vehicle_count(name) or _extract_vehicle_count(path.stem)
    if vehicle_count is None:
        raise ValueError(
            "Vehicle count could not be inferred. Expected instance name like X-n101-k25."
        )

    depot = Customer(
        id=depot_id,
        x=coords[depot_id][0],
        y=coords[depot_id][1],
        demand=demands[depot_id],
    )

    customers: dict[int, Customer] = {}
    for node_id, (x, y) in coords.items():
        if node_id == depot_id:
            continue
        if node_id not in demands:
            raise ValueError(f"Customer {node_id} missing demand")
        customers[node_id] = Customer(id=node_id, x=x, y=y, demand=demands[node_id])

    if not customers:
        raise ValueError("No non-depot customers found in instance")

    return InstanceData(
        name=name,
        depot=depot,
        customers=customers,
        vehicle_count=vehicle_count,
        capacity=capacity,
    )


def load_reference_solution(solution_path: str) -> tuple[list[Route], float | None]:
    """
    Assumes a simple route text format with lines like:
      Route #1: 2 5 8
      Route #2: 3 4
      Cost 27591
    """
    path = Path(solution_path)
    if not path.exists():
        raise FileNotFoundError(f"Solution file not found: {solution_path}")

    lines = path.read_text(encoding="utf-8").splitlines()
    routes: list[Route] = []
    declared_cost: float | None = None

    route_pattern = re.compile(r"^\s*Route\s*#?\d*\s*:\s*(.*)$", re.IGNORECASE)
    number_pattern = re.compile(r"-?\d+")

    for raw in lines:
        line = raw.strip()
        if not line:
            continue

        route_match = route_pattern.match(line)
        if route_match:
            tail = route_match.group(1)
            seq = [int(tok) for tok in number_pattern.findall(tail)]
            seq = [node for node in seq if node > 0]
            routes.append(Route(vehicle_id=len(routes) + 1, customers=seq))
            continue

        if re.search(r"\b(cost|objective|obj)\b", line, flags=re.IGNORECASE):
            vals = re.findall(r"[-+]?\d*\.?\d+", line)
            if vals:
                declared_cost = float(vals[-1])

    if not routes:
        raise ValueError(
            "No routes parsed from solution. Expected lines like 'Route #1: ...'."
        )

    return routes, declared_cost


def compute_route_timelines(instance: InstanceData, routes: list[Route]) -> list[Route]:
    def normalize_solution_ids() -> None:
        """
        Some CVRP solution files index customers as 1..n-1 (excluding depot),
        while Uchoa instance files use node IDs with depot at 1 and customers 2..n.
        This remaps by +1 when that pattern is detected.
        """
        all_ids = [cid for route in routes for cid in route.customers]
        if not all_ids:
            return

        customer_ids = set(instance.customers.keys())
        if set(all_ids).issubset(customer_ids):
            return

        shifted = [cid + 1 for cid in all_ids]
        if set(shifted).issubset(customer_ids):
            for route in routes:
                route.customers = [cid + 1 for cid in route.customers]
            return

        unknown = sorted(set(all_ids) - customer_ids)
        raise ValueError(
            "Solution customer IDs do not match instance IDs, and +1 remapping did not "
            f"resolve the mismatch. Unknown IDs (sample): {unknown[:10]}"
        )

    normalize_solution_ids()

    seen: set[int] = set()
    all_customers = set(instance.customers.keys())

    for route in routes:
        route.arrival_times = {}
        x_prev = instance.depot.x
        y_prev = instance.depot.y
        t = 0.0

        for cid in route.customers:
            if cid not in instance.customers:
                raise ValueError(f"Solution references unknown customer id: {cid}")
            if cid in seen:
                raise ValueError(f"Customer {cid} appears multiple times in solution")
            seen.add(cid)

            cust = instance.customers[cid]
            t += euclidean(x_prev, y_prev, cust.x, cust.y)
            route.arrival_times[cid] = t
            x_prev, y_prev = cust.x, cust.y

        if route.customers:
            route.last_customer_time = route.arrival_times[route.customers[-1]]
        else:
            route.last_customer_time = 0.0

        route.cost = t + euclidean(x_prev, y_prev, instance.depot.x, instance.depot.y)

    if seen != all_customers:
        missing = sorted(all_customers - seen)
        if missing:
            raise ValueError(
                f"Solution does not cover all instance customers. Missing count={len(missing)}"
            )

    return routes


def compute_safe_reveal_horizon(routes: list[Route]) -> float:
    if not routes:
        raise ValueError("No routes available to compute reveal horizon")
    horizon = min(route.last_customer_time for route in routes)
    if horizon <= 0:
        raise ValueError("Computed non-positive safe reveal horizon")
    return horizon


def compute_customer_service_phases(
    routes: list[Route], safe_horizon: float
) -> dict[int, float]:
    if safe_horizon <= 0:
        raise ValueError("safe_horizon must be > 0")

    phases: dict[int, float] = {}
    for route in routes:
        if route.arrival_times is None:
            raise ValueError("Routes missing arrival_times; run compute_route_timelines first")
        for cid, t in route.arrival_times.items():
            phase = max(0.0, min(1.0, t / safe_horizon))
            phases[cid] = phase

    return phases


def compute_demand_slack(instance: InstanceData, difficulty: str) -> tuple[int, int, int]:
    alpha_map = {"easy": 0.4, "medium": 0.6, "hard": 0.8}
    if difficulty not in alpha_map:
        raise ValueError(f"Invalid difficulty: {difficulty}")

    total_capacity = instance.vehicle_count * instance.capacity
    original_demand = sum(c.demand for c in instance.customers.values())
    slack = total_capacity - original_demand

    if slack <= 0:
        raise ValueError(
            f"Non-positive demand slack ({slack}). Cannot generate feasible added demand budget."
        )

    added_demand_budget = math.floor(alpha_map[difficulty] * slack)
    return total_capacity, slack, added_demand_budget


def compute_route_excess_capacity(instance: InstanceData, routes: list[Route]) -> tuple[dict[int, int], int]:
    route_excess: dict[int, int] = {}
    for route in routes:
        load = sum(instance.customers[cid].demand for cid in route.customers)
        excess = instance.capacity - load
        if excess < 0:
            raise ValueError(
                f"Reference route {route.vehicle_id} is over capacity by {-excess} units"
            )
        route_excess[route.vehicle_id] = excess

    if not route_excess:
        raise ValueError("No routes available to compute route excess capacity")

    max_excess = max(route_excess.values())
    return route_excess, max_excess


def generate_new_customers(
    instance: InstanceData,
    phases: dict[int, float],
    added_demand_budget: int,
    rng: random.Random,
    jitter_std: float,
    demand_perturb: int,
    per_customer_demand_cap: int,
) -> list[ScenarioCustomer]:
    if added_demand_budget <= 0:
        return []
    if jitter_std < 0:
        raise ValueError("jitter_std must be non-negative")
    if demand_perturb < 0:
        raise ValueError("demand_perturb must be non-negative")
    if per_customer_demand_cap < 1:
        raise ValueError(
            "Cannot generate feasible new customers: maximum route excess capacity is < 1"
        )

    parent_ids = sorted(instance.customers.keys())
    next_new_id = max(parent_ids) + 1
    current_added_demand = 0
    new_customers: list[ScenarioCustomer] = []

    failed_attempts = 0
    max_attempts = max(100, added_demand_budget * 20)

    while current_added_demand < added_demand_budget and failed_attempts < max_attempts:
        parent_id = rng.choice(parent_ids)
        parent = instance.customers[parent_id]
        inherited_phase = phases.get(parent_id)
        if inherited_phase is None:
            raise ValueError(f"Missing inherited phase for parent customer {parent_id}")

        demand_delta = rng.randint(-demand_perturb, demand_perturb)
        d_new = max(1, parent.demand + demand_delta)
        d_new = min(d_new, per_customer_demand_cap)
        remaining = added_demand_budget - current_added_demand

        if d_new > remaining:
            failed_attempts += 1
            if remaining <= 1:
                break
            continue

        x_new = parent.x + rng.gauss(0.0, jitter_std)
        y_new = parent.y + rng.gauss(0.0, jitter_std)

        new_customers.append(
            ScenarioCustomer(
                id=next_new_id,
                parent_id=parent_id,
                x=x_new,
                y=y_new,
                demand=d_new,
                inherited_phase=max(0.0, min(1.0, inherited_phase)),
                batch_id=0,
                reveal_time=0.0,
            )
        )
        current_added_demand += d_new
        next_new_id += 1
        failed_attempts = 0

    return new_customers


def assign_batches(
    new_customers: list[ScenarioCustomer],
    num_batches: int,
    safe_horizon: float,
) -> tuple[list[ScenarioCustomer], dict[int, float]]:
    if num_batches <= 0:
        raise ValueError("num_batches must be >= 1")
    if safe_horizon <= 0:
        raise ValueError("safe_horizon must be > 0")
    if not new_customers:
        return [], {}

    sorted_customers = sorted(new_customers, key=lambda c: c.inherited_phase)
    actual_batches = min(num_batches, len(sorted_customers))

    if actual_batches == 3 and num_batches == 3:
        batch_times = {
            1: 0.25 * safe_horizon,
            2: 0.50 * safe_horizon,
            3: 0.75 * safe_horizon,
        }
    else:
        batch_times = {
            b: (b / (actual_batches + 1)) * safe_horizon for b in range(1, actual_batches + 1)
        }

    n = len(sorted_customers)
    base_size, rem = divmod(n, actual_batches)

    start = 0
    for batch_id in range(1, actual_batches + 1):
        size = base_size + (1 if batch_id <= rem else 0)
        end = start + size
        for c in sorted_customers[start:end]:
            c.batch_id = batch_id
            c.reveal_time = batch_times[batch_id]
        start = end

    return sorted_customers, batch_times


def build_augmented_instance(
    instance: InstanceData,
    new_customers: list[ScenarioCustomer],
) -> InstanceData:
    customers = {cid: Customer(c.id, c.x, c.y, c.demand) for cid, c in instance.customers.items()}

    for sc in new_customers:
        if sc.id in customers:
            raise ValueError(f"New customer id collides with existing id: {sc.id}")
        customers[sc.id] = Customer(id=sc.id, x=sc.x, y=sc.y, demand=sc.demand)

    return InstanceData(
        name=f"{instance.name}_augmented",
        depot=Customer(
            id=instance.depot.id,
            x=instance.depot.x,
            y=instance.depot.y,
            demand=instance.depot.demand,
        ),
        customers=customers,
        vehicle_count=instance.vehicle_count,
        capacity=instance.capacity,
    )


def _write_vrp_file(instance: InstanceData, path: Path) -> None:
    node_ids = [instance.depot.id] + sorted(instance.customers.keys())
    dimension = len(node_ids)

    with path.open("w", encoding="utf-8") as f:
        f.write(f"NAME : {instance.name}\n")
        f.write("TYPE : CVRP\n")
        f.write(f"DIMENSION : {dimension}\n")
        f.write("EDGE_WEIGHT_TYPE : EUC_2D\n")
        f.write(f"CAPACITY : {instance.capacity}\n")
        f.write("NODE_COORD_SECTION\n")

        for nid in node_ids:
            if nid == instance.depot.id:
                c = instance.depot
            else:
                c = instance.customers[nid]
            f.write(f"{nid} {c.x:.6f} {c.y:.6f}\n")

        f.write("DEMAND_SECTION\n")
        f.write(f"{instance.depot.id} {instance.depot.demand}\n")
        for nid in sorted(instance.customers.keys()):
            c = instance.customers[nid]
            f.write(f"{nid} {c.demand}\n")

        f.write("DEPOT_SECTION\n")
        f.write(f"{instance.depot.id}\n")
        f.write("-1\n")
        f.write("EOF\n")


def _parse_hgs_cost(output: str) -> Optional[float]:
    patterns = [
        r"\bCost\b\s*[:=]?\s*([-+]?\d*\.?\d+)",
        r"\bObjective\b\s*[:=]?\s*([-+]?\d*\.?\d+)",
        r"\bBest\b[^\d\-+]*([-+]?\d*\.?\d+)",
        r"\bFinal\b[^\d\-+]*([-+]?\d*\.?\d+)",
    ]
    for pattern in patterns:
        matches = re.findall(pattern, output, flags=re.IGNORECASE)
        if matches:
            return float(matches[-1])
    return None


def validate_augmented_instance(
    augmented_instance: InstanceData,
    hgs_executable: str,
    temp_dir: str,
) -> float | None:
    exe = Path(hgs_executable)
    if not exe.exists():
        raise FileNotFoundError(f"HGS executable not found: {hgs_executable}")

    temp_path = Path(temp_dir)
    temp_path.mkdir(parents=True, exist_ok=True)
    vrp_path = temp_path / f"{augmented_instance.name}.vrp"
    _write_vrp_file(augmented_instance, vrp_path)

    cmd = [str(exe), str(vrp_path)]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    merged_output = (proc.stdout or "") + "\n" + (proc.stderr or "")

    if proc.returncode != 0:
        raise RuntimeError(
            "HGS validation failed with non-zero exit code "
            f"{proc.returncode}. Output:\n{merged_output[:2000]}"
        )

    cost = _parse_hgs_cost(merged_output)
    if cost is None:
        raise RuntimeError(
            "HGS validation completed, but no objective cost could be parsed. "
            "Please check executable output format."
        )
    return cost


def build_scenario_payload(
    metadata: ScenarioMetadata,
    new_customers: list[ScenarioCustomer],
) -> dict:
    return {
        "base_instance": metadata.base_instance,
        "seed": metadata.seed,
        "vehicle_count": metadata.vehicle_count,
        "capacity": metadata.capacity,
        "original_cost": metadata.original_cost,
        "augmented_static_cost": metadata.augmented_static_cost,
        "safe_reveal_horizon": metadata.safe_reveal_horizon,
        "added_demand_budget": metadata.added_demand_budget,
        "total_added_demand": metadata.total_added_demand,
        "batch_times": {str(k): v for k, v in metadata.batch_times.items()},
        "new_customers": [asdict(c) for c in new_customers],
    }


def save_scenario(payload: dict, output_path: str) -> None:
    path = Path(output_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Generate dynamic benchmark scenarios from Uchoa X-set CVRP instances."
    )
    parser.add_argument("--instance_path", required=True)
    parser.add_argument("--solution_path", required=True)
    parser.add_argument("--output_path", required=True)
    parser.add_argument("--seed", type=int, required=True)
    parser.add_argument("--difficulty", choices=["easy", "medium", "hard"], required=True)
    parser.add_argument("--num_batches", type=int, default=3)
    parser.add_argument("--jitter_std", type=float, default=5.0)
    parser.add_argument("--demand_perturb", type=int, default=1)
    parser.add_argument("--validate_hgs", action="store_true")
    parser.add_argument("--hgs_executable", default="")
    args = parser.parse_args()

    rng = random.Random(args.seed)

    instance = load_uchoa_instance(args.instance_path)
    routes, declared_solution_cost = load_reference_solution(args.solution_path)
    routes = compute_route_timelines(instance, routes)

    safe_horizon = compute_safe_reveal_horizon(routes)
    phases = compute_customer_service_phases(routes, safe_horizon)

    total_capacity, slack, added_budget = compute_demand_slack(instance, args.difficulty)
    _, max_route_excess = compute_route_excess_capacity(instance, routes)
    new_customers = generate_new_customers(
        instance=instance,
        phases=phases,
        added_demand_budget=added_budget,
        rng=rng,
        jitter_std=args.jitter_std,
        demand_perturb=args.demand_perturb,
        per_customer_demand_cap=max_route_excess,
    )

    if added_budget > 0 and not new_customers:
        raise RuntimeError("Failed to generate any new customers under the demand budget")

    new_customers, batch_times = assign_batches(
        new_customers=new_customers,
        num_batches=args.num_batches,
        safe_horizon=safe_horizon,
    )

    original_cost = (
        declared_solution_cost
        if declared_solution_cost is not None
        else sum(route.cost for route in routes)
    )

    total_added_demand = sum(c.demand for c in new_customers)
    original_max_id = max(instance.customers.keys())
    new_ids = [c.id for c in new_customers]

    if len(new_ids) != len(set(new_ids)):
        raise ValueError("Generated duplicate new customer IDs")
    if any(cid <= original_max_id for cid in new_ids):
        raise ValueError("Generated IDs must be strictly greater than original max ID")
    if total_added_demand > added_budget:
        raise ValueError("Total added demand exceeds added demand budget")
    if any(c.reveal_time > safe_horizon for c in new_customers):
        raise ValueError("Found reveal time greater than safe reveal horizon")
    if any(not (0.0 <= c.inherited_phase <= 1.0) for c in new_customers):
        raise ValueError("Found inherited phase outside [0, 1]")

    augmented_static_cost: float | None = None
    if args.validate_hgs:
        if not args.hgs_executable:
            raise ValueError("--hgs_executable is required when --validate_hgs is set")
        augmented = build_augmented_instance(instance, new_customers)
        with tempfile.TemporaryDirectory() as tmpdir:
            augmented_static_cost = validate_augmented_instance(
                augmented_instance=augmented,
                hgs_executable=args.hgs_executable,
                temp_dir=tmpdir,
            )

    metadata = ScenarioMetadata(
        base_instance=instance.name,
        seed=args.seed,
        vehicle_count=instance.vehicle_count,
        capacity=instance.capacity,
        original_cost=float(original_cost),
        augmented_static_cost=augmented_static_cost,
        safe_reveal_horizon=safe_horizon,
        added_demand_budget=added_budget,
        total_added_demand=total_added_demand,
        batch_times=batch_times,
    )

    payload = build_scenario_payload(metadata=metadata, new_customers=new_customers)
    save_scenario(payload=payload, output_path=args.output_path)

    original_customer_count = len(instance.customers)
    generated_customer_count = len(new_customers)
    original_demand = sum(c.demand for c in instance.customers.values())
    batch_sizes: dict[int, int] = {}
    for c in new_customers:
        batch_sizes[c.batch_id] = batch_sizes.get(c.batch_id, 0) + 1

    print(f"base_instance: {instance.name}")
    print(f"original_customers: {original_customer_count}")
    print(f"generated_customers: {generated_customer_count}")
    print(f"original_demand: {original_demand}")
    print(f"total_added_demand: {total_added_demand}")
    print(f"safe_reveal_horizon: {safe_horizon:.6f}")
    print(f"total_capacity: {total_capacity}")
    print(f"slack: {slack}")
    print(f"added_demand_budget: {added_budget}")
    print(f"max_route_excess_capacity: {max_route_excess}")
    print(f"batch_sizes: {batch_sizes}")
    print(f"output_path: {args.output_path}")


if __name__ == "__main__":
    main()
