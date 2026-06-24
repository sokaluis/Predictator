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

public class EvaluationServiceTests : TestFixtures
{
    [Fact]
    public async Task Evaluation_StoresFixtureLevelKnownResult()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = "Oráculo final",
            InputSummaryHash = "hash",
            PayloadJson = "{}",
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(1, count);
        Assert.True(fixture.IsPlayed);
        Assert.Equal(2, fixture.HomeGoals);
        Assert.Equal(1, fixture.AwayGoals);
    }

    [Fact]
    public async Task Evaluation_UsesSnapshotModelNameForContextAdjustedIdentity()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = MatchPrediction.ContextAdjustedPredictionIdentity,
            InputSummaryHash = "hash",
            PayloadJson = "{}",
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        var evaluation = Assert.Single(await db.Evaluations.ToListAsync());
        Assert.Equal(MatchPrediction.ContextAdjustedPredictionIdentity, evaluation.ModelName);
    }

    [Fact]
    public async Task Evaluation_EvaluatesLatestSnapshotPerModelIdentityForSameFixture()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.AddRange(
            Snapshot("f1", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Oráculo final"),
            Snapshot("f1", DateTimeOffset.Parse("2026-01-01T00:01:00Z"), MatchPrediction.ContextAdjustedPredictionIdentity));
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        var evaluations = await db.Evaluations.ToListAsync();
        Assert.Equal(2, count);
        Assert.Contains(evaluations, e => e.ModelName == "Oráculo final");
        Assert.Contains(evaluations, e => e.ModelName == MatchPrediction.ContextAdjustedPredictionIdentity);
        Assert.Single(await db.Results.ToListAsync());
    }

    [Fact]
    public async Task Evaluation_EvaluatesOnlyLatestSnapshotForSameModelIdentity()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.AddRange(
            Snapshot("f1", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Oráculo final"),
            Snapshot("f1", DateTimeOffset.Parse("2026-01-02T00:00:00Z"), "Oráculo final"));
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        var evaluation = Assert.Single(await db.Evaluations.ToListAsync());
        Assert.Equal(1, count);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T00:00:00Z"), evaluation.PredictedAt);
    }

    [Fact]
    public async Task Evaluation_BulkEvaluatesPlayedFixturesWithPriorSnapshots()
    {
        await using var db = await NewDb();
        db.Fixtures.Add(PlayedFixture("f1", 2, 1));
        db.Snapshots.Add(Snapshot("f1", DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        await db.SaveChangesAsync();

        var report = await new EvaluationService(db).EvaluateUnevaluatedPlayedFixturesAsync();

        Assert.Equal(1, report.Evaluated);
        Assert.Equal(0, report.SkippedAlreadyEvaluated);
        Assert.Equal(0, report.SkippedWithoutSnapshot);
        Assert.Equal(1, await db.Evaluations.CountAsync(e => e.FixtureId == "f1"));
    }

    [Fact]
    public async Task Evaluation_BulkSkipsFixturesWithoutScores()
    {
        await using var db = await NewDb();
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b", IsPlayed = true });
        db.Snapshots.Add(Snapshot("f1"));
        await db.SaveChangesAsync();

        var report = await new EvaluationService(db).EvaluateUnevaluatedPlayedFixturesAsync();

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(0, await db.Evaluations.CountAsync());
    }

    [Fact]
    public async Task Evaluation_BulkSkipsAlreadyEvaluatedFixtures()
    {
        await using var db = await NewDb();
        var predictedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        db.Fixtures.Add(PlayedFixture("f1", 2, 1));
        db.Snapshots.Add(Snapshot("f1", predictedAt));
        db.Evaluations.Add(Evaluation("f1", "Oráculo final", predictedAt));
        await db.SaveChangesAsync();

        var report = await new EvaluationService(db).EvaluateUnevaluatedPlayedFixturesAsync();

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(1, report.SkippedAlreadyEvaluated);
        Assert.Equal(1, await db.Evaluations.CountAsync(e => e.FixtureId == "f1"));
    }

    [Fact]
    public async Task Evaluation_BulkEvaluatesMissingModelIdentityEvenWhenFixtureHasPriorEvaluation()
    {
        await using var db = await NewDb();
        var oraclePredictedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var contextPredictedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z");
        db.Fixtures.Add(PlayedFixture("f1", 2, 1));
        db.Snapshots.AddRange(
            Snapshot("f1", oraclePredictedAt, "Oráculo final"),
            Snapshot("f1", contextPredictedAt, MatchPrediction.ContextAdjustedPredictionIdentity));
        db.Evaluations.Add(Evaluation("f1", "Oráculo final", oraclePredictedAt));
        db.Results.Add(Result("r1"));
        await db.SaveChangesAsync();

        var report = await new EvaluationService(db).EvaluateUnevaluatedPlayedFixturesAsync();

        Assert.Equal(1, report.Evaluated);
        Assert.Equal(0, report.SkippedAlreadyEvaluated);
        Assert.Equal(0, report.SkippedWithoutSnapshot);
        Assert.Equal(2, await db.Evaluations.CountAsync(e => e.FixtureId == "f1"));
        Assert.True(await db.Evaluations.AnyAsync(e => e.ModelName == MatchPrediction.ContextAdjustedPredictionIdentity && e.PredictedAt == contextPredictedAt));
        Assert.Equal(1, await db.Results.CountAsync());
    }

    [Fact]
    public async Task Evaluation_BulkSkipsPlayedFixturesWithoutPriorSnapshots()
    {
        await using var db = await NewDb();
        db.Fixtures.Add(PlayedFixture("f1", 2, 1));
        await db.SaveChangesAsync();

        var report = await new EvaluationService(db).EvaluateUnevaluatedPlayedFixturesAsync();

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(1, report.SkippedWithoutSnapshot);
        Assert.Equal(0, await db.Evaluations.CountAsync());
    }

    [Fact]
    public async Task Evaluation_BulkEvaluationIsIdempotent()
    {
        await using var db = await NewDb();
        db.Fixtures.Add(PlayedFixture("f1", 2, 1));
        db.Snapshots.Add(Snapshot("f1"));
        await db.SaveChangesAsync();
        var service = new EvaluationService(db);

        var first = await service.EvaluateUnevaluatedPlayedFixturesAsync();
        var second = await service.EvaluateUnevaluatedPlayedFixturesAsync();

        Assert.Equal(1, first.Evaluated);
        Assert.Equal(0, second.Evaluated);
        Assert.Equal(1, second.SkippedAlreadyEvaluated);
        Assert.Equal(1, await db.Evaluations.CountAsync(e => e.FixtureId == "f1"));
    }

    [Fact]
    public async Task Evaluation_IsIdempotentForFixtureModelAndPredictedAt()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        var predictedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(Snapshot("f1", predictedAt, "Oráculo final"));
        await db.SaveChangesAsync();
        var service = new EvaluationService(db);

        var first = await service.EvaluateLatestSnapshotAsync(fixture, 2, 1);
        var second = await service.EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, await db.Evaluations.CountAsync(e => e.FixtureId == "f1"));
        Assert.Single(await db.Results.ToListAsync());
    }

    private static string BuildContextAdjustedPayload(string baselinePredictorName = "Oráculo final", string? baselinePredictionIdentity = null, double baselineHomeWin = 0.45, double baselineDraw = 0.25, double baselineAwayWin = 0.30)
    {
        var baselineIdentity = baselinePredictionIdentity is null ? "null" : $"\"{baselinePredictionIdentity}\"";
        return $$"""
        {
            "AdjustmentComparison": {
                "BaselinePrediction": {
                    "PredictorName": "{{baselinePredictorName}}",
                    "PredictionIdentity": {{baselineIdentity}},
                    "Outcome": {
                        "HomeWin": {{BaselineJson(baselineHomeWin)}},
                        "Draw": {{BaselineJson(baselineDraw)}},
                        "AwayWin": {{BaselineJson(baselineAwayWin)}}
                    }
                },
                "AdjustedPrediction": {
                    "PredictorName": "Oráculo final",
                    "PredictionIdentity": "Oráculo final + contexto API",
                    "Outcome": {
                        "HomeWin": 0.60,
                        "Draw": 0.20,
                        "AwayWin": 0.20
                    }
                },
                "BaselineMethodName": "Oráculo final",
                "AdjustedMethodName": "Oráculo final + contexto API",
                "Signals": []
            }
        }
        """;
    }

    private static string BaselineJson(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Evaluation_ContextAdjustedSnapshotWithBaseline_CreatesTwoEvaluations()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = MatchPrediction.ContextAdjustedPredictionIdentity,
            InputSummaryHash = "hash",
            PayloadJson = BuildContextAdjustedPayload(),
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(2, count);
        var evaluations = await db.Evaluations.ToListAsync();
        Assert.Contains(evaluations, e => e.ModelName == MatchPrediction.ContextAdjustedPredictionIdentity);
        Assert.Contains(evaluations, e => e.ModelName == "Oráculo final");
        Assert.Single(await db.Results.ToListAsync());
    }

    [Fact]
    public async Task Evaluation_ContextAdjustedWithBaseline_RerunIsIdempotent()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = MatchPrediction.ContextAdjustedPredictionIdentity,
            InputSummaryHash = "hash",
            PayloadJson = BuildContextAdjustedPayload(),
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();
        var service = new EvaluationService(db);

        var first = await service.EvaluateLatestSnapshotAsync(fixture, 2, 1);
        var second = await service.EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        var evaluations = await db.Evaluations.ToListAsync();
        Assert.Equal(2, evaluations.Count);
        Assert.Single(await db.Results.ToListAsync());
    }

    [Fact]
    public async Task Evaluation_ContextAdjustedWithBaseline_SkipsWhenBaselineModelNameMatchesTopLevel()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        // Baseline PredictionIdentity matches top-level ModelName → skip synthesis
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = "Oráculo final",
            InputSummaryHash = "hash",
            PayloadJson = BuildContextAdjustedPayload(baselinePredictionIdentity: "Oráculo final"),
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(1, count);
        var evaluation = Assert.Single(await db.Evaluations.ToListAsync());
        Assert.Equal("Oráculo final", evaluation.ModelName);
    }

    [Fact]
    public async Task Evaluation_WithoutComparisonPayload_EvaluatesTopLevelOnly()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = "Oráculo final",
            InputSummaryHash = "hash",
            PayloadJson = "{}",
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(1, count);
        var evaluation = Assert.Single(await db.Evaluations.ToListAsync());
        Assert.Equal("Oráculo final", evaluation.ModelName);
    }

    [Fact]
    public async Task Evaluation_ContextAdjustedBaselineDoesNotDuplicateTopLevelEvaluationInSameCall()
    {
        await using var db = await NewDb();
        var predictedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.AddRange(
            Snapshot("f1", predictedAt, "Oráculo final"),
            new PredictionSnapshot
            {
                Kind = "match",
                FixtureId = "f1",
                ModelName = MatchPrediction.ContextAdjustedPredictionIdentity,
                CreatedAt = predictedAt,
                InputSummaryHash = "hash-context",
                PayloadJson = BuildContextAdjustedPayload(),
                Explanation = "test",
                HomeWin = .6,
                Draw = .2,
                AwayWin = .2
            });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        var evaluations = await db.Evaluations.ToListAsync();
        Assert.Equal(2, count);
        Assert.Equal(2, evaluations.Count);
        Assert.Single(evaluations.Where(e => e.ModelName == "Oráculo final"));
        Assert.Single(evaluations.Where(e => e.ModelName == MatchPrediction.ContextAdjustedPredictionIdentity));
    }

    [Theory]
    [InlineData("{ not valid json")]
    [InlineData("{ \"AdjustmentComparison\": { \"BaselinePrediction\": { \"PredictorName\": \"Oráculo final\", \"Outcome\": { \"HomeWin\": 1e9999, \"Draw\": 0.25, \"AwayWin\": 0.25 } }, \"AdjustedPrediction\": {} } }")]
    public async Task Evaluation_MalformedComparisonPayload_EvaluatesTopLevelOnly(string payloadJson)
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = MatchPrediction.ContextAdjustedPredictionIdentity,
            InputSummaryHash = "hash",
            PayloadJson = payloadJson,
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        var evaluation = Assert.Single(await db.Evaluations.ToListAsync());
        Assert.Equal(1, count);
        Assert.Equal(MatchPrediction.ContextAdjustedPredictionIdentity, evaluation.ModelName);
    }

    private static Fixture PlayedFixture(string id, int homeGoals, int awayGoals) => new()
    {
        Id = id,
        Group = "A",
        HomeTeamId = "a",
        AwayTeamId = "b",
        IsPlayed = true,
        HomeGoals = homeGoals,
        AwayGoals = awayGoals,
        NeutralVenue = true
    };

    private static PredictionSnapshot Snapshot(string fixtureId, DateTimeOffset? createdAt = null, string modelName = "Oráculo final") => new()
    {
        Kind = "match",
        FixtureId = fixtureId,
        ModelName = modelName,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        InputSummaryHash = "hash",
        PayloadJson = "{}",
        Explanation = "test",
        HomeWin = .6,
        Draw = .2,
        AwayWin = .2
    };

    private static PredictionEvaluation Evaluation(string fixtureId, string modelName = "Oráculo final", DateTimeOffset? predictedAt = null) => new()
    {
        ModelName = modelName,
        FixtureId = fixtureId,
        HomeTeamId = "a",
        AwayTeamId = "b",
        HomeGoals = 2,
        AwayGoals = 1,
        HomeWin = .6,
        Draw = .2,
        AwayWin = .2,
        Actual = "Home",
        BrierScore = 0,
        RankedProbabilityScore = 0,
        LogLoss = 0,
        TopPickCorrect = true,
        PredictedAt = predictedAt ?? DateTimeOffset.UtcNow
    };

    private static MatchResult Result(string id) => new()
    {
        Id = id,
        HomeTeamId = "a",
        AwayTeamId = "b",
        HomeGoals = 2,
        AwayGoals = 1,
        Date = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        Tournament = "FIFA World Cup 2026",
        Neutral = true,
        Source = "manual"
    };

}
