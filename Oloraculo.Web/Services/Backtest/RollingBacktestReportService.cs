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
            Options = options,
            SegmentSummaries = FilterSegmentSummaries(comparison.SegmentSummaries, options),
            Coverage = comparison.Coverage
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
        var modelNames = report.Summaries
            .Select(summary => summary.ModelName)
            .DefaultIfEmpty("Modelo base; Modelo de goles (Poisson)");
        var lines = new List<string>
        {
            "Rolling-origin backtest report",
            $"Data source: {OloraculoDataFiles.HistoricalResultsCsv}",
            $"Included scored matches: {report.LoadResult.IncludedRows.ToString(CultureInfo.InvariantCulture)}",
            $"Excluded unplayed/invalid rows: {report.LoadResult.ExcludedUnscoredOrInvalid.ToString(CultureInfo.InvariantCulture)}",
            $"Excluded duplicate rows: {report.LoadResult.DuplicateRows.ToString(CultureInfo.InvariantCulture)}",
            $"Excluded by report options: {report.LoadResult.FilteredOutByOptions.ToString(CultureInfo.InvariantCulture)}",
            $"Models: {string.Join("; ", modelNames)}",
        };

        if (report.Coverage is not null)
        {
            AddCoverageLines(lines, report.Coverage);
        }
        else
        {
            var includesRatingAwareModels = report.Summaries.Any(summary =>
                summary.ModelName is "Elo" or "Ranking FIFA" or "Forma reciente");
            lines.Add(includesRatingAwareModels
                ? "Rating snapshots: rating-aware models used historical as-of snapshots."
                : "Limitations: Elo, FIFA ranking, and RecentForm are intentionally excluded until historical as-of snapshots exist.");
        }

        lines.Add("");
        AddReadinessLines(lines, report.Summaries);

        lines.Add("");
        lines.Add("| Model | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |");
        lines.Add("| --- | ---: | ---: | ---: | ---: | ---: |");

        if (report.Options.EvaluateFrom is not null || report.Options.EvaluateTo is not null)
        {
            var from = report.Options.EvaluateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "start";
            var to = report.Options.EvaluateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "end";
            lines.Insert(6, $"Evaluation window: {from} to {to}");
        }

        if (report.Options.Segment is not null)
            lines.Insert(6, $"Segment: {report.Options.Segment}");

        lines.AddRange(report.Summaries.Select(summary => string.Create(
            CultureInfo.InvariantCulture,
            $"| {summary.ModelName} | {summary.Count} | {summary.MeanBrier:0.0000} | {summary.MeanLogLoss:0.0000} | {summary.MeanRps:0.0000} | {summary.TopPickAccuracy:P1} |")));

        AddOracleSelectorBreakdown(lines, report.Summaries);
        AddOracleSegmentBreakdown(lines, report.SegmentSummaries);

        if (report.SegmentSummaries.Count > 0)
        {
            lines.Add("");
            lines.Add("## Performance by match type");
            lines.Add("");
            lines.Add("| Segment | Model | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |");
            lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
            lines.AddRange(report.SegmentSummaries.Select(segment => string.Create(
                CultureInfo.InvariantCulture,
                $"| {segment.SegmentName} | {segment.Summary.ModelName} | {segment.Summary.Count} | {segment.Summary.MeanBrier:0.0000} | {segment.Summary.MeanLogLoss:0.0000} | {segment.Summary.MeanRps:0.0000} | {segment.Summary.TopPickAccuracy:P1} |")));

            var deltaRows = GetSegmentDeltaRows(report.SegmentSummaries);
            if (deltaRows.Count > 0)
            {
                lines.Add("");
                lines.Add("## Delta vs baseline by match type");
                lines.Add("");
                lines.Add("| Segment | Targets | ΔBrier | ΔLogLoss | ΔRPS | ΔTopPickAccuracy |");
                lines.Add("| --- | ---: | ---: | ---: | ---: | ---: |");
                lines.AddRange(deltaRows.Select(row => string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {row.SegmentName} | {row.Targets} | {row.DeltaBrier:+0.0000;-0.0000;0.0000} | {row.DeltaLogLoss:+0.0000;-0.0000;0.0000} | {row.DeltaRps:+0.0000;-0.0000;0.0000} | {row.DeltaTopPickAccuracy * 100:+0.0;-0.0;0.0} pp |")));

                var guidanceRows = GetSegmentConfidenceGuidanceRows(deltaRows);
                if (guidanceRows.Count > 0)
                {
                    lines.Add("");
                    lines.Add("## Confidence guidance by match type");
                    lines.Add("");
                    lines.Add("| Segment | Confidence guidance | Interpretation |");
                    lines.Add("| --- | --- | --- |");
                    lines.AddRange(guidanceRows.Select(row =>
                        $"| {row.SegmentName} | {row.Guidance} | {row.Interpretation} |"));
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddCoverageLines(List<string> lines, BacktestCoverageInfo coverage)
    {
        lines.Add("Rating snapshot coverage");
        lines.Add($"  Eligible targets: {coverage.EligibleTargets.ToString(CultureInfo.InvariantCulture)}");

        var eloPct = coverage.EligibleTargets > 0
            ? (coverage.EloCoveredTargets * 100.0 / coverage.EligibleTargets).ToString("F1", CultureInfo.InvariantCulture)
            : "0.0";
        var eloStatus = coverage.EloEnabled ? "enabled" : DisabledCoverageReason(coverage.EligibleTargets);
        lines.Add($"  Elo: {coverage.EloCoveredTargets}/{coverage.EligibleTargets} targets ({eloPct}%) — {eloStatus}");

        var fifaPct = coverage.EligibleTargets > 0
            ? (coverage.FifaCoveredTargets * 100.0 / coverage.EligibleTargets).ToString("F1", CultureInfo.InvariantCulture)
            : "0.0";
        var fifaStatus = coverage.FifaEnabled ? "enabled" : DisabledCoverageReason(coverage.EligibleTargets);
        lines.Add($"  FIFA: {coverage.FifaCoveredTargets}/{coverage.EligibleTargets} targets ({fifaPct}%) — {fifaStatus}");

        var recentFormStatus = coverage.RecentFormEnabled ? "enabled" : "disabled";
        lines.Add($"  RecentForm: {recentFormStatus} (requires Elo coverage)");

        if (!coverage.EloEnabled && !coverage.FifaEnabled)
            lines.Add("Limitations: Elo, FIFA ranking, and RecentForm are intentionally excluded until historical as-of snapshots exist.");
    }

    private static void AddOracleSelectorBreakdown(
        List<string> lines,
        IReadOnlyList<BacktestModelSummary> summaries)
    {
        var oracle = summaries.FirstOrDefault(summary =>
            string.Equals(summary.ModelName, "Oráculo final", StringComparison.Ordinal));

        if (oracle is null || oracle.ChosenPredictorCounts.Count == 0)
            return;

        lines.Add("");
        lines.Add("## Oráculo final — selector breakdown");
        lines.Add("Backtest note: this measures the final selector with historical match/rating context only; fixture-context signals are not replayed here, so `Goles + contexto reciente` may degrade more often than at runtime.");
        lines.Add("");
        lines.Add("| Chosen predictor | Count |");
        lines.Add("| --- | ---: |");

        foreach (var (predictor, count) in oracle.ChosenPredictorCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            lines.Add($"| {predictor} | {count} |");
        }
    }

    private static void AddOracleSegmentBreakdown(
        List<string> lines,
        IReadOnlyList<BacktestSegmentModelSummary> segmentSummaries)
    {
        var oracleSegments = segmentSummaries
            .Where(s => string.Equals(s.Summary.ModelName, "Oráculo final", StringComparison.Ordinal))
            .Where(s => s.Summary.ChosenPredictorCounts.Count > 0)
            .ToList();

        if (oracleSegments.Count == 0)
            return;

        lines.Add("");
        lines.Add("## Oráculo final — chosen predictor counts by segment");
        lines.Add("");
        lines.Add("| Segment | Chosen predictor | Count |");
        lines.Add("| --- | --- | ---: |");

        foreach (var segment in oracleSegments)
        {
            foreach (var (predictor, count) in segment.Summary.ChosenPredictorCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                lines.Add($"| {segment.SegmentName} | {predictor} | {count} |");
            }
        }
    }

    private static void AddReadinessLines(
        List<string> lines,
        IReadOnlyList<BacktestModelSummary> summaries)
    {
        var ratingDependent = summaries
            .Where(summary => summary.IsRatingDependent)
            .ToList();

        if (ratingDependent.Count == 0)
            return;

        lines.Add("## Model readiness and degraded coverage");
        lines.Add("");
        lines.Add("| Model | Evaluated | Signal-backed | Degraded | Readiness |");
        lines.Add("| --- | ---: | ---: | ---: | ---: |");

        foreach (var summary in ratingDependent)
        {
            var readiness = summary.ReadinessPct.ToString("F1", CultureInfo.InvariantCulture);
            var readinessNote = summary.ReadinessPct >= 100.0
                ? $"{readiness}%"
                : $"{readiness}% — ⚠ degraded fallback";

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"| {summary.ModelName} | {summary.Count} | {summary.SignalBackedCount} | {summary.DegradedCount} | {readinessNote} |"));
        }

        var degradedModels = ratingDependent
            .Where(summary => summary.DegradedCount > 0)
            .ToList();

        if (degradedModels.Count > 0)
        {
            lines.Add("");
            foreach (var model in degradedModels)
            {
                lines.Add(
                    $"- {model.ModelName}: {model.DegradedCount}/{model.Count} predictions fell back (missing as-of snapshots or required data).");

                if (model.DegradedReasonCounts.Count > 0)
                {
                    foreach (var (reason, count) in model.DegradedReasonCounts
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        lines.Add($"  • {reason}: {count}");
                    }
                }
            }
        }

    }

    private static string DisabledCoverageReason(int eligibleTargets) =>
        eligibleTargets == 0 ? "disabled, no eligible targets" : "disabled, no as-of snapshot pairs";

    private static IReadOnlyList<BacktestSegmentDeltaRow> GetSegmentDeltaRows(
        IReadOnlyList<BacktestSegmentModelSummary> segmentSummaries) =>
        segmentSummaries
            .GroupBy(summary => summary.SegmentName, StringComparer.Ordinal)
            .Select(group =>
            {
                var baseline = group.FirstOrDefault(summary =>
                    string.Equals(summary.Summary.ModelName, "Modelo base", StringComparison.Ordinal));
                var poisson = group.FirstOrDefault(summary =>
                    string.Equals(summary.Summary.ModelName, "Modelo de goles (Poisson)", StringComparison.Ordinal));

                return baseline is null || poisson is null
                    ? null
                    : new BacktestSegmentDeltaRow(
                        group.Key,
                        poisson.Summary.Count,
                        poisson.Summary.MeanBrier - baseline.Summary.MeanBrier,
                        poisson.Summary.MeanLogLoss - baseline.Summary.MeanLogLoss,
                        poisson.Summary.MeanRps - baseline.Summary.MeanRps,
                        poisson.Summary.TopPickAccuracy - baseline.Summary.TopPickAccuracy);
            })
            .Where(row => row is not null)
            .Cast<BacktestSegmentDeltaRow>()
            .ToList();

    private static IReadOnlyList<BacktestSegmentConfidenceGuidanceRow> GetSegmentConfidenceGuidanceRows(
        IReadOnlyList<BacktestSegmentDeltaRow> deltaRows) =>
        deltaRows
            .Select(row => row.SegmentName switch
            {
                BacktestMatchSegmentClassifier.AllMatches => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "Aggregate only",
                    "Use this as the overall benchmark; segment-specific rows are better for match-type confidence."),
                BacktestMatchSegmentClassifier.Friendlies => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "Lower confidence / noisy",
                    "Treat positive deltas as useful but weaker; rotation and experimental lineups likely add noise."),
                BacktestMatchSegmentClassifier.WorldCupQualifiers when HasLimitedSample(row) => LimitedSampleGuidance(row),
                BacktestMatchSegmentClassifier.WorldCupQualifiers when HasStrongSignal(row) => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "High confidence / strongest signal",
                    "Strong Poisson improvement with enough evaluated targets; this is the clearest segment signal."),
                BacktestMatchSegmentClassifier.WorldCupFinals when HasLimitedSample(row) => LimitedSampleGuidance(row),
                BacktestMatchSegmentClassifier.WorldCupFinals => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "Cautious confidence",
                    "Improvement can be meaningful, but knockout context keeps interpretation cautious."),
                BacktestMatchSegmentClassifier.OtherOfficialTournaments when HasLimitedSample(row) => LimitedSampleGuidance(row),
                BacktestMatchSegmentClassifier.OtherOfficialTournaments when HasUsefulPositiveSignal(row) => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "Good confidence",
                    "Positive official-match signal, but this bucket is broad and heterogeneous."),
                _ when HasLimitedSample(row) => LimitedSampleGuidance(row),
                _ when HasUsefulPositiveSignal(row) => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "Moderate confidence",
                    "Poisson improves over baseline; read alongside sample size and segment context."),
                _ => new BacktestSegmentConfidenceGuidanceRow(
                    row.SegmentName,
                    "Low confidence",
                    "Deltas do not show a clear improvement over baseline for this segment.")
            })
            .ToList();

    private static BacktestSegmentConfidenceGuidanceRow LimitedSampleGuidance(BacktestSegmentDeltaRow row) =>
        new(
            row.SegmentName,
            "Limited sample — cautious",
            "Improvement may exist, but the evaluated target count is small enough to avoid overclaiming.");

    private static bool HasLimitedSample(BacktestSegmentDeltaRow row) => row.Targets < 200;

    private static bool HasStrongSignal(BacktestSegmentDeltaRow row) =>
        row.Targets >= 1_000 &&
        row.DeltaBrier <= -0.1500 &&
        row.DeltaLogLoss <= -0.2000 &&
        row.DeltaRps <= -0.0500 &&
        row.DeltaTopPickAccuracy >= 0.1000;

    private static bool HasUsefulPositiveSignal(BacktestSegmentDeltaRow row) =>
        row.DeltaBrier < 0 &&
        row.DeltaLogLoss < 0 &&
        row.DeltaRps < 0 &&
        row.DeltaTopPickAccuracy > 0;

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
            else if (TryReadValue(arg, "--segment", out var segment))
                options = options with { Segment = ParseSegment(segment) };
        }

        return options;
    }

    public static string ParseSegment(string value)
    {
        var normalized = NormalizeSegmentValue(value);
        var segment = normalized switch
        {
            "all" or "all-matches" => BacktestMatchSegmentClassifier.AllMatches,
            "friendlies" or "friendly" => BacktestMatchSegmentClassifier.Friendlies,
            "world-cup-qualifiers" or "wc-qualifiers" or "qualifiers" => BacktestMatchSegmentClassifier.WorldCupQualifiers,
            "world-cup-finals" or "wc-finals" or "world-cup" => BacktestMatchSegmentClassifier.WorldCupFinals,
            "other-official" or "official" or "other" or "other-official-tournaments" => BacktestMatchSegmentClassifier.OtherOfficialTournaments,
            _ => null
        };

        return segment ?? throw new ArgumentException(
            $"Unknown --segment value '{value}'. Accepted values: all, friendlies, world-cup-qualifiers, world-cup-finals, other-official.",
            nameof(value));
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

        if (options.Segment is not null &&
            !string.Equals(options.Segment, BacktestMatchSegmentClassifier.AllMatches, StringComparison.Ordinal) &&
            !string.Equals(BacktestMatchSegmentClassifier.Classify(result.Tournament), options.Segment, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<BacktestSegmentModelSummary> FilterSegmentSummaries(
        IReadOnlyList<BacktestSegmentModelSummary> summaries,
        BacktestReportOptions options) =>
        options.Segment is null
            ? summaries
            : summaries
                .Where(summary => string.Equals(summary.SegmentName, options.Segment, StringComparison.Ordinal))
                .ToList();

    private static string NormalizeSegmentValue(string value) =>
        string.Join(
            '-',
            value
                .Trim()
                .ToLowerInvariant()
                .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));

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
    string? Segment = null,
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
    public IReadOnlyList<BacktestSegmentModelSummary> SegmentSummaries { get; init; } = [];
    public BacktestCoverageInfo? Coverage { get; init; }
}

internal sealed record BacktestSegmentDeltaRow(
    string SegmentName,
    int Targets,
    double DeltaBrier,
    double DeltaLogLoss,
    double DeltaRps,
    double DeltaTopPickAccuracy);

internal sealed record BacktestSegmentConfidenceGuidanceRow(
    string SegmentName,
    string Guidance,
    string Interpretation);
