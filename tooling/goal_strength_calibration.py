#!/usr/bin/env python3
"""Summarize GoalModel attack and defense vulnerability guardrails."""

from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


DEFAULT_WINDOWS = ("3", "5", "8", "12", "all")
DEFAULT_MIN_MULTIPLIER = 0.25
DEFAULT_MAX_MULTIPLIER = 3.5
DEFAULT_AVERAGE_GOALS = 1.25
PRIOR_MATCHES = 2.0
ITERATIONS = 8
RECENCY_DECAY = 0.75


@dataclass(frozen=True)
class Result:
    date: datetime
    home: str
    away: str
    home_goals: int
    away_goals: int


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Fit GoalModel-style strengths and print clamp calibration percentiles."
    )
    parser.add_argument(
        "--csv",
        type=Path,
        default=repo_root() / "Oloraculo.Web" / "Data" / "historical_results.csv",
        help="Path to historical_results.csv.",
    )
    parser.add_argument(
        "--windows",
        nargs="+",
        default=list(DEFAULT_WINDOWS),
        help="Year windows to run, e.g. 3 5 8 12 all or 3,5,8,12,all.",
    )
    parser.add_argument("--min", type=float, default=DEFAULT_MIN_MULTIPLIER, help="Minimum strength multiplier.")
    parser.add_argument("--max", type=float, default=DEFAULT_MAX_MULTIPLIER, help="Maximum strength multiplier.")
    args = parser.parse_args()

    if args.min <= 0 or args.max <= args.min:
        parser.error("--min must be positive and --max must be greater than --min")

    results = load_results(args.csv)
    if not results:
        parser.error(f"No usable results found in {args.csv}")

    print(f"csv={args.csv}")
    print(f"bounds={args.min:.3f}..{args.max:.3f}")
    try:
        windows = parse_windows(args.windows)
    except ValueError as ex:
        parser.error(str(ex))

    for window in windows:
        summary = fit(results, window, args.min, args.max)
        label = "all" if window is None else str(window)
        print(f"\nyears={label} matches={summary.matches_used} teams={summary.team_count}")
        print_stats("attack", summary.attacks)
        print_stats("vuln  ", summary.vulnerabilities)

    return 0


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def parse_windows(values: list[str]) -> list[int | None]:
    windows: list[int | None] = []
    for value in values:
        for part in value.split(","):
            normalized = part.strip().lower()
            if not normalized:
                continue
            if normalized == "all":
                windows.append(None)
            else:
                parsed = int(normalized)
                if parsed < 0:
                    raise ValueError("windows must be non-negative")
                windows.append(parsed)
    return windows


def load_results(path: Path) -> list[Result]:
    rows: list[Result] = []
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            try:
                rows.append(
                    Result(
                        date=parse_date(row["date"]),
                        home=team_key(row["home_team"]),
                        away=team_key(row["away_team"]),
                        home_goals=int(row["home_score"]),
                        away_goals=int(row["away_score"]),
                    )
                )
            except (KeyError, TypeError, ValueError):
                continue
    return rows


def parse_date(value: str) -> datetime:
    return datetime.fromisoformat(value).replace(tzinfo=timezone.utc)


def team_key(value: str) -> str:
    return " ".join(value.strip().casefold().split())


@dataclass(frozen=True)
class FitSummary:
    attacks: list[float]
    vulnerabilities: list[float]
    matches_used: int
    team_count: int


def fit(results: list[Result], years_window: int | None, min_multiplier: float, max_multiplier: float) -> FitSummary:
    latest = max(result.date for result in results)
    if years_window is None or years_window == 0:
        window = list(results)
    else:
        cutoff = add_years(latest, -years_window)
        window = [result for result in results if result.date >= cutoff] or list(results)

    teams = sorted({result.home for result in window} | {result.away for result in window})
    attacks = {team: 1.0 for team in teams}
    vulnerabilities = {team: 1.0 for team in teams}
    matches = {team: 0 for team in teams}

    for result in window:
        matches[result.home] += 1
        matches[result.away] += 1

    weighted = [(result, RECENCY_DECAY ** max(0.0, (latest - result.date).days / 365.25)) for result in window]
    total_weight = sum(weight for _, weight in weighted)
    avg = (
        DEFAULT_AVERAGE_GOALS
        if total_weight <= 0
        else sum(weight * (result.home_goals + result.away_goals) for result, weight in weighted) / (2.0 * total_weight)
    )
    avg = clamp(avg, 0.6, 2.4)

    for _ in range(ITERATIONS):
        next_attacks: dict[str, float] = {}
        next_vulnerabilities: dict[str, float] = {}

        for team in teams:
            goals_for = 0.0
            attack_expected = 0.0
            goals_against = 0.0
            defense_expected = 0.0
            team_weight = 0.0

            for result, weight in weighted:
                if result.home == team:
                    goals_for += weight * result.home_goals
                    attack_expected += weight * avg * vulnerabilities[result.away]
                    goals_against += weight * result.away_goals
                    defense_expected += weight * avg * attacks[result.away]
                    team_weight += weight
                elif result.away == team:
                    goals_for += weight * result.away_goals
                    attack_expected += weight * avg * vulnerabilities[result.home]
                    goals_against += weight * result.home_goals
                    defense_expected += weight * avg * attacks[result.home]
                    team_weight += weight

            raw_attack = 1.0 if attack_expected <= 0 else goals_for / attack_expected
            raw_vulnerability = 1.0 if defense_expected <= 0 else goals_against / defense_expected
            next_attacks[team] = shrink_to_neutral(raw_attack, team_weight, min_multiplier, max_multiplier)
            next_vulnerabilities[team] = shrink_to_neutral(raw_vulnerability, team_weight, min_multiplier, max_multiplier)

        normalize_mean(next_attacks)
        normalize_mean(next_vulnerabilities)
        attacks = next_attacks
        vulnerabilities = next_vulnerabilities

    return FitSummary(
        attacks=[clamp(attacks[team], min_multiplier, max_multiplier) for team in teams],
        vulnerabilities=[clamp(vulnerabilities[team], min_multiplier, max_multiplier) for team in teams],
        matches_used=len(window),
        team_count=len(teams),
    )


def shrink_to_neutral(value: float, weight: float, min_multiplier: float, max_multiplier: float) -> float:
    return clamp(((value * weight) + PRIOR_MATCHES) / (weight + PRIOR_MATCHES), min_multiplier, max_multiplier)


def add_years(value: datetime, years: int) -> datetime:
    try:
        return value.replace(year=value.year + years)
    except ValueError:
        return value.replace(year=value.year + years, day=28)


def normalize_mean(values: dict[str, float]) -> None:
    mean = sum(values.values()) / len(values) if values else 1.0
    if mean <= 0:
        return
    for key in list(values):
        values[key] /= mean


def clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(maximum, value))


def print_stats(label: str, values: list[float]) -> None:
    ordered = sorted(values)
    stats = {
        "min": ordered[0],
        "p01": percentile(ordered, 0.01),
        "p05": percentile(ordered, 0.05),
        "p95": percentile(ordered, 0.95),
        "p99": percentile(ordered, 0.99),
        "max": ordered[-1],
    }
    rendered = " ".join(f"{name}={value:.3f}" for name, value in stats.items())
    print(f"{label} {rendered}")


def percentile(ordered: list[float], probability: float) -> float:
    index = round((len(ordered) - 1) * probability)
    index = max(0, min(len(ordered) - 1, index))
    return ordered[index]


if __name__ == "__main__":
    raise SystemExit(main())
