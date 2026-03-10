from __future__ import annotations

import argparse
import json
from pathlib import Path

from generate_dynamic_benchmarks import (
    compute_demand_slack,
    compute_route_excess_capacity,
    compute_route_timelines,
    load_reference_solution,
    load_uchoa_instance,
)


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Rank available Uchoa X-set instances by slack-per-vehicle and expected room "
            "for adding new customers under current benchmark rules."
        )
    )
    parser.add_argument("--instances_dir", default="Instances/CVRP")
    parser.add_argument("--solutions_dir", default="Solutions")
    parser.add_argument("--difficulty", choices=["easy", "medium", "hard"], default="medium")
    parser.add_argument("--top_k", type=int, default=10)
    parser.add_argument(
        "--output_path",
        default="Generated Benchmarks/top_x_instances_by_slack.json",
        help="Optional JSON output with full ranking details.",
    )
    args = parser.parse_args()

    root = Path.cwd()
    instances_dir = (root / args.instances_dir).resolve()
    solutions_dir = (root / args.solutions_dir).resolve()
    output_path = (root / args.output_path).resolve()

    if not instances_dir.exists():
        raise FileNotFoundError(f"Instances directory not found: {instances_dir}")
    if not solutions_dir.exists():
        raise FileNotFoundError(f"Solutions directory not found: {solutions_dir}")

    solution_files = sorted(solutions_dir.glob("X-*.sol"))
    if not solution_files:
        raise ValueError(f"No X-set solution files found in {solutions_dir}")

    rows: list[dict] = []
    skipped: list[dict] = []

    for sol_path in solution_files:
        stem = sol_path.stem
        instance_path = instances_dir / f"{stem}.vrp"
        if not instance_path.exists():
            skipped.append({"instance": stem, "reason": "missing_instance"})
            continue

        try:
            instance = load_uchoa_instance(str(instance_path))
            routes, _ = load_reference_solution(str(sol_path))
            routes = compute_route_timelines(instance, routes)
            _, slack, added_budget = compute_demand_slack(instance, args.difficulty)
            route_excess, max_route_excess = compute_route_excess_capacity(instance, routes)

            slack_per_vehicle = slack / instance.vehicle_count
            avg_route_excess = sum(route_excess.values()) / len(route_excess)

            # Upper bound for number of new customers under script rules:
            # each new customer demand is >=1 and total added demand is budget-capped.
            max_possible_new_customers = added_budget

            rows.append(
                {
                    "instance": stem,
                    "vehicle_count": instance.vehicle_count,
                    "capacity": instance.capacity,
                    "slack": slack,
                    "slack_per_vehicle": slack_per_vehicle,
                    "added_demand_budget": added_budget,
                    "max_route_excess_capacity": max_route_excess,
                    "avg_route_excess_capacity": avg_route_excess,
                    "max_possible_new_customers": max_possible_new_customers,
                }
            )
        except Exception as exc:  # Keep batch ranking robust to one bad file.
            skipped.append({"instance": stem, "reason": str(exc)})

    if not rows:
        raise RuntimeError("No rankable X-set instance/solution pairs were processed successfully")

    rows.sort(
        key=lambda r: (
            r["slack_per_vehicle"],
            r["added_demand_budget"],
            r["max_route_excess_capacity"],
        ),
        reverse=True,
    )

    top_k = min(args.top_k, len(rows))
    top_rows = rows[:top_k]

    print(f"Ranked X-set pairs: {len(rows)}")
    print(f"Skipped: {len(skipped)}")
    print(f"Top {top_k} by slack_per_vehicle (difficulty={args.difficulty}):")
    for idx, row in enumerate(top_rows, start=1):
        print(
            f"{idx:2d}. {row['instance']}: "
            f"slack_per_vehicle={row['slack_per_vehicle']:.3f}, "
            f"budget={row['added_demand_budget']}, "
            f"max_route_excess={row['max_route_excess_capacity']}"
        )

    payload = {
        "difficulty": args.difficulty,
        "top_k": top_k,
        "ranking_metric": "slack_per_vehicle",
        "rows": rows,
        "top_rows": top_rows,
        "skipped": skipped,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    print(f"Saved full ranking JSON to: {output_path}")


if __name__ == "__main__":
    main()
