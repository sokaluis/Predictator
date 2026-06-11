# Oloraculo

Oloraculo is a .NET 9 Blazor Server app for predicting the 2026 FIFA World Cup. It builds predictions as a small model ladder, explains which model was used, and can run a Monte Carlo simulation of the full tournament.

## What It Does

- Imports seed data from CSV files: groups, historical results, FIFA rankings, and Elo ratings.
- Builds match predictions through layered models:
  - uniform baseline
  - FIFA ranking
  - Elo
  - recent form
  - Poisson scoreline model with a Dixon-Coles-style low-score adjustment
  - goal model adjusted by recent context and player availability when available
- Selects the highest usable model as the final oracle, with notes about missing or skipped signals.
- Runs a repeatable Monte Carlo tournament simulation and stores tournament snapshots.
- Saves match predictions and evaluates them later with Brier score, RPS, log loss, and top-pick accuracy.
- Optionally refreshes rankings, API-Football fixture/context data, and availability news classified through OpenRouter.

## Tech Stack

- .NET 9
- Blazor Server with MudBlazor
- Entity Framework Core 9
- SQLite
- CsvHelper
- xUnit

## Main Screens

- `/` - overview and model ladder
- `/lab` - compare two teams across the prediction ladder
- `/matches` - group-stage fixtures, prediction snapshots, context refresh, and result entry
- `/fixture` - full fixture view
- `/tournament` - run the Monte Carlo tournament simulation
- `/tournament/snapshots` - inspect saved tournament projections
- `/performance` - prediction evaluation metrics
- `/data` - CSV import, rankings refresh, API-Football refresh, and availability refresh

## Project Structure

```text
Oloraculo.sln
Oloraculo.Web/
  Components/          Blazor pages, layout, and shared UI
  DAL/                 EF Core DbContext
  Data/                CSV seed data and video notes
  Helpers/             CSV parsing, team-name normalization, crypto helpers
  Models/              Domain, CSV, API-Football, snapshot, and evaluation models
  Predictors/          Model ladder and final selector
  Probability/         Outcome, scoreline, and tournament probability math
  Services/            Import, prediction, rankings, API, availability, snapshots, evaluation
    Simulation/        World Cup bracket and Monte Carlo engine
Oloraculo.Web.Tests/   xUnit tests
```

## Getting Started

Prerequisites:

- .NET 9 SDK

Run the app:

```bash
dotnet restore
dotnet run --project Oloraculo.Web
```

The SQLite database is created automatically on startup, and the CSV seed data is imported when needed.

## Configuration

Settings live in `Oloraculo.Web/appsettings.json` under the `Oloraculo` section.

Important keys:

- `SimulationCount` and `SimulationSeed`
- `RecentResultCount`
- `GoalModelYearsWindow`
- `RankingRefreshOnStartup`
- `FifaRankingsRawUrl`
- `EloRankingsBaseUrl`
- `ApiFootballApiKey`
- `OpenRouterApiKey`
- `AvailabilitySourceUrls`

Keep secrets such as API-Football and OpenRouter keys in `appsettings.Development.json` or user secrets.

## Testing

```bash
dotnet test
```

## Data Sources

CSV seed data lives in `Oloraculo.Web/Data`:

- `wc2026_groups.csv`
- `historical_results.csv`
- `fifa_rankings.csv`
- `elo_snapshot.csv`

