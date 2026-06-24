using Oloraculo.Web.Models;
using Oloraculo.Web.Predictors;
using Oloraculo.Web.Probability;

namespace Oloraculo.Web.Services.Backtest;

public sealed class RollingBacktestService
{
    private readonly IBacktestRatingSnapshotProvider _ratingSnapshotProvider;

    public RollingBacktestService(IBacktestRatingSnapshotProvider? ratingSnapshotProvider = null)
    {
        _ratingSnapshotProvider = ratingSnapshotProvider ?? BacktestRatingSnapshotProvider.Empty;
    }

    public static IReadOnlyList<BacktestModelStrategy> DefaultComparisonStrategies(int goalModelYearsWindow = 8) =>
    [
        new BacktestModelStrategy("Modelo base", _ => new NullModel(), ReusePredictorForSamePriorResults: true),
        new BacktestModelStrategy(
            "Modelo de goles (Poisson)",
            priorResults => new GoalModel(priorResults, goalModelYearsWindow),
            ReusePredictorForSamePriorResults: true)
    ];

    public static BacktestModelStrategy OracleFinalComparisonStrategy(int goalModelYearsWindow = 8) =>
        new(
            "Oráculo final",
            priorResults =>
            {
                var goal = new GoalModel(priorResults, goalModelYearsWindow);
                var ladder = new IPredictor[]
                {
                    new NullModel(),
                    new FifaRankingModel(),
                    new EloModel(),
                    new RecentFormModel(),
                    goal,
                    new GoalPlusRecentContextModel(goal)
                };
                return new OracleFinalBacktestPredictor(ladder);
            },
            ReusePredictorForSamePriorResults: true);

    private sealed class OracleFinalBacktestPredictor(IReadOnlyList<IPredictor> ladder) : IPredictor
    {
        public string Name => "Oráculo final";
        public int Priority => int.MaxValue;

        public MatchPrediction Predict(MatchContext context)
        {
            var predictions = ladder.Select(p => p.Predict(context)).ToList();
            return FinalPredictionSelector.Select(predictions);
        }
    }

    public IReadOnlyList<BacktestPredictionPoint> BuildPredictionPoints(
        IEnumerable<MatchResult> results,
        int minimumPriorMatchesPerTeam = 1,
        int goalModelYearsWindow = 8)
    {
        var strategy = new BacktestModelStrategy(
            "Modelo de goles (Poisson)",
            priorResults => new GoalModel(priorResults, goalModelYearsWindow),
            ReusePredictorForSamePriorResults: true);

        return BuildPredictionPoints(results, strategy, minimumPriorMatchesPerTeam);
    }

    public IReadOnlyList<BacktestPredictionPoint> BuildPredictionPoints(
        IEnumerable<MatchResult> results,
        BacktestModelStrategy strategy,
        int minimumPriorMatchesPerTeam = 1,
        Func<MatchResult, bool>? targetFilter = null)
    {
        var ordered = OrderByDate(results);
        var points = new List<BacktestPredictionPoint>();
        var priorResults = new List<MatchResult>();
        var priorCountsByTeam = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var targetsOnDate in ordered.GroupBy(result => result.Date))
        {
            IReadOnlyList<MatchResult>? priorSnapshot = null;
            IPredictor? reusablePredictor = null;

            foreach (var target in targetsOnDate)
            {
                if (targetFilter is not null && !targetFilter(target))
                    continue;

                if (!HasEnoughPriorData(target, priorCountsByTeam, minimumPriorMatchesPerTeam))
                    continue;

                priorSnapshot ??= priorResults.ToList();
                var model = strategy.ReusePredictorForSamePriorResults
                    ? reusablePredictor ??= strategy.CreatePredictor(priorSnapshot)
                    : strategy.CreatePredictor(priorSnapshot);
                var context = BuildContext(target, priorSnapshot);
                points.Add(new BacktestPredictionPoint(target, priorSnapshot, model.Predict(context)));
            }

            foreach (var result in targetsOnDate)
            {
                priorResults.Add(result);
                IncrementTeamCount(priorCountsByTeam, result.HomeTeamId);
                IncrementTeamCount(priorCountsByTeam, result.AwayTeamId);
            }
        }

