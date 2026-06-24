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

public class CsvImportServiceTests : TestFixtures
{
    [Fact]
    public async Task CsvImport_CreatesTeamsGroupsFixturesRatingsAndResults()
    {
        await using var db = await NewDb();
        var importer = new CsvImportService(db, new TestEnvironment(WebProjectRoot()));

        var report = await importer.ImportAllAsync();

        Assert.True(report.Teams >= 48);
        Assert.Equal(12, report.Groups);
        Assert.Equal(72, report.Fixtures);
        Assert.True(report.Ratings > 0);
        Assert.True(report.Results > 0);
        Assert.Equal(ExpectedUniqueHistoricalResultIds(), report.Results);
        Assert.DoesNotContain(await db.Fixtures.ToListAsync(), f => string.IsNullOrWhiteSpace(f.Group));
    }

    [Fact]
    public async Task CsvImport_ReimportPreservesManualResults()
    {
        await using var db = await NewDb();
        var importer = new CsvImportService(db, new TestEnvironment(WebProjectRoot()));
        await importer.ImportAllAsync();
        db.Results.Add(new MatchResult
        {
            Id = "manual-result",
            HomeTeamId = "argentina",
            AwayTeamId = "brasil",
            HomeGoals = 2,
            AwayGoals = 1,
            Date = DateTimeOffset.Parse("2026-06-11T00:00:00Z"),
            Tournament = "FIFA World Cup 2026",
            Neutral = true,
            Source = "manual"
        });
        await db.SaveChangesAsync();

        var report = await importer.ImportAllAsync();

        var manual = Assert.Single(await db.Results.Where(r => r.Source == "manual").ToListAsync());
        Assert.Equal("manual-result", manual.Id);
        Assert.Equal(2, manual.HomeGoals);
        Assert.Equal(ExpectedUniqueHistoricalResultIds() + 1, report.Results);
    }

    [Fact]
    public async Task CsvImport_ReimportDoesNotDuplicateCsvRowsThatMatchPreservedManualResults()
    {
        await using var db = await NewDb();
        var importer = new CsvImportService(db, new TestEnvironment(WebProjectRoot()));
        await importer.ImportAllAsync();
        var csvResult = await db.Results.AsNoTracking().FirstAsync(r => r.Source == OloraculoDataFiles.HistoricalResultsCsv);
        db.Results.Add(new MatchResult
        {
            Id = "manual-over-csv-result",
            HomeTeamId = csvResult.HomeTeamId,
            AwayTeamId = csvResult.AwayTeamId,
            HomeGoals = csvResult.HomeGoals,
            AwayGoals = csvResult.AwayGoals,
            Date = csvResult.Date,
            Tournament = csvResult.Tournament,
            Neutral = csvResult.Neutral,
            Source = "manual"
        });
        await db.SaveChangesAsync();

        var report = await importer.ImportAllAsync();

        Assert.Equal(ExpectedUniqueHistoricalResultIds(), report.Results);
        Assert.Single(await db.Results.Where(r => r.HomeTeamId == csvResult.HomeTeamId
            && r.AwayTeamId == csvResult.AwayTeamId
            && r.Date == csvResult.Date
            && r.Tournament == csvResult.Tournament).ToListAsync());
        Assert.True(await db.Results.AnyAsync(r => r.Id == "manual-over-csv-result" && r.Source == "manual"));
    }

    [Fact]
    public async Task CsvImport_ReimportPreservesManualFixtureScoreState()
    {
        await using var db = await NewDb();
        var importer = new CsvImportService(db, new TestEnvironment(WebProjectRoot()));
        await importer.ImportAllAsync();
        var fixtureId = Fixture.GenerateFixtureId("A", "mexico", "czechia");
        var fixture = await db.Fixtures.SingleAsync(f => f.Id == fixtureId);
        fixture.IsPlayed = true;
        fixture.HomeGoals = 3;
        fixture.AwayGoals = 2;
        fixture.KickoffUtc = DateTimeOffset.Parse("2026-06-11T00:00:00Z");
        fixture.Venue = "Estadio Azteca";
        fixture.City = "Mexico City";
        fixture.Status = "FT";
        db.Results.Add(new MatchResult
        {
            Id = "manual-fixture-state",
            HomeTeamId = "mexico",
            AwayTeamId = "czechia",
            HomeGoals = 3,
            AwayGoals = 2,
            Date = DateTimeOffset.Parse("2026-06-11T00:00:00Z"),
            Tournament = "FIFA World Cup 2026",
            Neutral = true,
            Source = "manual"
        });
        await db.SaveChangesAsync();

        await importer.ImportAllAsync();

        var reimportedFixture = await db.Fixtures.SingleAsync(f => f.Id == fixtureId);
        Assert.True(reimportedFixture.IsPlayed);
        Assert.Equal(3, reimportedFixture.HomeGoals);
        Assert.Equal(2, reimportedFixture.AwayGoals);
        Assert.Equal(DateTimeOffset.Parse("2026-06-11T00:00:00Z"), reimportedFixture.KickoffUtc);
        Assert.Equal("Estadio Azteca", reimportedFixture.Venue);
        Assert.Equal("Mexico City", reimportedFixture.City);
        Assert.Equal("FT", reimportedFixture.Status);
    }

    private static int ExpectedUniqueHistoricalResultIds()
    {
        var rows = CsvParsingHelper.ReadCsv<HistoricalResultCsvRow>(Path.Combine(WebProjectRoot(), "Data", OloraculoDataFiles.HistoricalResultsCsv));
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!DateTimeOffset.TryParse(row.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ||
                !int.TryParse(row.HomeScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var homeScore) ||
                !int.TryParse(row.AwayScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var awayScore))
            {
                continue;
            }

            var homeId = TeamNameNormalizer.ToId(row.HomeTeam);
            var awayId = TeamNameNormalizer.ToId(row.AwayTeam);
            ids.Add(CryptoUtil.GetSha256($"{homeId}-{awayId}-{date:O}-{row.Tournament}-{homeScore}-{awayScore}"));
        }

        return ids.Count;
    }

}
