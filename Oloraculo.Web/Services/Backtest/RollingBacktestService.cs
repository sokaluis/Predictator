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
        strategies ??= DefaultComparisonStrategies(goalModelYearsWindow);

        var evaluations = new List<PredictionEvaluation>();
        foreach (var strategy in strategies)
        {
            foreach (var point in BuildPredictionPoints(results, strategy, minimumPriorMatchesPerTeam, targetFilter))
            {
                var evaluation = ToEvaluation(point.Target, point.Prediction);
                evaluations.Add(evaluation);
            }
        }

        return new BacktestComparisonResult(evaluations, Summarize(evaluations));
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

    private static IReadOnlyList<BacktestModelSummary> Summarize(IReadOnlyList<PredictionEvaluation> evaluations) =>
        evaluations
            .GroupBy(evaluation => evaluation.ModelName)
            .Select(group => new BacktestModelSummary(
                group.Key,
                group.Count(),
                group.Average(evaluation => evaluation.BrierScore),
                group.Average(evaluation => evaluation.LogLoss),
                group.Average(evaluation => evaluation.RankedProbabilityScore),
                group.Average(evaluation => evaluation.TopPickCorrect ? 1.0 : 0.0)))
            .OrderBy(summary => summary.MeanBrier)
            .ThenBy(summary => summary.MeanLogLoss)
            .ThenBy(summary => summary.ModelName, StringComparer.Ordinal)
            .ToList();

    private static int CountTeamMatches(IEnumerable<MatchResult> results, string teamId) =>
        results.Count(result => result.HomeTeamId == teamId || result.AwayTeamId == teamId);

    private static void IncrementTeamCount(IDictionary<string, int> countsByTeam, string teamId) =>
        countsByTeam[teamId] = countsByTeam.TryGetValue(teamId, out var count) ? count + 1 : 1;

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

    public BacktestRatingSnapshot? GetSnapshot(DateTimeOffset targetDate, string homeTeamId, string awayTeamId) => null;
}

public sealed record BacktestComparisonResult(
    IReadOnlyList<PredictionEvaluation> Evaluations,
    IReadOnlyList<BacktestModelSummary> Summaries);

public sealed record BacktestModelSummary(
    string ModelName,
    int Count,
    double MeanBrier,
    double MeanLogLoss,
    double MeanRps,
    double TopPickAccuracy);
