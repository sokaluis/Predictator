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

public class PredictionServiceTests : TestFixtures
{
    [Fact]
    public async Task PredictionService_BulkPredictsImportedGroupFixtures()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures.AsNoTracking().ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var results = await service.PredictFixturesAsync(fixtures);

        Assert.Equal(72, results.Count);
        Assert.All(results, result =>
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Fixture.Id));
            Assert.True(result.BestPrediction.Outcome.IsValid);
        });
    }

    [Fact]
    public async Task PredictionService_BulkPredictionsMatchSingleFixturePredictions()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures
            .AsNoTracking()
            .OrderBy(f => f.Group)
            .ThenBy(f => f.HomeTeamId)
            .ThenBy(f => f.AwayTeamId)
            .Take(3)
            .ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var bulk = await service.PredictFixturesAsync(fixtures);

        foreach (var fixture in fixtures)
        {
            var expected = await service.PredictFixtureAsync(fixture.Id);
            var actual = bulk.Single(result => result.Fixture.Id == fixture.Id);

            Assert.NotNull(expected);
            AssertPredictionResultEqual(expected, actual);
        }
    }

    [Fact]
    public async Task PredictionService_BulkPredictionUsesFixtureIdsWhenTeamsAreMissing()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "ghost-home", AwayTeamId = "ghost-away" };
        db.Fixtures.Add(fixture);
        await db.SaveChangesAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var result = Assert.Single(await service.PredictFixturesAsync([fixture]));

        Assert.Equal("ghost-home", result.HomeTeamName);
        Assert.Equal("ghost-away", result.AwayTeamName);
        Assert.True(result.BestPrediction.Outcome.IsValid);
    }

    [Fact]
    public async Task PredictionService_ReturnsCriteriaWithoutChangingOutcome()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures.AsNoTracking().Take(5).ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var results = await service.PredictFixturesAsync(fixtures);

        Assert.All(results, result =>
        {
            Assert.NotNull(result.Criteria);
            Assert.NotEmpty(result.Criteria!.Signals);
            Assert.NotNull(result.Criteria.SelectedPredictorName);
            Assert.True(result.BestPrediction.Outcome.IsValid);

            // Lineups/odds must never be applied
            Assert.DoesNotContain(result.Criteria.Applied, s => s.Category == SignalCategory.Lineups);
            Assert.DoesNotContain(result.Criteria.Applied, s => s.Category == SignalCategory.Odds);
        });
    }

    [Fact]
    public async Task PredictionService_ExposesBaselineVsContextAdjustedPrediction()
    {
        await using var db = await NewDb();
        SeedPredictionData(db, includeFixtureContext: true, context =>
        {
            context.UnavailableHomePlayers = 2;
            context.UnavailableHomeAttackImpact = 0.10;
            context.UnavailableAwayDefenseImpact = 0.05;
        });
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var result = await service.PredictFixtureAsync("f1");

        Assert.NotNull(result);
        var comparison = Assert.IsType<PredictionAdjustmentComparison>(result!.AdjustmentComparison);
        Assert.Equal("Oráculo final", comparison.BaselinePrediction.PredictorName);
        Assert.Equal("Modelo de goles (Poisson)", comparison.BaselineMethodName);
        Assert.Equal("Goles + contexto reciente", comparison.AdjustedMethodName);
        Assert.Equal(result.BestPrediction.PredictorName, comparison.AdjustedPrediction.PredictorName);
        Assert.True(comparison.HasExpectedGoalsDelta);
        Assert.Contains(comparison.Signals, signal =>
            signal.Name == "Disponibilidad de jugadores" && signal.Applied && signal.Modeled);
    }

    [Fact]
    public async Task PredictionService_DoesNotExposeComparisonWhenFixtureContextIsMissing()
    {
        await using var db = await NewDb();
        SeedPredictionData(db, includeFixtureContext: false);
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var result = await service.PredictFixtureAsync("f1");

        Assert.NotNull(result);
        Assert.Null(result!.AdjustmentComparison);
    }

    [Fact]
    public async Task PredictionService_ReportsLineupsAndOddsAsAvailableButNotModeled()
    {
        await using var db = await NewDb();
        SeedPredictionData(db, includeFixtureContext: true, context =>
        {
            context.HasLineups = true;
            context.HasOdds = true;
        });
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var result = await service.PredictFixtureAsync("f1");

        Assert.NotNull(result);
        var comparison = Assert.IsType<PredictionAdjustmentComparison>(result!.AdjustmentComparison);
        Assert.False(comparison.HasExpectedGoalsDelta);
        Assert.False(comparison.PickChanged);

        var lineups = comparison.Signals.Single(signal => signal.Name == "Alineaciones");
        Assert.True(lineups.Available);
        Assert.False(lineups.Applied);
        Assert.False(lineups.Modeled);

        var odds = comparison.Signals.Single(signal => signal.Name == "Cuotas (odds)");
        Assert.True(odds.Available);
        Assert.False(odds.Applied);
        Assert.False(odds.Modeled);
    }

    private static void SeedPredictionData(
        OloraculoDbContext db,
        bool includeFixtureContext,
        Action<FixtureContext>? configureContext = null)
    {
        db.Teams.AddRange(
            new Team { Id = "a", Name = "Alpha" },
            new Team { Id = "b", Name = "Beta" });
        db.Fixtures.Add(new Fixture { Id = "f1", HomeTeamId = "a", AwayTeamId = "b", NeutralVenue = true });
        db.Results.AddRange(
            Result("a", "b", 2, 0),
            Result("b", "a", 1, 1),
            Result("a", "b", 3, 1),
            Result("b", "a", 0, 2));

        if (includeFixtureContext)
        {
            var fixtureContext = new FixtureContext
            {
                FixtureId = "f1",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            configureContext?.Invoke(fixtureContext);
            db.FixtureContexts.Add(fixtureContext);
        }

        db.SaveChanges();
    }

}
