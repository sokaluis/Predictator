using System.Globalization;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.CsvModels;

namespace Oloraculo.Web.Services.Backtest;

public sealed class RollingBacktestReportService
{
    private readonly RollingBacktestService _backtest;

    public RollingBacktestReportService(RollingBacktestService? backtest = null)
    {
        _backtest = backtest ?? new RollingBacktestService();
    }

    public BacktestReport GenerateFromCsv(string historicalResultsCsvPath, BacktestReportOptions? options = null)
    {
        options ??= new BacktestReportOptions();
        var rows = CsvParsingHelper.ReadCsv<HistoricalResultCsvRow>(historicalResultsCsvPath);
        var loadResult = LoadScoredResults(rows, options);
        var comparison = _backtest.Compare(
            loadResult.Results,
            minimumPriorMatchesPerTeam: options.MinimumPriorMatchesPerTeam,
            goalModelYearsWindow: options.GoalModelYearsWindow,
            targetFilter: result => MatchesEvaluationTarget(result, options));

        return new BacktestReport(loadResult, comparison.Summaries)
        {
            Options = options
        };
    }

    public static BacktestReportLoadResult LoadScoredResults(
        IEnumerable<HistoricalResultCsvRow> rows,
        BacktestReportOptions? options = null)
    {
        options ??= new BacktestReportOptions();
        var totalRows = 0;
        var excludedUnscoredOrInvalid = 0;
        var duplicateRows = 0;
        var filteredOutByOptions = 0;
        var importedIds = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<MatchResult>();

        foreach (var row in rows)
        {
            totalRows++;
            if (!TryParseScoredResult(row, out var result))
            {
                excludedUnscoredOrInvalid++;
                continue;
            }

            if (!MatchesOptions(result, options))
            {
                filteredOutByOptions++;
                continue;
            }

            if (!importedIds.Add(result.Id))
            {
                duplicateRows++;
                continue;
            }

            results.Add(result);
        }

        var ordered = RollingBacktestService.OrderByDate(results);
        if (options.Take is > 0 && ordered.Count > options.Take)
        {
            filteredOutByOptions += ordered.Count - options.Take.Value;
            ordered = ordered.Take(options.Take.Value).ToList();
        }

        return new BacktestReportLoadResult(
            totalRows,
            ordered.Count,
            excludedUnscoredOrInvalid,
            duplicateRows,
            filteredOutByOptions,
            ordered);
    }

    public static string Render(BacktestReport report)
    {
        var lines = new List<string>
        {
            "Rolling-origin backtest report",
            $"Data source: {OloraculoDataFiles.HistoricalResultsCsv}",
            $"Included scored matches: {report.LoadResult.IncludedRows.ToString(CultureInfo.InvariantCulture)}",
            $"Excluded unplayed/invalid rows: {report.LoadResult.ExcludedUnscoredOrInvalid.ToString(CultureInfo.InvariantCulture)}",
            $"Excluded duplicate rows: {report.LoadResult.DuplicateRows.ToString(CultureInfo.InvariantCulture)}",
            $"Excluded by report options: {report.LoadResult.FilteredOutByOptions.ToString(CultureInfo.InvariantCulture)}",
            "Models: Modelo base; Modelo de goles (Poisson)",
            "Limitations: Elo, FIFA ranking, and RecentForm are intentionally excluded until historical as-of snapshots exist.",
            "",
            "| Model | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |",
            "| --- | ---: | ---: | ---: | ---: | ---: |"
        };

        if (report.Options.EvaluateFrom is not null || report.Options.EvaluateTo is not null)
        {
            var from = report.Options.EvaluateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "start";
            var to = report.Options.EvaluateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "end";
            lines.Insert(6, $"Evaluation window: {from} to {to}");
        }

        lines.AddRange(report.Summaries.Select(summary => string.Create(
            CultureInfo.InvariantCulture,
            $"| {summary.ModelName} | {summary.Count} | {summary.MeanBrier:0.0000} | {summary.MeanLogLoss:0.0000} | {summary.MeanRps:0.0000} | {summary.TopPickAccuracy:P1} |")));

        return string.Join(Environment.NewLine, lines);
    }

