using Oloraculo.Web.Models;
using Oloraculo.Web.Predictors;
using Oloraculo.Web.Probability;
using Oloraculo.Web.Services.Backtest;

namespace Oloraculo.Web.Tests.Services.Backtest;

public class RollingBacktestServiceTests
{
    [Fact]
    public void OrderByDate_SortsHistoricalMatchesChronologically()
    {
        var results = new[]
        {
            Result("latest", "a", "b", "2024-01-03", 1, 0),
            Result("earliest", "a", "c", "2024-01-01", 0, 0),
            Result("middle", "b", "c", "2024-01-02", 2, 1)
        };

        var ordered = RollingBacktestService.OrderByDate(results);

        Assert.Equal(["earliest", "middle", "latest"], ordered.Select(result => result.Id));
    }

    [Fact]
    public void PriorResultsFor_UsesOnlyMatchesBeforeTargetDate()
    {
        var target = Result("target", "a", "b", "2024-01-03", 1, 0);
        var ordered = RollingBacktestService.OrderByDate(
        [
            Result("later", "a", "c", "2024-01-04", 0, 1),
            target,
            Result("same-day", "b", "c", "2024-01-03", 2, 2),
            Result("prior", "c", "a", "2024-01-01", 1, 1)
        ]);

        var prior = RollingBacktestService.PriorResultsFor(ordered, target);

        Assert.Equal(["prior"], prior.Select(result => result.Id));
        Assert.All(prior, result => Assert.True(result.Date < target.Date));
    }

