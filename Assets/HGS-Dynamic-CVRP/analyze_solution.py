#!/usr/bin/env python3
import argparse
import math
import re
from pathlib import Path


def parse_vrp(vrp_path: Path):
    lines = vrp_path.read_text().splitlines()

    dimension = None
    capacity = None
    node_coords = {}
    demands = {}
    depot_ids = []

    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if not line:
            i += 1
            continue

        if line.startswith("DIMENSION"):
            m = re.search(r":\s*(\d+)", line)
            if m:
                dimension = int(m.group(1))
        elif line.startswith("CAPACITY"):
            m = re.search(r":\s*([0-9]+(?:\.[0-9]+)?)", line)
            if m:
                capacity = float(m.group(1))
        elif line == "NODE_COORD_SECTION":
            i += 1
            while i < len(lines):
                s = lines[i].strip()
                if s == "DEMAND_SECTION":
                    break
                parts = s.split()
                if len(parts) >= 3:
                    node_id = int(parts[0])
                    x = float(parts[1])
                    y = float(parts[2])
                    node_coords[node_id] = (x, y)
                i += 1
            continue
        elif line == "DEMAND_SECTION":
            i += 1
            while i < len(lines):
                s = lines[i].strip()
                if s == "DEPOT_SECTION":
                    break
                parts = s.split()
                if len(parts) >= 2:
                    node_id = int(parts[0])
                    dem = float(parts[1])
                    demands[node_id] = dem
                i += 1
            continue
        elif line == "DEPOT_SECTION":
            i += 1
            while i < len(lines):
                s = lines[i].strip()
                if s == "EOF":
                    break
                depot_val = int(s)
                if depot_val == -1:
                    break
                depot_ids.append(depot_val)
                i += 1
            continue

        i += 1

    if not depot_ids:
        raise ValueError("DEPOT_SECTION missing or malformed")
    depot_id = depot_ids[0]

    if dimension is not None and len(node_coords) != dimension:
        raise ValueError(f"DIMENSION={dimension} but parsed {len(node_coords)} coordinates")

    # Solution files use customer indexing 1..n (excluding depot). Build that mapping.
    customer_node_ids = [nid for nid in sorted(node_coords.keys()) if nid != depot_id]

    return {
        "capacity": capacity,
        "coords": node_coords,
        "demands": demands,
        "depot_id": depot_id,
        "customer_node_ids": customer_node_ids,
    }


def parse_solution(sol_path: Path):
    routes = []
    announced_cost = None

    for raw in sol_path.read_text().splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("Route"):
            parts = line.split(":", 1)
            if len(parts) != 2:
                raise ValueError(f"Malformed route line: {line}")
            seq = parts[1].strip()
            route = [int(x) for x in seq.split()] if seq else []
            routes.append(route)
        elif line.startswith("Cost"):
            tokens = line.split()
            if len(tokens) >= 2:
                announced_cost = float(tokens[1])

    return routes, announced_cost


def euclidean(a, b, do_round: bool):
    d = math.hypot(a[0] - b[0], a[1] - b[1])
    return float(round(d)) if do_round else d


def map_solution_customer_to_node(customer_id: int, customer_node_ids):
    if customer_id < 1 or customer_id > len(customer_node_ids):
        raise ValueError(
            f"Solution customer id {customer_id} out of range [1, {len(customer_node_ids)}]"
        )
    return customer_node_ids[customer_id - 1]


def compute_cost_and_loads(vrp, routes, do_round: bool):
    coords = vrp["coords"]
    demands = vrp["demands"]
    depot = vrp["depot_id"]
    customer_node_ids = vrp["customer_node_ids"]

    total_cost = 0.0
    route_infos = []

    for r_idx, route in enumerate(routes, start=1):
        if not route:
            route_infos.append((r_idx, 0.0, 0.0))
            continue

        node_route = [map_solution_customer_to_node(c, customer_node_ids) for c in route]

        route_cost = 0.0
        route_load = 0.0

        route_cost += euclidean(coords[depot], coords[node_route[0]], do_round)
        for i in range(len(node_route) - 1):
            route_cost += euclidean(coords[node_route[i]], coords[node_route[i + 1]], do_round)
        route_cost += euclidean(coords[node_route[-1]], coords[depot], do_round)

        for n in node_route:
            route_load += demands.get(n, 0.0)

        total_cost += route_cost
        route_infos.append((r_idx, route_cost, route_load))

    return total_cost, route_infos