    public static BacktestReportOptions ParseOptions(IEnumerable<string> args)
    {
        var options = new BacktestReportOptions();

        foreach (var arg in args)
        {
            if (TryReadValue(arg, "--from", out var from) && TryParseDate(from, out var fromDate))
                options = options with { From = fromDate };
            else if (TryReadValue(arg, "--to", out var to) && TryParseDate(to, out var toDate))
                options = options with { To = toDate };
            else if (TryReadValue(arg, "--evaluate-from", out var evaluateFrom) && TryParseDate(evaluateFrom, out var evaluateFromDate))
                options = options with { EvaluateFrom = evaluateFromDate };
            else if (TryReadValue(arg, "--evaluate-to", out var evaluateTo) && TryParseDate(evaluateTo, out var evaluateToDate))
                options = options with { EvaluateTo = evaluateToDate };
            else if (TryReadValue(arg, "--tournament", out var tournament) && !string.IsNullOrWhiteSpace(tournament))
                options = options with { Tournament = tournament };
            else if (TryReadValue(arg, "--take", out var take) && int.TryParse(take, NumberStyles.Integer, CultureInfo.InvariantCulture, out var takeValue))
                options = options with { Take = takeValue };
            else if (TryReadValue(arg, "--min-prior-matches", out var minimum) && int.TryParse(minimum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimumValue))
                options = options with { MinimumPriorMatchesPerTeam = minimumValue };
            else if (TryReadValue(arg, "--goal-window-years", out var window) && int.TryParse(window, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowValue))
                options = options with { GoalModelYearsWindow = windowValue };
        }

        return options;
    }

    public static string ResolveDefaultHistoricalResultsPath()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Data", OloraculoDataFiles.HistoricalResultsCsv);
        if (File.Exists(outputPath))
            return outputPath;

        return Path.Combine(Directory.GetCurrentDirectory(), "Data", OloraculoDataFiles.HistoricalResultsCsv);
    }

    private static bool TryParseScoredResult(HistoricalResultCsvRow row, out MatchResult result)
    {
        result = default!;
        if (!DateTimeOffset.TryParse(row.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ||
            !int.TryParse(row.HomeScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var homeScore) ||
            !int.TryParse(row.AwayScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var awayScore) ||
            homeScore < 0 ||
            awayScore < 0)
        {
            return false;
        }

        var homeId = TeamNameNormalizer.ToId(row.HomeTeam);
        var awayId = TeamNameNormalizer.ToId(row.AwayTeam);
        var resultId = CryptoUtil.GetSha256($"{homeId}-{awayId}-{date:O}-{row.Tournament}-{homeScore}-{awayScore}");

        result = new MatchResult
        {
            Id = resultId,
            HomeTeamId = homeId,
            AwayTeamId = awayId,
            HomeGoals = homeScore,
            AwayGoals = awayScore,
            Date = date,
            Tournament = row.Tournament,
            Neutral = bool.TryParse(row.Neutral, out var neutral) && neutral,
            Source = OloraculoDataFiles.HistoricalResultsCsv
        };
        return true;
    }

    private static bool MatchesOptions(MatchResult result, BacktestReportOptions options)
    {
        if (options.From is not null && result.Date < options.From.Value)
            return false;

        if (options.To is not null && result.Date > options.To.Value)
            return false;

        return string.IsNullOrWhiteSpace(options.Tournament) ||
            string.Equals(result.Tournament, options.Tournament, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesEvaluationTarget(MatchResult result, BacktestReportOptions options)
    {
        if (options.EvaluateFrom is not null && result.Date < options.EvaluateFrom.Value)
            return false;

        if (options.EvaluateTo is not null && result.Date > options.EvaluateTo.Value)
            return false;

        return true;
    }

    private static bool TryReadValue(string arg, string name, out string value)
    {
        value = "";
        var prefix = name + "=";
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        value = arg[prefix.Length..];
        return true;
    }

    private static bool TryParseDate(string value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
}

public sealed record BacktestReportOptions(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    DateTimeOffset? EvaluateFrom = null,
    DateTimeOffset? EvaluateTo = null,
    string? Tournament = null,
    int? Take = null,
    int MinimumPriorMatchesPerTeam = 1,
    int GoalModelYearsWindow = 8);

public sealed record BacktestReportLoadResult(
    int TotalRows,
    int IncludedRows,
    int ExcludedUnscoredOrInvalid,
    int DuplicateRows,
    int FilteredOutByOptions,
    IReadOnlyList<MatchResult> Results);

public sealed record BacktestReport(
    BacktestReportLoadResult LoadResult,
    IReadOnlyList<BacktestModelSummary> Summaries)
{
    public BacktestReportOptions Options { get; init; } = new();
}
