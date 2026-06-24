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

    [Fact]
    public async Task Evaluation_CorrectingScoreUpdatesManualResultWithoutDuplicate()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b", KickoffUtc = DateTimeOffset.Parse("2026-06-11T00:00:00Z") };
        var predictedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(Snapshot("f1", predictedAt, "Oráculo final"));
        await db.SaveChangesAsync();
        var service = new EvaluationService(db);

        var first = await service.EvaluateLatestSnapshotAsync(fixture, 2, 1);
        var corrected = await service.EvaluateLatestSnapshotAsync(fixture, 1, 3);

        Assert.Equal(1, first);
        Assert.Equal(0, corrected);
        var result = Assert.Single(await db.Results.Where(r => r.Source == "manual").ToListAsync());
        Assert.Equal(1, result.HomeGoals);
        Assert.Equal(3, result.AwayGoals);
        Assert.Equal(DateTimeOffset.Parse("2026-06-11T00:00:00Z"), result.Date);
        Assert.Equal(1, fixture.HomeGoals);
        Assert.Equal(3, fixture.AwayGoals);
        var evaluation = Assert.Single(await db.Evaluations.Where(e => e.FixtureId == "f1").ToListAsync());
        Assert.Equal("Away", evaluation.Actual);
        Assert.False(evaluation.TopPickCorrect);
        Assert.Equal(1, evaluation.HomeGoals);
        Assert.Equal(3, evaluation.AwayGoals);
    }

    [Fact]
    public async Task Evaluation_UpdatesLegacyManualResultWithDifferentDateWithoutDuplicate()
    {
        await using var db = await NewDb();
        var kickoff = DateTimeOffset.Parse("2026-06-11T00:00:00Z");
        var legacyDate = DateTimeOffset.Parse("2026-06-24T00:00:00Z");
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b", KickoffUtc = kickoff };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(Snapshot("f1", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Oráculo final"));
        db.Results.Add(new MatchResult
        {
            Id = "legacy-manual-result",
            HomeTeamId = "a",
            AwayTeamId = "b",
            HomeGoals = 2,
            AwayGoals = 1,
            Date = legacyDate,
            Tournament = "FIFA World Cup 2026",
            Neutral = true,
            Source = "manual"
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 1, 3);

        Assert.Equal(1, count);
        var result = Assert.Single(await db.Results.Where(r => r.Source == "manual").ToListAsync());
        Assert.Equal("legacy-manual-result", result.Id);
        Assert.Equal(1, result.HomeGoals);
        Assert.Equal(3, result.AwayGoals);
        Assert.Equal(kickoff, result.Date);
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

    // ----- Paired comparison tests -----

    [Fact]
    public async Task PairedComparison_ReturnsNullWhenNoPairsExist()
    {
        await using var db = await NewDb();
        db.Evaluations.AddRange(
            Evaluation("f1", "Oráculo final", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            Evaluation("f1", MatchPrediction.ContextAdjustedPredictionIdentity, DateTimeOffset.Parse("2026-01-01T00:01:00Z")));
        await db.SaveChangesAsync();

        var result = await new EvaluationService(db).PairedOracleContextComparisonAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task PairedComparison_IgnoresUnpairedRows()
    {
        await using var db = await NewDb();
        var pairedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var unpairedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z");

        // One pair at pairedAt
        db.Evaluations.AddRange(
            PairedEvaluation("f1", "Oráculo final", pairedAt, brier: 0.2, rps: 0.3, logLoss: 0.4, topPick: true),
            PairedEvaluation("f1", MatchPrediction.ContextAdjustedPredictionIdentity, pairedAt, brier: 0.1, rps: 0.2, logLoss: 0.3, topPick: false));
        // Unpaired extra baseline for f1 at different time
        db.Evaluations.Add(PairedEvaluation("f1", "Oráculo final", unpairedAt, brier: 0.99, rps: 0.99, logLoss: 0.99, topPick: false));
        // Unpaired extra context for another fixture at the paired time
        db.Evaluations.Add(PairedEvaluation("f2", MatchPrediction.ContextAdjustedPredictionIdentity, pairedAt, brier: 0.99, rps: 0.99, logLoss: 0.99, topPick: true));
        await db.SaveChangesAsync();

        var result = await new EvaluationService(db).PairedOracleContextComparisonAsync();

        Assert.NotNull(result);
        Assert.Equal(1, result.PairCount);
        Assert.Equal(0.2, result.BaselineMeanBrier, 3);
        Assert.Equal(0.1, result.ContextMeanBrier, 3);
        Assert.Equal(-0.1, result.DeltaBrier, 3);
        Assert.Equal(0.3, result.BaselineMeanRps, 3);
        Assert.Equal(0.2, result.ContextMeanRps, 3);
        Assert.Equal(-0.1, result.DeltaRps, 3);
        Assert.Equal(0.4, result.BaselineMeanLogLoss, 3);
        Assert.Equal(0.3, result.ContextMeanLogLoss, 3);
        Assert.Equal(-0.1, result.DeltaLogLoss, 3);
        Assert.Equal(1.0, result.BaselineTopPickAccuracy, 3);
        Assert.Equal(0.0, result.ContextTopPickAccuracy, 3);
        Assert.Equal(-1.0, result.DeltaTopPickAccuracy, 3);
    }

    [Fact]
    public async Task PairedComparison_UsesLatestEvaluationWhenDuplicateKeysExist()
    {
        await using var db = await NewDb();
        var pairedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        db.Evaluations.AddRange(
            PairedEvaluation("f1", "Oráculo final", pairedAt, brier: 0.9, rps: 0.9, logLoss: 0.9, topPick: false),
            PairedEvaluation("f1", MatchPrediction.ContextAdjustedPredictionIdentity, pairedAt, brier: 0.8, rps: 0.8, logLoss: 0.8, topPick: false),
            PairedEvaluation("f1", "Oráculo final", pairedAt, brier: 0.2, rps: 0.3, logLoss: 0.4, topPick: true),
            PairedEvaluation("f1", MatchPrediction.ContextAdjustedPredictionIdentity, pairedAt, brier: 0.1, rps: 0.2, logLoss: 0.3, topPick: true));
        await db.SaveChangesAsync();

        var result = await new EvaluationService(db).PairedOracleContextComparisonAsync();

        Assert.NotNull(result);
        Assert.Equal(1, result.PairCount);
        Assert.Equal(0.2, result.BaselineMeanBrier, 3);
        Assert.Equal(0.1, result.ContextMeanBrier, 3);
        Assert.Equal(-0.1, result.DeltaBrier, 3);
        Assert.Equal(1.0, result.BaselineTopPickAccuracy, 3);
        Assert.Equal(1.0, result.ContextTopPickAccuracy, 3);
    }

    [Fact]
    public async Task PairedComparison_ComputesMetricsFromPairedRowsOnly()
    {
        await using var db = await NewDb();
        var t1 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

        // Pair 1: f1 at t1
        db.Evaluations.AddRange(
            PairedEvaluation("f1", "Oráculo final", t1, brier: 0.15, rps: 0.20, logLoss: 0.30, topPick: true),
            PairedEvaluation("f1", MatchPrediction.ContextAdjustedPredictionIdentity, t1, brier: 0.10, rps: 0.15, logLoss: 0.25, topPick: true));
        // Pair 2: f2 at t2
        db.Evaluations.AddRange(
            PairedEvaluation("f2", "Oráculo final", t2, brier: 0.25, rps: 0.30, logLoss: 0.40, topPick: false),
            PairedEvaluation("f2", MatchPrediction.ContextAdjustedPredictionIdentity, t2, brier: 0.20, rps: 0.28, logLoss: 0.35, topPick: true));
        // Unpaired: f3 context-only
        db.Evaluations.Add(PairedEvaluation("f3", MatchPrediction.ContextAdjustedPredictionIdentity, t1, brier: 0.99, rps: 0.99, logLoss: 0.99, topPick: false));
        await db.SaveChangesAsync();

        var result = await new EvaluationService(db).PairedOracleContextComparisonAsync();

        Assert.NotNull(result);
        Assert.Equal(2, result.PairCount);

        // Baseline means
        Assert.Equal(0.20, result.BaselineMeanBrier, 3);    // (0.15+0.25)/2
        Assert.Equal(0.25, result.BaselineMeanRps, 3);      // (0.20+0.30)/2
        Assert.Equal(0.35, result.BaselineMeanLogLoss, 3);  // (0.30+0.40)/2
        Assert.Equal(0.5, result.BaselineTopPickAccuracy, 3); // (1+0)/2

        // Context means
        Assert.Equal(0.15, result.ContextMeanBrier, 3);     // (0.10+0.20)/2
        Assert.Equal(0.215, result.ContextMeanRps, 3);      // (0.15+0.28)/2
        Assert.Equal(0.30, result.ContextMeanLogLoss, 3);   // (0.25+0.35)/2
        Assert.Equal(1.0, result.ContextTopPickAccuracy, 3); // (1+1)/2

        // Deltas (context - baseline)
        Assert.Equal(-0.05, result.DeltaBrier, 3);
        Assert.Equal(-0.035, result.DeltaRps, 3);
        Assert.Equal(-0.05, result.DeltaLogLoss, 3);
        Assert.Equal(0.5, result.DeltaTopPickAccuracy, 3);
    }

    private static PredictionEvaluation PairedEvaluation(
        string fixtureId, string modelName, DateTimeOffset predictedAt,
        double brier = 0.0, double rps = 0.0, double logLoss = 0.0, bool topPick = true) => new()
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
        BrierScore = brier,
        RankedProbabilityScore = rps,
        LogLoss = logLoss,
        TopPickCorrect = topPick,
        PredictedAt = predictedAt
    };

}