    [Fact]
    public void BuildPredictionPoints_SkipsMatchesUntilBothTeamsHavePriorData()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("eligible", "a", "b", "2024-01-03", 1, 0)
        };

        var points = new RollingBacktestService().BuildPredictionPoints(results, minimumPriorMatchesPerTeam: 1);

        var point = Assert.Single(points);
        Assert.Equal("eligible", point.Target.Id);
        Assert.Equal(["first", "second"], point.PriorResults.Select(result => result.Id));
        Assert.All(point.PriorResults, result => Assert.True(result.Date < point.Target.Date));
    }

    [Fact]
    public void Evaluate_ComputesMetricsForDeterministicRollingSample()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };

        var evaluations = new RollingBacktestService().Evaluate(results, minimumPriorMatchesPerTeam: 1);

        var evaluation = Assert.Single(evaluations);
        Assert.Equal("backtest:target", evaluation.FixtureId);
        Assert.Equal("Home", evaluation.Actual);
        Assert.Equal(new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero), evaluation.PredictedAt);
        Assert.InRange(evaluation.HomeWin + evaluation.Draw + evaluation.AwayWin, 0.999999, 1.000001);
        Assert.True(double.IsFinite(evaluation.BrierScore));
        Assert.True(double.IsFinite(evaluation.RankedProbabilityScore));
        Assert.True(double.IsFinite(evaluation.LogLoss));
    }

    [Fact]
    public void Compare_EvaluatesEveryStrategyAgainstSameEligibleTargets()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("future", "a", "b", "2024-01-04", 0, 1)
        };
        var strategies = new[]
        {
            Strategy("home-heavy", new OutcomeProbabilities(0.80, 0.10, 0.10)),
            Strategy("away-heavy", new OutcomeProbabilities(0.10, 0.10, 0.80))
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            strategies,
            minimumPriorMatchesPerTeam: 1);

        Assert.Equal(4, comparison.Evaluations.Count);
        Assert.Equal(["backtest:target", "backtest:future"], comparison.Evaluations
            .Where(evaluation => evaluation.ModelName == "home-heavy")
            .Select(evaluation => evaluation.FixtureId));
        Assert.Equal(["backtest:target", "backtest:future"], comparison.Evaluations
            .Where(evaluation => evaluation.ModelName == "away-heavy")
            .Select(evaluation => evaluation.FixtureId));
    }

    [Fact]
    public void Compare_GivesEveryStrategyTheSamePriorOnlyDatasetPerTarget()
    {
        var seen = new Dictionary<string, List<string[]>>();
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("same-day", "a", "b", "2024-01-03", 3, 0),
            Result("future", "a", "b", "2024-01-04", 0, 1)
        };
        var strategies = new[]
        {
            RecordingStrategy("first-model", seen),
            RecordingStrategy("second-model", seen)
        };

        new RollingBacktestService().Compare(results, strategies, minimumPriorMatchesPerTeam: 1);

        Assert.Equal(["first", "second"], seen["backtest:target"][0]);
        Assert.Equal(["first", "second"], seen["backtest:target"][1]);
        Assert.Equal(["first", "second", "same-day", "target"], seen["backtest:future"][0]);
        Assert.Equal(["first", "second", "same-day", "target"], seen["backtest:future"][1]);
        Assert.DoesNotContain(seen.Values.SelectMany(items => items).SelectMany(ids => ids), id => id == "future");
    }

    [Fact]
    public void Compare_TargetFilterLimitsEvaluatedMatchesButKeepsPriorHistoryStrict()
    {
        var seen = new Dictionary<string, List<string[]>>();
        var results = new[]
        {
            Result("prior-a", "c", "a", "2024-01-01", 1, 1),
            Result("prior-b", "b", "c", "2024-01-01", 2, 0),
            Result("target", "a", "b", "2024-01-02", 1, 0),
            Result("same-day", "a", "b", "2024-01-02", 3, 0),
            Result("future", "a", "b", "2024-01-03", 0, 1)
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            [RecordingStrategy("recording", seen)],
            minimumPriorMatchesPerTeam: 1,
            targetFilter: result => result.Id == "target");

        var evaluation = Assert.Single(comparison.Evaluations);
        Assert.Equal("backtest:target", evaluation.FixtureId);
        Assert.Equal(["prior-a", "prior-b"], seen["backtest:target"][0]);
        Assert.DoesNotContain(seen["backtest:target"][0], id => id is "same-day" or "future");
    }

    [Fact]
    public void BuildPredictionPoints_ReusableStrategyExcludesTargetSameDayAndFutureRows()
    {
        var factoryPriorIds = new List<string[]>();
        var seen = new Dictionary<string, List<string[]>>();
        var results = new[]
        {
            Result("prior-a", "x", "a", "2024-01-01", 1, 1),
            Result("prior-b", "b", "x", "2024-01-01", 2, 0),
            Result("target", "a", "b", "2024-01-02", 1, 0),
            Result("same-day", "a", "b", "2024-01-02", 3, 0),
            Result("future", "a", "b", "2024-01-03", 0, 1)
        };
        var strategy = new BacktestModelStrategy(
            "reusable-recording",
            priorResults =>
            {
                var priorIds = priorResults.Select(result => result.Id).ToArray();
                factoryPriorIds.Add(priorIds);
                return new RecordingPredictor("reusable-recording", OutcomeProbabilities.Uniform, seen, priorIds);
            },
            ReusePredictorForSamePriorResults: true);

        var points = new RollingBacktestService().BuildPredictionPoints(
            results,
            strategy,
            minimumPriorMatchesPerTeam: 1,
            targetFilter: result => result.Date == DateTimeOffset.Parse("2024-01-02T00:00:00Z"));

        Assert.Equal(["same-day", "target"], points.Select(point => point.Target.Id));
        var priorIds = Assert.Single(factoryPriorIds);
        Assert.Equal(["prior-a", "prior-b"], priorIds);
        Assert.Equal(["prior-a", "prior-b"], seen["backtest:target"][0]);
        Assert.Equal(["prior-a", "prior-b"], seen["backtest:same-day"][0]);
        Assert.DoesNotContain(priorIds, id => id is "target" or "same-day" or "future");
    }

    [Fact]
    public void Compare_TargetFilterCanUsePriorHistoryOutsideEvaluationWindow()
    {
        var seen = new Dictionary<string, List<string[]>>();
        var results = new[]
        {
            Result("outside-window-a", "c", "a", "2024-01-01", 1, 1),
            Result("outside-window-b", "b", "c", "2024-01-01", 2, 0),
            Result("target", "a", "b", "2024-01-02", 1, 0),
            Result("outside-window-future", "a", "b", "2024-01-03", 0, 1)
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            [RecordingStrategy("recording", seen) with { ReusePredictorForSamePriorResults = true }],
            minimumPriorMatchesPerTeam: 1,
            targetFilter: result => result.Id == "target");

        var evaluation = Assert.Single(comparison.Evaluations);
        Assert.Equal("backtest:target", evaluation.FixtureId);
        Assert.Equal(["outside-window-a", "outside-window-b"], seen["backtest:target"][0]);
    }

    [Fact]
    public void Compare_SegmentSummariesUseTargetTournamentWithoutFilteringPriorHistory()
    {
        var seen = new Dictionary<string, List<string[]>>();
        var results = new[]
        {
            Result("friendly-prior-a", "c", "a", "2024-01-01", 1, 1, tournament: "Friendly"),
            Result("friendly-prior-b", "b", "c", "2024-01-01", 2, 0, tournament: "Friendly"),
            Result("world-cup-target", "a", "b", "2024-01-02", 1, 0, tournament: "FIFA World Cup")
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            [RecordingStrategy("recording", seen)],
            minimumPriorMatchesPerTeam: 1);

        var evaluation = Assert.Single(comparison.Evaluations);
        Assert.Equal("backtest:world-cup-target", evaluation.FixtureId);
        Assert.Equal(["friendly-prior-a", "friendly-prior-b"], seen["backtest:world-cup-target"][0]);
        Assert.Contains(comparison.SegmentSummaries, summary =>
            summary.SegmentName == BacktestMatchSegmentClassifier.WorldCupFinals &&
            summary.Summary.ModelName == "recording" &&
            summary.Summary.Count == 1);
        Assert.DoesNotContain(comparison.SegmentSummaries, summary =>
            summary.SegmentName == BacktestMatchSegmentClassifier.Friendlies);
    }

    [Fact]
    public void Compare_CacheableDefaultStrategiesMatchUncachedReferenceMetrics()
    {
        var results = new[]
        {
            Result("prior-a", "x", "a", "2024-01-01", 1, 1),
            Result("prior-b", "b", "x", "2024-01-01", 2, 0),
            Result("prior-c", "y", "c", "2024-01-01", 0, 1),
            Result("prior-d", "d", "y", "2024-01-01", 1, 2),
            Result("target-ab", "a", "b", "2024-01-02", 1, 0),
            Result("target-cd", "c", "d", "2024-01-02", 0, 1),
            Result("future", "a", "c", "2024-01-03", 2, 2)
        };
        var cached = new RollingBacktestService().Compare(
            results,
            RollingBacktestService.DefaultComparisonStrategies(),
            minimumPriorMatchesPerTeam: 1);
        var uncached = new RollingBacktestService().Compare(
            results,
            [
                new BacktestModelStrategy("Modelo base", _ => new NullModel()),
                new BacktestModelStrategy("Modelo de goles (Poisson)", priorResults => new GoalModel(priorResults))
            ],
            minimumPriorMatchesPerTeam: 1);

        Assert.Equal(uncached.Evaluations.Count, cached.Evaluations.Count);
        foreach (var (expected, actual) in uncached.Evaluations.Zip(cached.Evaluations))
        {
            Assert.Equal(expected.ModelName, actual.ModelName);
            Assert.Equal(expected.FixtureId, actual.FixtureId);
            Assert.Equal(expected.Actual, actual.Actual);
            Assert.Equal(expected.HomeWin, actual.HomeWin, precision: 12);
            Assert.Equal(expected.Draw, actual.Draw, precision: 12);
            Assert.Equal(expected.AwayWin, actual.AwayWin, precision: 12);
            Assert.Equal(expected.BrierScore, actual.BrierScore, precision: 12);
            Assert.Equal(expected.LogLoss, actual.LogLoss, precision: 12);
            Assert.Equal(expected.RankedProbabilityScore, actual.RankedProbabilityScore, precision: 12);
        }

        foreach (var (expected, actual) in uncached.Summaries.Zip(cached.Summaries))
        {
            Assert.Equal(expected.ModelName, actual.ModelName);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.MeanBrier, actual.MeanBrier, precision: 12);
            Assert.Equal(expected.MeanLogLoss, actual.MeanLogLoss, precision: 12);
            Assert.Equal(expected.MeanRps, actual.MeanRps, precision: 12);
            Assert.Equal(expected.TopPickAccuracy, actual.TopPickAccuracy, precision: 12);
        }
    }

    [Fact]
    public void Compare_SummarizesAndRanksModelsByMeanBrierThenLogLoss()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("future", "a", "b", "2024-01-04", 2, 1)
        };
        var strategies = new[]
        {
            Strategy("good", new OutcomeProbabilities(0.80, 0.10, 0.10)),
            Strategy("bad", new OutcomeProbabilities(0.10, 0.10, 0.80))
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            strategies,
            minimumPriorMatchesPerTeam: 1);

        Assert.Equal(["good", "bad"], comparison.Summaries.Select(summary => summary.ModelName));
        Assert.All(comparison.Summaries, summary => Assert.Equal(2, summary.Count));
        Assert.True(comparison.Summaries[0].MeanBrier < comparison.Summaries[1].MeanBrier);
        Assert.True(comparison.Summaries[0].MeanLogLoss < comparison.Summaries[1].MeanLogLoss);
    }

    [Fact]
    public void Compare_DefaultStrategiesIncludeGoalModelAndUniformBaseline()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };

        var comparison = new RollingBacktestService().Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.Equal(["Modelo base", "Modelo de goles (Poisson)"], comparison.Evaluations
            .Select(evaluation => evaluation.ModelName)
            .OrderBy(modelName => modelName, StringComparer.Ordinal));
        Assert.All(comparison.Evaluations, evaluation => Assert.Equal("backtest:target", evaluation.FixtureId));
    }

    [Fact]
    public void Compare_DefaultStrategiesExcludeRatingAwareModelsWhenAsOfSnapshotsAreUnavailable()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };

        var comparison = new RollingBacktestService().Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.Equal(["Modelo base", "Modelo de goles (Poisson)"], comparison.Evaluations
            .Select(evaluation => evaluation.ModelName)
            .OrderBy(modelName => modelName, StringComparer.Ordinal));
    }

    [Fact]
    public void Compare_DefaultStrategiesIncludeRatingAwareModelsWhenAsOfSnapshotsAreAvailable()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Elo, 1400, targetDate.AddDays(-1)),
            Rating("a", RatingTypeEnum.Fifa, 1600, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Fifa, 1300, targetDate.AddDays(-1))
        ]);
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.Equal([
            "Elo",
            "Forma reciente",
            "Modelo base",
            "Modelo de goles (Poisson)",
            "Ranking FIFA"
        ], comparison.Evaluations
            .Select(evaluation => evaluation.ModelName)
            .OrderBy(modelName => modelName, StringComparer.Ordinal));
        Assert.All(comparison.Evaluations, evaluation => Assert.Equal("backtest:target", evaluation.FixtureId));
    }

    [Fact]
    public void Compare_EloOnlySnapshotsIncludeEloAndRecentFormButNotFifa()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Elo, 1400, targetDate.AddDays(-1))
        ]);
        var results = RatingGatedResults();

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.Contains(comparison.Evaluations, evaluation => evaluation.ModelName == "Elo");
        Assert.Contains(comparison.Evaluations, evaluation => evaluation.ModelName == "Forma reciente");
        Assert.DoesNotContain(comparison.Evaluations, evaluation => evaluation.ModelName == "Ranking FIFA");
    }

    [Fact]
    public void Compare_FifaOnlySnapshotsIncludeFifaButNotEloOrRecentForm()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Fifa, 1600, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Fifa, 1300, targetDate.AddDays(-1))
        ]);
        var results = RatingGatedResults();

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.Contains(comparison.Evaluations, evaluation => evaluation.ModelName == "Ranking FIFA");
        Assert.DoesNotContain(comparison.Evaluations, evaluation => evaluation.ModelName == "Elo");
        Assert.DoesNotContain(comparison.Evaluations, evaluation => evaluation.ModelName == "Forma reciente");
    }

    [Fact]
    public void Compare_FutureOnlySnapshotsDoNotEnableRatingAwareModels()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(1)),
            Rating("b", RatingTypeEnum.Elo, 1400, targetDate.AddDays(1)),
            Rating("a", RatingTypeEnum.Fifa, 1600, targetDate.AddDays(1)),
            Rating("b", RatingTypeEnum.Fifa, 1300, targetDate.AddDays(1))
        ]);
        var results = RatingGatedResults();

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.Equal(["Modelo base", "Modelo de goles (Poisson)"], comparison.Evaluations
            .Select(evaluation => evaluation.ModelName)
            .OrderBy(modelName => modelName, StringComparer.Ordinal));
    }

    [Fact]
    public void InMemoryRatingSnapshotProvider_SelectsLatestRatingsAsOfTargetDate()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1400, targetDate.AddDays(-10)),
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(-1)),
            Rating("a", RatingTypeEnum.Elo, 2000, targetDate.AddDays(1)),
            Rating("b", RatingTypeEnum.Elo, 1300, targetDate.AddDays(-2)),
            Rating("a", RatingTypeEnum.Fifa, 1550, targetDate),
            Rating("b", RatingTypeEnum.Fifa, 1250, targetDate.AddDays(2))
        ]);

        var snapshot = provider.GetSnapshot(targetDate, "a", "b");

        Assert.NotNull(snapshot);
        Assert.Equal(1500, snapshot.HomeElo?.Value);
        Assert.Equal(1300, snapshot.AwayElo?.Value);
        Assert.Equal(1550, snapshot.HomeFifaRank?.Value);
        Assert.Null(snapshot.AwayFifaRank);
        Assert.All(new[] { snapshot.HomeElo, snapshot.AwayElo, snapshot.HomeFifaRank }.Where(rating => rating is not null),
            rating => Assert.True(rating!.AsOf <= targetDate));
    }

    [Fact]
    public void BuildPredictionPoints_RatingAwareStrategyReceivesTargetDateSnapshots()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var futureDate = DateTimeOffset.Parse("2024-01-04T00:00:00Z");
        var provider = new StubRatingSnapshotProvider(new Dictionary<DateTimeOffset, BacktestRatingSnapshot>
        {
            [targetDate] = EloSnapshot("a", 1500, "b", 1400, targetDate.AddDays(-1)),
            [futureDate] = EloSnapshot("a", 2000, "b", 2100, futureDate)
        });
        var seen = new List<(string FixtureId, double? HomeElo, double? AwayElo, DateTimeOffset? HomeAsOf)>();
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("future", "a", "b", "2024-01-04", 0, 1)
        };
        var strategy = new BacktestModelStrategy(
            "rating-spy",
            _ => new RatingSpyPredictor("rating-spy", seen));

        new RollingBacktestService(provider).BuildPredictionPoints(
            results,
            strategy,
            minimumPriorMatchesPerTeam: 1);

        Assert.Equal([targetDate, futureDate], provider.Requests.Select(request => request.TargetDate));
        Assert.Equal("backtest:target", seen[0].FixtureId);
        Assert.Equal(1500, seen[0].HomeElo);
        Assert.Equal(1400, seen[0].AwayElo);
        Assert.Equal(targetDate.AddDays(-1), seen[0].HomeAsOf);
        Assert.Equal("backtest:future", seen[1].FixtureId);
        Assert.Equal(2000, seen[1].HomeElo);
        Assert.Equal(2100, seen[1].AwayElo);
    }

    [Fact]
    public void BuildPredictionPoints_MissingSnapshotsMakeRatingAwareModelDegradeExplicitly()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };
        var strategy = new BacktestModelStrategy("Forma reciente", _ => new RecentFormModel());

        var point = Assert.Single(new RollingBacktestService().BuildPredictionPoints(
            results,
            strategy,
            minimumPriorMatchesPerTeam: 1));

        Assert.True(point.Prediction.Degraded);
        Assert.Equal(OutcomeProbabilities.Uniform, point.Prediction.Outcome);
        Assert.Contains("ratings Elo", point.Prediction.Explanation);
    }

    [Fact]
    public void BuildPredictionPoints_FutureDatedSnapshotsAreNotInjected()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new StubRatingSnapshotProvider(new Dictionary<DateTimeOffset, BacktestRatingSnapshot>
        {
            [targetDate] = EloSnapshot("a", 1500, "b", 1400, targetDate.AddDays(1))
        });
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };
        var strategy = new BacktestModelStrategy("Elo", _ => new EloModel());

        var point = Assert.Single(new RollingBacktestService(provider).BuildPredictionPoints(
            results,
            strategy,
            minimumPriorMatchesPerTeam: 1));

        Assert.True(point.Prediction.Degraded);
        Assert.Equal(OutcomeProbabilities.Uniform, point.Prediction.Outcome);
        Assert.Contains("Faltan ratings Elo", point.Prediction.Explanation);
    }

    [Fact]
    public void Compare_CoverageInfoCountsEligibleTargetsCorrectly()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("future", "a", "b", "2024-01-04", 0, 1)
        };

        var comparison = new RollingBacktestService().Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.NotNull(comparison.Coverage);
        Assert.Equal(2, comparison.Coverage.EligibleTargets);
        Assert.Equal(0, comparison.Coverage.EloCoveredTargets);
        Assert.Equal(0, comparison.Coverage.FifaCoveredTargets);
        Assert.False(comparison.Coverage.EloEnabled);
        Assert.False(comparison.Coverage.FifaEnabled);
        Assert.False(comparison.Coverage.RecentFormEnabled);
    }

    [Fact]
    public void Compare_CoverageInfoReflectsEloFifaCoverage()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Elo, 1400, targetDate.AddDays(-1)),
            Rating("a", RatingTypeEnum.Fifa, 1600, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Fifa, 1300, targetDate.AddDays(-1))
        ]);
        var results = RatingGatedResults();

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.NotNull(comparison.Coverage);
        Assert.Equal(1, comparison.Coverage.EligibleTargets);
        Assert.Equal(1, comparison.Coverage.EloCoveredTargets);
        Assert.Equal(1, comparison.Coverage.FifaCoveredTargets);
        Assert.True(comparison.Coverage.EloEnabled);
        Assert.True(comparison.Coverage.FifaEnabled);
        Assert.True(comparison.Coverage.RecentFormEnabled);
    }

    [Fact]
    public void Compare_CoverageInfoComputedEvenWithCustomStrategies()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };
        var strategies = new[] { Strategy("custom", OutcomeProbabilities.Uniform) };

        var comparison = new RollingBacktestService().Compare(
            results, strategies, minimumPriorMatchesPerTeam: 1);

        Assert.NotNull(comparison.Coverage);
        Assert.Equal(1, comparison.Coverage.EligibleTargets);
    }

    [Fact]
    public void Compare_CoverageInfoReflectsTargetFilter()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("future", "a", "b", "2024-01-04", 0, 1)
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            minimumPriorMatchesPerTeam: 1,
            targetFilter: result => result.Id == "target");

        Assert.NotNull(comparison.Coverage);
        Assert.Equal(1, comparison.Coverage.EligibleTargets);
    }

    [Fact]
    public void Compare_CoverageInfoShowsEloOnlyWhenFifaSnapshotsMissing()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Elo, 1400, targetDate.AddDays(-1))
        ]);
        var results = RatingGatedResults();

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        Assert.NotNull(comparison.Coverage);
        Assert.True(comparison.Coverage.EloEnabled);
        Assert.True(comparison.Coverage.RecentFormEnabled);
        Assert.False(comparison.Coverage.FifaEnabled);
        Assert.Equal(1, comparison.Coverage.EloCoveredTargets);
        Assert.Equal(0, comparison.Coverage.FifaCoveredTargets);
    }

    [Fact]
    public void Compare_SignalBackedAndDegradedCountsTrackDegradedPredictions()
    {
        var targetDate = DateTimeOffset.Parse("2024-01-03T00:00:00Z");
        var futureDate = DateTimeOffset.Parse("2024-01-04T00:00:00Z");
        var provider = new InMemoryBacktestRatingSnapshotProvider(
        [
            Rating("a", RatingTypeEnum.Elo, 1500, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Elo, 1400, targetDate.AddDays(-1)),
            Rating("d", RatingTypeEnum.Elo, 1450, targetDate.AddDays(-1)),
            // FIFA covers the first evaluated target, but not the future target with team d.
            Rating("a", RatingTypeEnum.Fifa, 1600, targetDate.AddDays(-1)),
            Rating("b", RatingTypeEnum.Fifa, 1500, targetDate.AddDays(-1))
        ]);
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("second-d", "d", "c", "2024-01-02", 1, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0),
            Result("future", "a", "d", "2024-01-04", 0, 1)
        };

        var comparison = new RollingBacktestService(provider).Compare(results, minimumPriorMatchesPerTeam: 1);

        var eloSummary = comparison.Summaries.Single(summary => summary.ModelName == "Elo");
        Assert.Equal(2, eloSummary.Count);
        Assert.Equal(2, eloSummary.SignalBackedCount);
        Assert.Equal(0, eloSummary.DegradedCount);

        var fifaSummary = comparison.Summaries.Single(summary => summary.ModelName == "Ranking FIFA");
        Assert.Equal(2, fifaSummary.Count);
        Assert.Equal(1, fifaSummary.SignalBackedCount);
        Assert.Equal(1, fifaSummary.DegradedCount);

        var baseSummary = comparison.Summaries.Single(summary => summary.ModelName == "Modelo base");
        Assert.Equal(2, baseSummary.Count);
        Assert.Equal(2, baseSummary.SignalBackedCount);
        Assert.Equal(0, baseSummary.DegradedCount);
    }

    [Fact]
    public void Compare_NonDegradedPredictionsAreSignalBacked()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };

        var strategies = new[]
        {
            new BacktestModelStrategy("Modelo base", _ => new NullModel(), ReusePredictorForSamePriorResults: true)
        };

        var comparison = new RollingBacktestService().Compare(results, strategies, minimumPriorMatchesPerTeam: 1);

        var summary = Assert.Single(comparison.Summaries);
        Assert.Equal("Modelo base", summary.ModelName);
        Assert.Equal(summary.Count, summary.SignalBackedCount);
        Assert.Equal(0, summary.DegradedCount);
        Assert.Equal(100.0, summary.ReadinessPct, precision: 3);
    }

    [Fact]
    public void Compare_RecentFormDegradesWhenEloIsMissing()
    {
        var results = new[]
        {
            Result("first", "c", "a", "2024-01-01", 1, 1),
            Result("second", "b", "c", "2024-01-02", 2, 0),
            Result("target", "a", "b", "2024-01-03", 1, 0)
        };
        var strategies = new[]
        {
            new BacktestModelStrategy("Forma reciente", _ => new RecentFormModel(), ReusePredictorForSamePriorResults: true)
        };

        var comparison = new RollingBacktestService().Compare(
            results,
            strategies,
            minimumPriorMatchesPerTeam: 1);

        var summary = Assert.Single(comparison.Summaries);
        Assert.Equal("Forma reciente", summary.ModelName);
        Assert.Equal(0, summary.SignalBackedCount);
        Assert.Equal(1, summary.DegradedCount);
        Assert.Equal(0.0, summary.ReadinessPct, precision: 3);
    }

    private static MatchResult Result(
        string id,
        string homeTeamId,
        string awayTeamId,
        string date,
        int homeGoals,
        int awayGoals,
        string tournament = "test") => new()
    {
        Id = id,
        HomeTeamId = homeTeamId,
        AwayTeamId = awayTeamId,
        HomeGoals = homeGoals,
        AwayGoals = awayGoals,
        Date = DateTimeOffset.Parse($"{date}T00:00:00Z"),
        Tournament = tournament,
        Neutral = true,
        Source = "test"
    };

    private static BacktestModelStrategy Strategy(string name, OutcomeProbabilities outcome) =>
        new(name, _ => new FixedPredictor(name, outcome));

    private static BacktestModelStrategy RecordingStrategy(
        string name,
        IDictionary<string, List<string[]>> seen) =>
        new(name, priorResults => new RecordingPredictor(
            name,
            OutcomeProbabilities.Uniform,
            seen,
            priorResults.Select(result => result.Id).ToArray()));

    private static MatchResult[] RatingGatedResults() =>
    [
        Result("first", "c", "a", "2024-01-01", 1, 1),
        Result("second", "b", "c", "2024-01-02", 2, 0),
        Result("target", "a", "b", "2024-01-03", 1, 0)
    ];

    private static BacktestRatingSnapshot EloSnapshot(
        string homeTeamId,
        double homeElo,
        string awayTeamId,
        double awayElo,
        DateTimeOffset asOf) =>
        new(
            HomeElo: Rating(homeTeamId, RatingTypeEnum.Elo, homeElo, asOf),
            AwayElo: Rating(awayTeamId, RatingTypeEnum.Elo, awayElo, asOf));

    private static Rating Rating(string teamId, RatingTypeEnum type, double value, DateTimeOffset asOf) => new()
    {
        TeamId = teamId,
        Type = type,
        Value = value,
        AsOf = asOf,
        Source = "test"
    };

    private sealed class FixedPredictor(string name, OutcomeProbabilities outcome) : IPredictor
    {
        public string Name => name;
        public int Priority => 0;

        public MatchPrediction Predict(MatchContext context) => new()
        {
            PredictorName = name,
            PredictorPriority = Priority,
            FixtureId = context.Fixture.Id,
            HomeTeamId = context.HomeTeamId,
            AwayTeamId = context.AwayTeamId,
            Outcome = outcome
        };
    }

    private sealed class RecordingPredictor(
        string name,
        OutcomeProbabilities outcome,
        IDictionary<string, List<string[]>> seen,
        string[] priorIds) : IPredictor
    {
        public string Name => name;
        public int Priority => 0;

        public MatchPrediction Predict(MatchContext context)
        {
            if (!seen.TryGetValue(context.Fixture.Id, out var fixtureSeen))
            {
                fixtureSeen = [];
                seen[context.Fixture.Id] = fixtureSeen;
            }

            fixtureSeen.Add(priorIds);

            return new MatchPrediction
            {
                PredictorName = name,
                PredictorPriority = Priority,
                FixtureId = context.Fixture.Id,
                HomeTeamId = context.HomeTeamId,
                AwayTeamId = context.AwayTeamId,
                Outcome = outcome
            };
        }
    }

    private sealed class RatingSpyPredictor(
        string name,
        IList<(string FixtureId, double? HomeElo, double? AwayElo, DateTimeOffset? HomeAsOf)> seen) : IPredictor
    {
        public string Name => name;
        public int Priority => 0;

        public MatchPrediction Predict(MatchContext context)
        {
            seen.Add((
                context.Fixture.Id,
                context.HomeElo?.Value,
                context.AwayElo?.Value,
                context.HomeElo?.AsOf));

            return new MatchPrediction
            {
                PredictorName = name,
                PredictorPriority = Priority,
                FixtureId = context.Fixture.Id,
                HomeTeamId = context.HomeTeamId,
                AwayTeamId = context.AwayTeamId,
                Outcome = OutcomeProbabilities.Uniform
            };
        }
    }

    private sealed class StubRatingSnapshotProvider(
        IReadOnlyDictionary<DateTimeOffset, BacktestRatingSnapshot> snapshots) : IBacktestRatingSnapshotProvider
    {
        public bool HasAsOfSnapshotPair(RatingTypeEnum type, DateTimeOffset targetDate, string homeTeamId, string awayTeamId)
        {
            var snapshot = snapshots.GetValueOrDefault(targetDate);
            var homeRating = RatingFor(snapshot, type, home: true);
            var awayRating = RatingFor(snapshot, type, home: false);

            return homeRating is not null &&
                awayRating is not null &&
                homeRating.TeamId == homeTeamId &&
                awayRating.TeamId == awayTeamId &&
                homeRating.AsOf <= targetDate &&
                awayRating.AsOf <= targetDate;
        }

        public List<(DateTimeOffset TargetDate, string HomeTeamId, string AwayTeamId)> Requests { get; } = [];

        public BacktestRatingSnapshot? GetSnapshot(DateTimeOffset targetDate, string homeTeamId, string awayTeamId)
        {
            Requests.Add((targetDate, homeTeamId, awayTeamId));
            return snapshots.GetValueOrDefault(targetDate);
        }

        private static Rating? RatingFor(BacktestRatingSnapshot? snapshot, RatingTypeEnum type, bool home) => type switch
        {
            RatingTypeEnum.Elo => home ? snapshot?.HomeElo : snapshot?.AwayElo,
            RatingTypeEnum.Fifa => home ? snapshot?.HomeFifaRank : snapshot?.AwayFifaRank,
            _ => null
        };
    }
}