        return points;
    }

    public IReadOnlyList<PredictionEvaluation> Evaluate(
        IEnumerable<MatchResult> results,
        int minimumPriorMatchesPerTeam = 1,
        int goalModelYearsWindow = 8) =>
        BuildPredictionPoints(results, minimumPriorMatchesPerTeam, goalModelYearsWindow)
            .Select(point => ToEvaluation(point.Target, point.Prediction))
            .ToList();

    public BacktestComparisonResult Compare(
        IEnumerable<MatchResult> results,
        IReadOnlyList<BacktestModelStrategy>? strategies = null,
        int minimumPriorMatchesPerTeam = 1,
        int goalModelYearsWindow = 8,
        Func<MatchResult, bool>? targetFilter = null)
    {
        var orderedResults = OrderByDate(results);

        // Compute eligible targets once for coverage and strategy selection.
        var eligibleTargets = EligibleTargets(orderedResults, minimumPriorMatchesPerTeam, targetFilter).ToList();
        var eloCoveredCount = eligibleTargets.Count(target => _ratingSnapshotProvider.HasAsOfSnapshotPair(
            RatingTypeEnum.Elo, target.Date, target.HomeTeamId, target.AwayTeamId));
        var fifaCoveredCount = eligibleTargets.Count(target => _ratingSnapshotProvider.HasAsOfSnapshotPair(
            RatingTypeEnum.Fifa, target.Date, target.HomeTeamId, target.AwayTeamId));
        var includeElo = eloCoveredCount > 0;
        var includeFifa = fifaCoveredCount > 0;

        var coverage = new BacktestCoverageInfo(eligibleTargets.Count, eloCoveredCount, fifaCoveredCount, includeElo, includeFifa);

        strategies ??= ComparisonStrategiesForResults(goalModelYearsWindow, includeElo, includeFifa);

        var evaluations = new List<BacktestPredictionEvaluation>();
        var segmentedEvaluations = new List<(string SegmentName, BacktestPredictionEvaluation Evaluation)>();

        foreach (var strategy in strategies)
        {
            foreach (var point in BuildPredictionPoints(orderedResults, strategy, minimumPriorMatchesPerTeam, targetFilter))
            {
                var evaluation = ToBacktestEvaluation(point.Target, point.Prediction);
                evaluations.Add(evaluation);
                segmentedEvaluations.Add((BacktestMatchSegmentClassifier.AllMatches, evaluation));
                segmentedEvaluations.Add((BacktestMatchSegmentClassifier.Classify(point.Target.Tournament), evaluation));
            }
        }

        return new BacktestComparisonResult(evaluations.Select(item => item.Evaluation).ToList(), Summarize(evaluations))
        {
            SegmentSummaries = SummarizeSegments(segmentedEvaluations),
            Coverage = coverage
        };
    }

    public static IReadOnlyList<MatchResult> OrderByDate(IEnumerable<MatchResult> results) =>
        results
            .OrderBy(result => result.Date)
            .ThenBy(result => result.Id, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<MatchResult> PriorResultsFor(
        IEnumerable<MatchResult> orderedResults,
        MatchResult target) =>
        orderedResults
            .Where(result => result.Date < target.Date)
            .OrderBy(result => result.Date)
            .ThenBy(result => result.Id, StringComparer.Ordinal)
            .ToList();

    public static bool HasEnoughPriorData(
        MatchResult target,
        IReadOnlyList<MatchResult> priorResults,
        int minimumPriorMatchesPerTeam) =>
        CountTeamMatches(priorResults, target.HomeTeamId) >= minimumPriorMatchesPerTeam &&
        CountTeamMatches(priorResults, target.AwayTeamId) >= minimumPriorMatchesPerTeam;

    private static bool HasEnoughPriorData(
        MatchResult target,
        IReadOnlyDictionary<string, int> priorCountsByTeam,
        int minimumPriorMatchesPerTeam) =>
        priorCountsByTeam.GetValueOrDefault(target.HomeTeamId) >= minimumPriorMatchesPerTeam &&
        priorCountsByTeam.GetValueOrDefault(target.AwayTeamId) >= minimumPriorMatchesPerTeam;

    private MatchContext BuildContext(MatchResult target, IReadOnlyList<MatchResult> priorResults)
    {
        var snapshot = _ratingSnapshotProvider.GetSnapshot(target.Date, target.HomeTeamId, target.AwayTeamId);

        return new MatchContext
        {
            Fixture = new Fixture
            {
                Id = $"backtest:{target.Id}",
                HomeTeamId = target.HomeTeamId,
                AwayTeamId = target.AwayTeamId,
                KickoffUtc = target.Date,
                NeutralVenue = target.Neutral,
                IsPlayed = false
            },
            HomeTeam = new Team { Id = target.HomeTeamId, Name = target.HomeTeamId },
            AwayTeam = new Team { Id = target.AwayTeamId, Name = target.AwayTeamId },
            HomeElo = AsOfRating(snapshot?.HomeElo, target.Date, target.HomeTeamId, RatingTypeEnum.Elo),
            AwayElo = AsOfRating(snapshot?.AwayElo, target.Date, target.AwayTeamId, RatingTypeEnum.Elo),
            HomeFifaRank = AsOfRating(snapshot?.HomeFifaRank, target.Date, target.HomeTeamId, RatingTypeEnum.Fifa),
            AwayFifaRank = AsOfRating(snapshot?.AwayFifaRank, target.Date, target.AwayTeamId, RatingTypeEnum.Fifa),
            HomeRecentMatchHistory = RecentTeamResults(priorResults, target.HomeTeamId),
            AwayRecentMatchHistory = RecentTeamResults(priorResults, target.AwayTeamId)
        };
    }

    private static Rating? AsOfRating(
        Rating? rating,
        DateTimeOffset targetDate,
        string teamId,
        RatingTypeEnum type) =>
        rating is not null &&
        rating.AsOf <= targetDate &&
        rating.TeamId == teamId &&
        rating.Type == type
            ? rating
            : null;

    private static BacktestPredictionEvaluation ToBacktestEvaluation(MatchResult target, MatchPrediction prediction) =>
        new(ToEvaluation(target, prediction), prediction.Degraded, NormalizeDegradedReasons(prediction))
        {
            ChosenPredictorName = ExtractChosenPredictorName(prediction),
            RankingBiasApplied = HasRankingBiasApplied(prediction)
        };

    private static bool HasRankingBiasApplied(MatchPrediction prediction) =>
        string.Equals(prediction.PredictorName, "Oráculo final", StringComparison.Ordinal) &&
        prediction.Drivers.Any(d => d.Contains("Aplicó una calibración Elo/FIFA", StringComparison.Ordinal));

    private static IReadOnlyList<string> NormalizeDegradedReasons(MatchPrediction prediction)
    {
        if (!prediction.Degraded)
            return [];

        if (prediction.FeaturesMissing.Count > 0)
            return prediction.FeaturesMissing
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Small normalization from Explanation when FeaturesMissing is empty but Degraded is true.
        // This handles fallback reasons that predictors express only in Explanation text.
        var explanation = prediction.Explanation ?? "";

        if (explanation.Contains("Elo", StringComparison.OrdinalIgnoreCase))
            return ["ratings Elo"];

        if (explanation.Contains("FIFA", StringComparison.OrdinalIgnoreCase))
            return ["ranking FIFA"];

        if (explanation.Contains("historial reciente", StringComparison.OrdinalIgnoreCase) ||
            explanation.Contains("recent", StringComparison.OrdinalIgnoreCase))
            return ["historial reciente"];

        return ["motivo no clasificado"];
    }

    private static string? ExtractChosenPredictorName(MatchPrediction prediction)
    {
        if (!string.Equals(prediction.PredictorName, "Oráculo final", StringComparison.Ordinal))
            return null;

        return prediction.Sources
            .FirstOrDefault(s => string.Equals(s.Name, "model ladder", StringComparison.OrdinalIgnoreCase))
            ?.Notes;
    }

    private static PredictionEvaluation ToEvaluation(MatchResult target, MatchPrediction prediction)
    {
        var actual = EvaluationService.OutcomeFromGoals(target.HomeGoals, target.AwayGoals);

        return new PredictionEvaluation
        {
            ModelName = prediction.PredictorName,
            FixtureId = $"backtest:{target.Id}",
            HomeTeamId = target.HomeTeamId,
            AwayTeamId = target.AwayTeamId,
            HomeGoals = target.HomeGoals,
            AwayGoals = target.AwayGoals,
            HomeWin = prediction.Outcome.HomeWin,
            Draw = prediction.Outcome.Draw,
            AwayWin = prediction.Outcome.AwayWin,
            Actual = actual,
            BrierScore = ProbabilityHelper.BrierScore(prediction.Outcome, actual),
            RankedProbabilityScore = ProbabilityHelper.RankedProbabilityScore(prediction.Outcome, actual),
            LogLoss = ProbabilityHelper.LogLoss(prediction.Outcome, actual),
            TopPickCorrect = prediction.Outcome.TopPick == actual,
            PredictedAt = target.Date
        };
    }

    private static IReadOnlyList<BacktestModelSummary> Summarize(IReadOnlyList<BacktestPredictionEvaluation> evaluations) =>
        evaluations
            .GroupBy(evaluation => evaluation.Evaluation.ModelName)
            .Select(group =>
            {
                var count = group.Count();
                var signalBacked = group.Count(evaluation => !evaluation.Degraded);
                var degraded = count - signalBacked;

                var reasonCounts = group
                    .Where(evaluation => evaluation.Degraded)
                    .SelectMany(evaluation => evaluation.DegradedReasons)
                    .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                var chosenPredictorCounts = group
                    .Where(evaluation => evaluation.ChosenPredictorName is not null)
                    .GroupBy(evaluation => evaluation.ChosenPredictorName!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                var biasAppliedCount = group.Count(evaluation => evaluation.RankingBiasApplied);
                var biasNotAppliedCount = group.Count(evaluation =>
                    string.Equals(evaluation.Evaluation.ModelName, "Oráculo final", StringComparison.Ordinal) &&
                    !evaluation.RankingBiasApplied);

                BacktestBiasGroupSummary? rankingBiasAppliedSummary = null;
                BacktestBiasGroupSummary? rankingBiasNotAppliedSummary = null;

                if (string.Equals(group.Key, "Oráculo final", StringComparison.Ordinal))
                {
                    var appliedEvals = group.Where(e => e.RankingBiasApplied).ToList();
                    if (appliedEvals.Count > 0)
                    {
                        rankingBiasAppliedSummary = new BacktestBiasGroupSummary(
                            appliedEvals.Count,
                            appliedEvals.Average(e => e.Evaluation.BrierScore),
                            appliedEvals.Average(e => e.Evaluation.LogLoss),
                            appliedEvals.Average(e => e.Evaluation.RankedProbabilityScore),
                            appliedEvals.Average(e => e.Evaluation.TopPickCorrect ? 1.0 : 0.0));
                    }

                    var notAppliedEvals = group.Where(e => !e.RankingBiasApplied).ToList();
                    if (notAppliedEvals.Count > 0)
                    {
                        rankingBiasNotAppliedSummary = new BacktestBiasGroupSummary(
                            notAppliedEvals.Count,
                            notAppliedEvals.Average(e => e.Evaluation.BrierScore),
                            notAppliedEvals.Average(e => e.Evaluation.LogLoss),
                            notAppliedEvals.Average(e => e.Evaluation.RankedProbabilityScore),
                            notAppliedEvals.Average(e => e.Evaluation.TopPickCorrect ? 1.0 : 0.0));
                    }
                }

                return new BacktestModelSummary(
                    group.Key,
                    count,
                    group.Average(evaluation => evaluation.Evaluation.BrierScore),
                    group.Average(evaluation => evaluation.Evaluation.LogLoss),
                    group.Average(evaluation => evaluation.Evaluation.RankedProbabilityScore),
                    group.Average(evaluation => evaluation.Evaluation.TopPickCorrect ? 1.0 : 0.0))
                {
                    SignalBackedCount = signalBacked,
                    DegradedCount = degraded,
                    DegradedReasonCounts = reasonCounts,
                    ChosenPredictorCounts = chosenPredictorCounts,
                    RankingBiasAppliedCount = biasAppliedCount,
                    RankingBiasNotAppliedCount = biasNotAppliedCount,
                    RankingBiasAppliedSummary = rankingBiasAppliedSummary,
                    RankingBiasNotAppliedSummary = rankingBiasNotAppliedSummary
                };
            })
            .OrderBy(summary => summary.MeanBrier)
            .ThenBy(summary => summary.MeanLogLoss)
            .ThenBy(summary => summary.ModelName, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<BacktestSegmentModelSummary> SummarizeSegments(
        IReadOnlyList<(string SegmentName, BacktestPredictionEvaluation Evaluation)> segmentedEvaluations) =>
        segmentedEvaluations
            .GroupBy(item => item.SegmentName)
            .SelectMany(segmentGroup => Summarize(segmentGroup.Select(item => item.Evaluation).ToList())
                .Select(summary => new BacktestSegmentModelSummary(segmentGroup.Key, summary)))
            .OrderBy(summary => SegmentOrder(summary.SegmentName))
            .ThenBy(summary => summary.Summary.MeanBrier)
            .ThenBy(summary => summary.Summary.MeanLogLoss)
            .ThenBy(summary => summary.Summary.ModelName, StringComparer.Ordinal)
            .ToList();

    private static int SegmentOrder(string segmentName)
    {
        for (var index = 0; index < BacktestMatchSegmentClassifier.OrderedSegments.Count; index++)
        {
            if (string.Equals(BacktestMatchSegmentClassifier.OrderedSegments[index], segmentName, StringComparison.Ordinal))
                return index;
        }

        return BacktestMatchSegmentClassifier.OrderedSegments.Count;
    }

    private static int CountTeamMatches(IEnumerable<MatchResult> results, string teamId) =>
        results.Count(result => result.HomeTeamId == teamId || result.AwayTeamId == teamId);

    private static void IncrementTeamCount(IDictionary<string, int> countsByTeam, string teamId) =>
        countsByTeam[teamId] = countsByTeam.TryGetValue(teamId, out var count) ? count + 1 : 1;

    private static IReadOnlyList<BacktestModelStrategy> ComparisonStrategiesForResults(
        int goalModelYearsWindow,
        bool includeElo,
        bool includeFifa)
    {
        var strategies = DefaultComparisonStrategies(goalModelYearsWindow).ToList();

        AddRatingAwareStrategies(strategies, includeElo, includeFifa);

        // Always include Oráculo final — it handles its own degradation internally.
        strategies.Add(OracleFinalComparisonStrategy(goalModelYearsWindow));

        return strategies;
    }

    private static IEnumerable<MatchResult> EligibleTargets(
        IReadOnlyList<MatchResult> ordered,
        int minimumPriorMatchesPerTeam,
        Func<MatchResult, bool>? targetFilter)
    {
        var priorCountsByTeam = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var targetsOnDate in ordered.GroupBy(result => result.Date))
        {
            foreach (var target in targetsOnDate)
            {
                if ((targetFilter is null || targetFilter(target)) &&
                    HasEnoughPriorData(target, priorCountsByTeam, minimumPriorMatchesPerTeam))
                    yield return target;
            }

            foreach (var result in targetsOnDate)
            {
                IncrementTeamCount(priorCountsByTeam, result.HomeTeamId);
                IncrementTeamCount(priorCountsByTeam, result.AwayTeamId);
            }
        }
    }

    private static void AddRatingAwareStrategies(
        ICollection<BacktestModelStrategy> strategies,
        bool includeElo,
        bool includeFifa)
    {
        if (includeElo)
        {
            strategies.Add(new BacktestModelStrategy("Elo", _ => new EloModel(), ReusePredictorForSamePriorResults: true));
            strategies.Add(new BacktestModelStrategy("Forma reciente", _ => new RecentFormModel(), ReusePredictorForSamePriorResults: true));
        }

        if (includeFifa)
            strategies.Add(new BacktestModelStrategy("Ranking FIFA", _ => new FifaRankingModel(), ReusePredictorForSamePriorResults: true));
    }

    private static IReadOnlyList<MatchResult> RecentTeamResults(
        IEnumerable<MatchResult> results,
        string teamId) =>
        results
            .Where(result => result.HomeTeamId == teamId || result.AwayTeamId == teamId)
            .OrderByDescending(result => result.Date)
            .ToList();
}

public sealed record BacktestPredictionPoint(
    MatchResult Target,
    IReadOnlyList<MatchResult> PriorResults,
    MatchPrediction Prediction);

public sealed record BacktestModelStrategy(
    string Name,
    Func<IReadOnlyList<MatchResult>, IPredictor> CreatePredictor,
    bool ReusePredictorForSamePriorResults = false);

public interface IBacktestRatingSnapshotProvider
{
    bool HasAsOfSnapshotPair(RatingTypeEnum type, DateTimeOffset targetDate, string homeTeamId, string awayTeamId);

    BacktestRatingSnapshot? GetSnapshot(DateTimeOffset targetDate, string homeTeamId, string awayTeamId);
}

public sealed record BacktestRatingSnapshot(
    Rating? HomeElo = null,
    Rating? AwayElo = null,
    Rating? HomeFifaRank = null,
    Rating? AwayFifaRank = null);

public sealed class BacktestRatingSnapshotProvider : IBacktestRatingSnapshotProvider
{
    public static BacktestRatingSnapshotProvider Empty { get; } = new();

    private BacktestRatingSnapshotProvider()
    {
    }

    public bool HasAsOfSnapshotPair(RatingTypeEnum type, DateTimeOffset targetDate, string homeTeamId, string awayTeamId) => false;

    public BacktestRatingSnapshot? GetSnapshot(DateTimeOffset targetDate, string homeTeamId, string awayTeamId) => null;
}

public sealed class InMemoryBacktestRatingSnapshotProvider : IBacktestRatingSnapshotProvider
{
    private readonly IReadOnlyList<Rating> _ratings;

    public InMemoryBacktestRatingSnapshotProvider(IEnumerable<Rating> ratings)
    {
        _ratings = ratings.ToList();
    }

    public bool HasAsOfSnapshotPair(RatingTypeEnum type, DateTimeOffset targetDate, string homeTeamId, string awayTeamId) =>
        LatestAsOf(targetDate, homeTeamId, type) is not null &&
        LatestAsOf(targetDate, awayTeamId, type) is not null;

    public BacktestRatingSnapshot? GetSnapshot(DateTimeOffset targetDate, string homeTeamId, string awayTeamId)
    {
        var homeElo = LatestAsOf(targetDate, homeTeamId, RatingTypeEnum.Elo);
        var awayElo = LatestAsOf(targetDate, awayTeamId, RatingTypeEnum.Elo);
        var homeFifa = LatestAsOf(targetDate, homeTeamId, RatingTypeEnum.Fifa);
        var awayFifa = LatestAsOf(targetDate, awayTeamId, RatingTypeEnum.Fifa);

        return homeElo is null && awayElo is null && homeFifa is null && awayFifa is null
            ? null
            : new BacktestRatingSnapshot(homeElo, awayElo, homeFifa, awayFifa);
    }

    private Rating? LatestAsOf(DateTimeOffset targetDate, string teamId, RatingTypeEnum type) =>
        _ratings
            .Where(rating =>
                rating.TeamId == teamId &&
                rating.Type == type &&
                rating.AsOf <= targetDate)
            .OrderByDescending(rating => rating.AsOf)
            .FirstOrDefault();
}

public sealed record BacktestCoverageInfo(
    int EligibleTargets,
    int EloCoveredTargets,
    int FifaCoveredTargets,
    bool EloEnabled,
    bool FifaEnabled)
{
    public bool RecentFormEnabled => EloEnabled;
}

public sealed record BacktestComparisonResult(
    IReadOnlyList<PredictionEvaluation> Evaluations,
    IReadOnlyList<BacktestModelSummary> Summaries)
{
    public IReadOnlyList<BacktestSegmentModelSummary> SegmentSummaries { get; init; } = [];
    public BacktestCoverageInfo? Coverage { get; init; }
}

public sealed record BacktestModelSummary(
    string ModelName,
    int Count,
    double MeanBrier,
    double MeanLogLoss,
    double MeanRps,
    double TopPickAccuracy)
{
    public int SignalBackedCount { get; init; } = Count;
    public int DegradedCount { get; init; }

    public IReadOnlyDictionary<string, int> DegradedReasonCounts { get; init; } =
        new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> ChosenPredictorCounts { get; init; } =
        new Dictionary<string, int>();

    public int RankingBiasAppliedCount { get; init; }
    public int RankingBiasNotAppliedCount { get; init; }

    public BacktestBiasGroupSummary? RankingBiasAppliedSummary { get; init; }
    public BacktestBiasGroupSummary? RankingBiasNotAppliedSummary { get; init; }

    public double ReadinessPct => Count > 0 ? SignalBackedCount * 100.0 / Count : 100.0;

    public bool IsRatingDependent =>
        ModelName is "Elo" or "Ranking FIFA" or "Forma reciente";
}

internal sealed record BacktestPredictionEvaluation(
    PredictionEvaluation Evaluation,
    bool Degraded,
    IReadOnlyList<string> DegradedReasons)
{
    public string? ChosenPredictorName { get; init; }
    public bool RankingBiasApplied { get; init; }
}

public sealed record BacktestSegmentModelSummary(
    string SegmentName,
    BacktestModelSummary Summary);

public sealed record BacktestBiasGroupSummary(
    int Count,
    double MeanBrier,
    double MeanLogLoss,
    double MeanRps,
    double TopPickAccuracy);
