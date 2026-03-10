from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Run generate_dynamic_benchmarks.py for each solution file and its matching "
            "Uchoa instance."
        )
    )
    parser.add_argument("--instances_dir", default="Instances/CVRP")
    parser.add_argument("--solutions_dir", default="Solutions")
    parser.add_argument("--output_dir", default="Generated Benchmarks")
    parser.add_argument("--generator_script", default="generate_dynamic_benchmarks.py")
    parser.add_argument("--difficulty", choices=["easy", "medium", "hard"], default="medium")
    parser.add_argument("--seed_base", type=int, default=123)
    parser.add_argument("--num_batches", type=int, default=3)
    parser.add_argument("--jitter_std", type=float, default=5.0)
    parser.add_argument("--demand_perturb", type=int, default=1)
    parser.add_argument("--validate_hgs", action="store_true")
    parser.add_argument("--hgs_executable", default="")
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Overwrite existing output JSON files. If omitted, existing outputs are skipped.",
    )
    parser.add_argument(
        "--continue_on_error",
        action="store_true",
        help="Continue processing remaining files if one run fails.",
    )
    args = parser.parse_args()

    root = Path.cwd()
    instances_dir = (root / args.instances_dir).resolve()
    solutions_dir = (root / args.solutions_dir).resolve()
    output_dir = (root / args.output_dir).resolve()
    generator_script = (root / args.generator_script).resolve()

    if not generator_script.exists():
        raise FileNotFoundError(f"Generator script not found: {generator_script}")
    if not instances_dir.exists():
        raise FileNotFoundError(f"Instances directory not found: {instances_dir}")
    if not solutions_dir.exists():
        raise FileNotFoundError(f"Solutions directory not found: {solutions_dir}")
    if args.validate_hgs and not args.hgs_executable:
        raise ValueError("--hgs_executable is required when --validate_hgs is set")

    output_dir.mkdir(parents=True, exist_ok=True)

    solution_files = sorted(solutions_dir.glob("*.sol"))
    if not solution_files:
        raise ValueError(f"No .sol files found in {solutions_dir}")

    success = 0
    skipped = 0
    failed = 0

    for idx, solution_path in enumerate(solution_files):
        stem = solution_path.stem
        instance_path = instances_dir / f"{stem}.vrp"
        output_path = output_dir / f"{stem}.json"

        if not instance_path.exists():
            print(f"[MISSING] instance not found for solution {solution_path.name}: {instance_path}")
            failed += 1
            if not args.continue_on_error:
                raise FileNotFoundError(f"Missing instance for {solution_path.name}")
            continue

        if output_path.exists() and not args.overwrite:
            print(f"[SKIP] {output_path.name} already exists")
            skipped += 1
            continue

        seed = args.seed_base + idx

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
            str(seed),
            "--difficulty",
            args.difficulty,
            "--num_batches",
            str(args.num_batches),
            "--jitter_std",
            str(args.jitter_std),
            "--demand_perturb",
            str(args.demand_perturb),
        ]

        if args.validate_hgs:
            cmd.extend(["--validate_hgs", "--hgs_executable", args.hgs_executable])

        print(f"[RUN] {stem} (seed={seed})")
        proc = subprocess.run(cmd, capture_output=True, text=True)

        if proc.returncode == 0:
            success += 1
            print(f"[OK] {stem} -> {output_path}")
            continue

        failed += 1
        print(f"[FAIL] {stem}")
        if proc.stdout.strip():
            print("stdout:")
            print(proc.stdout.strip())
        if proc.stderr.strip():
            print("stderr:")
            print(proc.stderr.strip())

        if not args.continue_on_error:
            raise RuntimeError(f"Generation failed for {stem}")

    total = len(solution_files)
    print("\nBatch generation summary")
    print(f"total_solutions: {total}")
    print(f"success: {success}")
    print(f"skipped: {skipped}")
    print(f"failed: {failed}")
    print(f"output_dir: {output_dir}")

    if failed > 0:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
