using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Oloraculo.Web;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.ApiFootballModels;
using Oloraculo.Web.Models.CsvModels;
using Oloraculo.Web.Predictors;
using Oloraculo.Web.Probability;
using Oloraculo.Web.Services;
using Oloraculo.Web.Services.Simulation;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Oloraculo.Web.Tests;

public class PredictorTests : TestFixtures
{
    [Fact]
    public void GoalModel_ProducesUsableScorelineWhenTeamsHaveEnoughHistory()
    {
        var model = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);

        var prediction = model.Predict(TestContext());

        Assert.False(prediction.Degraded);
        Assert.NotNull(prediction.Scoreline);
        Assert.True(prediction.ExpectedHomeGoals > 0.1);
        Assert.True(prediction.Outcome.IsValid);
    }

    [Fact]
    public void GoalModel_ExtremeHistoryKeepsExpectedGoalsBoundedAndValid()
    {
        var now = DateTimeOffset.UtcNow;
        var results = Enumerable.Range(0, 6)
            .Select(i => new MatchResult
            {
                Id = $"extreme-{i}",
                HomeTeamId = "a",
                AwayTeamId = "b",
                HomeGoals = 20,
                AwayGoals = 0,
                Date = now.AddDays(-i),
                Tournament = "test",
                Neutral = true,
                Source = "test"
            })
            .ToList();
        var model = new GoalModel(results);

        var (homeGoals, awayGoals, degraded) = model.ExpectedGoals(TestContext());
        var prediction = model.Predict(TestContext());

        Assert.False(degraded);
        Assert.InRange(homeGoals, 0.1, 5.5);
        Assert.InRange(awayGoals, 0.1, 5.5);
        Assert.True(double.IsFinite(homeGoals));
        Assert.True(double.IsFinite(awayGoals));
        Assert.True(prediction.Outcome.IsValid);
    }

    [Fact]
    public void ContextModel_DoesNotClaimLineupsOrOddsWereUsedWithoutConversionLogic()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            HasLineups = true,
            HasOdds = true
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(context);

        Assert.DoesNotContain(nameof(FeaturesEnum.Lineups), prediction.FeaturesUsed);
        Assert.DoesNotContain(nameof(FeaturesEnum.Odds), prediction.FeaturesUsed);
        Assert.Contains("modelo de impacto de alineaciones", prediction.FeaturesMissing);
        Assert.Contains("calibración por cuotas", prediction.FeaturesMissing);
        Assert.True(prediction.Degraded);
    }

    [Fact]
    public void ContextModel_BecomesUsableWhenAvailabilityActuallyAdjustsGoals()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 2
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(context);

        Assert.False(prediction.Degraded);
        Assert.Contains("Disponibilidad de jugadores", prediction.FeaturesUsed);
    }

    [Fact]
    public void FinalSelector_ChoosesHighestUsableRungWithoutAveraging()
    {
        var form = Prediction(3, "Forma reciente", .05, .05, .90);
        var goal = Prediction(4, "Goal", .90, .05, .05, scoreline: ProbabilityHelper.PoissonScoreline(3.0, .4));
        var context = Prediction(5, "Context", .10, .80, .10, degraded: true, missing: ["availability"]);

        var final = FinalPredictionSelector.Select([form, goal, context]);

        Assert.Equal("Oráculo final", final.PredictorName);
        Assert.Equal(4, final.PredictorPriority);
        Assert.Equal(goal.Outcome, final.Outcome);
        Assert.NotEqual(.475, final.Outcome.HomeWin, 3);
    }

    [Fact]
    public void FinalSelector_AppliesLightRankingBiasWhenEloAndFifaAgreeAgainstSelected()
    {
        var fifa = Prediction(1, "Ranking FIFA", .15, .20, .65, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goalScoreline = ProbabilityHelper.PoissonScoreline(1.4, 1.1);
        var goal = Prediction(4, "Goal", .45, .35, .20, scoreline: goalScoreline);

        var final = FinalPredictionSelector.Select([fifa, elo, goal]);

        Assert.Equal("Oráculo final", final.PredictorName);
        Assert.Equal(4, final.PredictorPriority);
        Assert.Equal(.40125, final.Outcome.HomeWin, 5);
        Assert.Equal(.3275, final.Outcome.Draw, 5);
        Assert.Equal(.27125, final.Outcome.AwayWin, 5);
        Assert.Same(goalScoreline, final.Scoreline);
        Assert.Contains(final.Drivers, d => d.Contains("calibración Elo/FIFA"));
        Assert.Contains("calibración Elo/FIFA", final.Explanation);
        Assert.Contains(SourceMetadata.FifaRankings, final.Sources);
        Assert.Contains(SourceMetadata.EloRatings, final.Sources);
    }

    [Fact]
    public void FinalSelector_DoesNotApplyRankingBiasWhenRankingModelsDisagree()
    {
        var fifa = Prediction(1, "Ranking FIFA", .65, .20, .15, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goal = Prediction(4, "Goal", .45, .35, .20);

        var final = FinalPredictionSelector.Select([fifa, elo, goal]);

        Assert.Equal(goal.Outcome, final.Outcome);
        Assert.DoesNotContain(final.Drivers, d => d.Contains("calibración Elo/FIFA"));
        Assert.DoesNotContain(SourceMetadata.FifaRankings, final.Sources);
        Assert.DoesNotContain(SourceMetadata.EloRatings, final.Sources);
    }

    [Fact]
    public void FinalSelector_DoesNotApplyRankingBiasWhenRankingModelIsDegraded()
    {
        var fifa = Prediction(1, "Ranking FIFA", .15, .20, .65, degraded: true, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goal = Prediction(4, "Goal", .45, .35, .20);

        var final = FinalPredictionSelector.Select([fifa, elo, goal]);

        Assert.Equal(goal.Outcome, final.Outcome);
        Assert.DoesNotContain(final.Drivers, d => d.Contains("calibración Elo/FIFA"));
        Assert.DoesNotContain(SourceMetadata.FifaRankings, final.Sources);
        Assert.DoesNotContain(SourceMetadata.EloRatings, final.Sources);
    }

    [Fact]
    public void CriteriaBuilder_InventoriesLadderAndSelectorCorrectly()
    {
        var fifa = Prediction(1, "Ranking FIFA", .65, .20, .15,
            featuresUsed: ["Puntos FIFA del equipo A", "Puntos FIFA del equipo B"],
            sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70,
            featuresUsed: ["Elo del equipo A", "Elo del equipo B"],
            sources: [SourceMetadata.EloRatings]);
        var goal = Prediction(4, "Goal", .45, .35, .20,
            featuresUsed: ["Fuerza de ataque ajustada por rival", "Vulnerabilidad defensiva ajustada por rival", "Grilla de marcadores Dixon-Coles"]);
        var ladder = new List<MatchPrediction> { fifa, elo, goal };
        var best = FinalPredictionSelector.Select(ladder);
        var context = TestContext();

        var criteria = PredictionCriteriaBuilder.Build(context, ladder, best);

        Assert.Equal("Goal", criteria.SelectedPredictorName);
        Assert.False(criteria.HasRankingBias);
        Assert.Contains(criteria.Applied, s => s.Name == "Modelo de goles (Poisson/Dixon-Coles)");
        Assert.Contains(criteria.Available, s => s.Name == "Ratings Elo");
        Assert.Contains(criteria.Missing, s => s.Name == "Ranking FIFA");
        Assert.DoesNotContain(criteria.Applied, s => s.Category == SignalCategory.Odds);
        Assert.DoesNotContain(criteria.Applied, s => s.Category == SignalCategory.Lineups);
    }

    [Fact]
    public void CriteriaBuilder_LineupsAndOddsAreNeverApplied()
    {
        var goal = Prediction(4, "Goal", .45, .35, .20,
            featuresUsed: ["Fuerza de ataque ajustada por rival"]);
        var ladder = new List<MatchPrediction> { goal };
        var best = FinalPredictionSelector.Select(ladder);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            HasLineups = true,
            HasOdds = true
        });

        var criteria = PredictionCriteriaBuilder.Build(context, ladder, best);

        var lineups = criteria.Signals.Single(s => s.Name == "Alineaciones");
        Assert.Equal(SignalStatus.Available, lineups.Status);
        Assert.Contains("sin modelo de conversión", lineups.Detail);

        var odds = criteria.Signals.Single(s => s.Name == "Cuotas (odds)");
        Assert.Equal(SignalStatus.Available, odds.Status);
        Assert.Contains("sin modelo de calibración", odds.Detail);

        Assert.DoesNotContain(criteria.Applied, s => s.Category == SignalCategory.Lineups);
        Assert.DoesNotContain(criteria.Applied, s => s.Category == SignalCategory.Odds);
    }

    [Fact]
    public void CriteriaBuilder_LineupsAndOddsMissingWhenNoContext()
    {
        var goal = Prediction(4, "Goal", .45, .35, .20,
            featuresUsed: ["Fuerza de ataque ajustada por rival"]);
        var ladder = new List<MatchPrediction> { goal };
        var best = FinalPredictionSelector.Select(ladder);
        var context = TestContext(); // No FixtureContext

        var criteria = PredictionCriteriaBuilder.Build(context, ladder, best);

        var lineups = criteria.Signals.Single(s => s.Name == "Alineaciones");
        Assert.Equal(SignalStatus.Missing, lineups.Status);

        var odds = criteria.Signals.Single(s => s.Name == "Cuotas (odds)");
        Assert.Equal(SignalStatus.Missing, odds.Status);
    }

    [Fact]
    public void CriteriaBuilder_RankingBiasAppearsWhenApplied()
    {
        var fifa = Prediction(1, "Ranking FIFA", .15, .20, .65, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goal = Prediction(4, "Goal", .45, .35, .20);
        var ladder = new List<MatchPrediction> { fifa, elo, goal };
        var best = FinalPredictionSelector.Select(ladder);
        var context = TestContext();

        var criteria = PredictionCriteriaBuilder.Build(context, ladder, best);

        Assert.True(criteria.HasRankingBias);
        Assert.Contains(criteria.Applied, s => s.Name == "Calibración Elo/FIFA");
        Assert.Contains(criteria.Applied, s => s.Name == "Ratings Elo");
        Assert.Contains(criteria.Applied, s => s.Name == "Ranking FIFA");
    }

    [Fact]
    public void CriteriaBuilder_PlayerAvailabilityAppearsWhenContextHasPlayers()
    {
        var ctxPred = Prediction(5, "Context", .10, .80, .10,
            featuresUsed: ["Modelo de goles", "Disponibilidad de jugadores",
                "Fuerza de ataque ajustada por rival", "Vulnerabilidad defensiva ajustada por rival",
                "Grilla de marcadores Dixon-Coles"]);
        var ladder = new List<MatchPrediction> { ctxPred };
        var best = FinalPredictionSelector.Select(ladder);
        var ctx = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 2,
            UnavailableAwayPlayers = 1
        });

        var criteria = PredictionCriteriaBuilder.Build(ctx, ladder, best);

        var playerSignal = criteria.Signals.Single(s => s.Name == "Disponibilidad de jugadores");
        Assert.Equal(SignalStatus.Applied, playerSignal.Status);
        Assert.Contains("Bajas: equipo A 2, equipo B 1", playerSignal.Detail);
    }

}
