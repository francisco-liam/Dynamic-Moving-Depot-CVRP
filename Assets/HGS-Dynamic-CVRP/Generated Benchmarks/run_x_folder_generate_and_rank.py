from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from generate_dynamic_benchmarks import (
    compute_demand_slack,
    compute_route_excess_capacity,
    compute_route_timelines,
    load_reference_solution,
    load_uchoa_instance,
)


def generate_all(
    x_dir: Path,
    output_dir: Path,
    generator_script: Path,
    difficulty: str,
    seed_base: int,
    num_batches: int,
    jitter_std: float,
    demand_perturb: int,
    validate_hgs: bool,
    hgs_executable: str,
    overwrite: bool,
    continue_on_error: bool,
) -> tuple[list[str], list[dict]]:
    stems_with_both = sorted(
        {
            p.stem
            for p in x_dir.glob("X-*.sol")
            if (x_dir / f"{p.stem}.vrp").exists()
        }
    )

    if not stems_with_both:
        raise ValueError(f"No matching X-*.sol/X-*.vrp pairs found in {x_dir}")

    output_dir.mkdir(parents=True, exist_ok=True)

    successes: list[str] = []
    failures: list[dict] = []

    for idx, stem in enumerate(stems_with_both):
        instance_path = x_dir / f"{stem}.vrp"
        solution_path = x_dir / f"{stem}.sol"
        output_path = output_dir / f"{stem}.json"

        if output_path.exists() and not overwrite:
            print(f"[SKIP] {output_path.name} already exists")
            successes.append(stem)
            continue

        cmd = [
            sys.executable,
            str(generator_script),
            "--instance_path",
            str(instance_path),
            "--solution_path",
            str(solution_path),
            "--output_path",
            str(output_path),
            "--seed",
            str(seed_base + idx),
            "--difficulty",
            difficulty,
            "--num_batches",
            str(num_batches),
            "--jitter_std",
            str(jitter_std),
            "--demand_perturb",
            str(demand_perturb),
        ]
        if validate_hgs:
            cmd.extend(["--validate_hgs", "--hgs_executable", hgs_executable])

        print(f"[RUN] {stem} (seed={seed_base + idx})")
        proc = subprocess.run(cmd, capture_output=True, text=True)

        if proc.returncode == 0:
            print(f"[OK] {stem}")
            successes.append(stem)
            continue

        fail = {
            "instance": stem,
            "returncode": proc.returncode,
            "stdout": proc.stdout.strip(),
            "stderr": proc.stderr.strip(),
        }
        failures.append(fail)
        print(f"[FAIL] {stem}")
        if proc.stderr.strip():
            print(proc.stderr.strip())

        if not continue_on_error:
            raise RuntimeError(f"Generation failed for {stem}")

    print("\nGeneration summary")
    print(f"pairs_found: {len(stems_with_both)}")
    print(f"success: {len(successes)}")
    print(f"failed: {len(failures)}")

    return successes, failures


def rank_instances(x_dir: Path, stems: list[str], difficulty: str, top_k: int) -> list[dict]:
    rows: list[dict] = []

    for stem in stems:
        instance_path = x_dir / f"{stem}.vrp"
        solution_path = x_dir / f"{stem}.sol"

        instance = load_uchoa_instance(str(instance_path))
        routes, _ = load_reference_solution(str(solution_path))
        routes = compute_route_timelines(instance, routes)
        _, slack, added_budget = compute_demand_slack(instance, difficulty)
        route_excess, max_route_excess = compute_route_excess_capacity(instance, routes)

        rows.append(
            {
                "instance": stem,
                "vehicle_count": instance.vehicle_count,
                "capacity": instance.capacity,
                "slack": slack,
                "slack_per_vehicle": slack / instance.vehicle_count,
                "added_demand_budget": added_budget,
                "max_route_excess_capacity": max_route_excess,
                "avg_route_excess_capacity": sum(route_excess.values()) / len(route_excess),
                "max_possible_new_customers": added_budget,
            }
        )

    rows.sort(
        key=lambda r: (
            r["slack_per_vehicle"],
            r["added_demand_budget"],
            r["max_route_excess_capacity"],
        ),
        reverse=True,
    )

    return rows[: min(top_k, len(rows))]


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Generate dynamic benchmarks for all matching X-set pairs in one folder and "
            "rank the top instances by slack-per-vehicle."
        )
    )
    parser.add_argument("--x_dir", default="X")
    parser.add_argument("--output_dir", default="Generated Benchmarks/X")
    parser.add_argument("--generator_script", default="generate_dynamic_benchmarks.py")
    parser.add_argument("--difficulty", choices=["easy", "medium", "hard"], default="medium")
    parser.add_argument("--seed_base", type=int, default=123)
    parser.add_argument("--num_batches", type=int, default=3)
    parser.add_argument("--jitter_std", type=float, default=5.0)
    parser.add_argument("--demand_perturb", type=int, default=1)
    parser.add_argument("--validate_hgs", action="store_true")
    parser.add_argument("--hgs_executable", default="")
    parser.add_argument("--overwrite", action="store_true")
    parser.add_argument("--continue_on_error", action="store_true")
    parser.add_argument("--top_k", type=int, default=10)
    parser.add_argument(
        "--ranking_output",
        default="Generated Benchmarks/X/top_10_by_slack.json",
        help="Path to write ranking JSON",
    )
    args = parser.parse_args()

    root = Path.cwd()
    x_dir = (root / args.x_dir).resolve()
    output_dir = (root / args.output_dir).resolve()
    generator_script = (root / args.generator_script).resolve()
    ranking_output = (root / args.ranking_output).resolve()

    if not x_dir.exists():
        raise FileNotFoundError(f"x_dir not found: {x_dir}")
    if not generator_script.exists():
        raise FileNotFoundError(f"generator_script not found: {generator_script}")
    if args.validate_hgs and not args.hgs_executable:
        raise ValueError("--hgs_executable is required when --validate_hgs is set")

    successes, failures = generate_all(
        x_dir=x_dir,
        output_dir=output_dir,
        generator_script=generator_script,
        difficulty=args.difficulty,
        seed_base=args.seed_base,
        num_batches=args.num_batches,
        jitter_std=args.jitter_std,
        demand_perturb=args.demand_perturb,
        validate_hgs=args.validate_hgs,
        hgs_executable=args.hgs_executable,
        overwrite=args.overwrite,
        continue_on_error=args.continue_on_error,
    )

    if not successes:
        raise RuntimeError("No successful benchmark generations; cannot rank")

    top_rows = rank_instances(
        x_dir=x_dir,
        stems=successes,
        difficulty=args.difficulty,
        top_k=args.top_k,
    )

    print(f"\nTop {len(top_rows)} instances by slack_per_vehicle:")
    for i, row in enumerate(top_rows, start=1):
        print(
            f"{i:2d}. {row['instance']}: "
            f"slack_per_vehicle={row['slack_per_vehicle']:.3f}, "
            f"budget={row['added_demand_budget']}, "
            f"max_route_excess={row['max_route_excess_capacity']}"
        )

    payload = {
        "difficulty": args.difficulty,
        "top_k": len(top_rows),
        "ranking_metric": "slack_per_vehicle",
        "top_rows": top_rows,
        "successful_instances": successes,
        "failed_instances": failures,
    }
    ranking_output.parent.mkdir(parents=True, exist_ok=True)
    ranking_output.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    print(f"Saved ranking JSON to: {ranking_output}")

    if failures:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