def plot_routes(vrp, routes, out_png: Path):
    try:
        import matplotlib.pyplot as plt
    except ImportError as exc:
        raise RuntimeError(
            "matplotlib is required for plotting. Install with: pip install matplotlib"
        ) from exc

    coords = vrp["coords"]
    depot = vrp["depot_id"]
    customer_node_ids = vrp["customer_node_ids"]

    fig, ax = plt.subplots(figsize=(9, 7))

    # Plot all customers.
    cust_x = [coords[n][0] for n in customer_node_ids]
    cust_y = [coords[n][1] for n in customer_node_ids]
    ax.scatter(cust_x, cust_y, s=22, c="#3366cc", alpha=0.8, label="Customers")

    # Plot depot.
    ax.scatter([coords[depot][0]], [coords[depot][1]], s=140, marker="s", c="#d62728", label="Depot")

    # Draw routes.
    for r_idx, route in enumerate(routes, start=1):
        if not route:
            continue
        node_route = [map_solution_customer_to_node(c, customer_node_ids) for c in route]
        route_nodes = [depot] + node_route + [depot]
        xs = [coords[n][0] for n in route_nodes]
        ys = [coords[n][1] for n in route_nodes]
        ax.plot(xs, ys, linewidth=1.6, alpha=0.9, label=f"Route {r_idx}")

        # Annotate customer ids as they appear in solution (1..n customers).
        for sol_cust_id, node_id in zip(route, node_route):
            x, y = coords[node_id]
            ax.text(x + 0.4, y + 0.4, str(sol_cust_id), fontsize=7)

    ax.set_title("CVRP Solution Routes")
    ax.set_xlabel("X")
    ax.set_ylabel("Y")
    ax.axis("equal")
    ax.grid(alpha=0.25)
    ax.legend(loc="best", fontsize=8)

    out_png.parent.mkdir(parents=True, exist_ok=True)
    fig.tight_layout()
    fig.savefig(out_png, dpi=180)
    plt.close(fig)


def main():
    parser = argparse.ArgumentParser(
        description=(
            "Compute CVRP solution cost and draw routes. "
            "Assumes solution customers are indexed 1..n excluding the depot."
        )
    )
    parser.add_argument("instance", type=Path, help="Path to .vrp instance file")
    parser.add_argument("solution", type=Path, help="Path to solution .sol file")
    parser.add_argument(
        "--round",
        type=int,
        choices=[0, 1],
        default=1,
        help="Distance rounding mode: 1=round Euclidean (default), 0=raw Euclidean",
    )
    parser.add_argument(
        "--plot",
        type=Path,
        default=Path("solution_plot.png"),
        help="Output PNG path for route plot",
    )
    args = parser.parse_args()

    vrp = parse_vrp(args.instance)
    routes, announced_cost = parse_solution(args.solution)

    computed_cost, route_infos = compute_cost_and_loads(vrp, routes, do_round=bool(args.round))

    print(f"Instance: {args.instance}")
    print(f"Solution: {args.solution}")
    print(f"Depot node id in instance: {vrp['depot_id']}")
    print(f"Customer indexing mapping: solution customer k -> instance node id customer_node_ids[k-1]")
    print(f"Distance rounding mode: {'rounded' if args.round == 1 else 'raw'}")
    print("")
    print("Route stats:")
    for r_idx, rcost, rload in route_infos:
        if vrp["capacity"] is not None:
            print(f"  Route {r_idx:>2}: cost={rcost:.3f} load={rload:.1f} cap={vrp['capacity']:.1f}")
        else:
            print(f"  Route {r_idx:>2}: cost={rcost:.3f} load={rload:.1f}")

    print("")
    print(f"Computed total cost: {computed_cost:.6f}")
    if announced_cost is not None:
        print(f"Announced solution cost: {announced_cost:.6f}")
        print(f"Difference (computed - announced): {computed_cost - announced_cost:.6f}")

    plot_routes(vrp, routes, args.plot)
    print(f"Plot written to: {args.plot}")


if __name__ == "__main__":
    main()
