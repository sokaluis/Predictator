using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.CsvModels;
using System.Data;
using System.Globalization;

namespace Oloraculo.Web.Services
{
    public class CsvImportService
    {
        private readonly OloraculoDbContext _db;
        private readonly IWebHostEnvironment _environment;

        public CsvImportService(OloraculoDbContext db, IWebHostEnvironment env)
        {
            _db = db;
            _environment = env;
        }

        public async Task ImportIfNeededAsync(CancellationToken ct = default)
        {
            await _db.Database.EnsureCreatedAsync(ct);
            await EnsureFixtureResultColumnsAsync(ct);
            await EnsureAvailabilityTablesAsync(ct);
            await EnsureSnapshotColumnsAsync(ct);

            var needsImport =
                !await _db.Groups.AnyAsync(ct) ||
                !await _db.Teams.AnyAsync(ct) ||
                !await _db.Fixtures.AnyAsync(ct) ||
                !await _db.Results.AnyAsync(ct) ||
                !await _db.Ratings.AnyAsync(ct) ||
                await _db.Fixtures.AnyAsync(f => f.Group == "", ct);

            if (needsImport)
                await ImportAllAsync(ct);
        }

        public async Task<CsvImportReport> ImportAllAsync(CancellationToken ct = default)
        {
            await _db.Database.EnsureCreatedAsync(ct);
            await EnsureFixtureResultColumnsAsync(ct);
            await EnsureAvailabilityTablesAsync(ct);
            await EnsureSnapshotColumnsAsync(ct);
            await ImportGroupsAsync(ct);
            await ImportRatingsAsync(ct);
            await ImportHistoricalResultsAsync(ct);
            await _db.SaveChangesAsync(ct);
            await GenerateFixturesAsync(ct);
            await _db.SaveChangesAsync(ct);

            return new CsvImportReport
            {
                Groups = await _db.Groups.CountAsync(ct),
                Teams = await _db.Teams.CountAsync(ct),
                Ratings = await _db.Ratings.CountAsync(ct),
                Results = await _db.Results.CountAsync(ct),
                Fixtures = await _db.Fixtures.CountAsync(ct),
            };
        }

        public async Task<int> ImportRatingsOnlyAsync(CancellationToken ct = default)
        {
            await _db.Database.EnsureCreatedAsync(ct);
            await EnsureAvailabilityTablesAsync(ct);
            await EnsureSnapshotColumnsAsync(ct);
            await ImportRatingsAsync(ct);
            await _db.SaveChangesAsync(ct);
            return await _db.Ratings.CountAsync(ct);
        }

        private async Task ImportGroupsAsync(CancellationToken ct)
        {
            _db.Groups.RemoveRange(_db.Groups);
            var groupRows = CsvParsingHelper.ReadCsv<GroupCsvRow>(FullPath(OloraculoDataFiles.GroupsCsv));
            var teams = new Dictionary<string, Team>();

            foreach (var row in groupRows)
            {
                var name = TeamNameNormalizer.CanonicalName(row.Team);
                var id = TeamNameNormalizer.ToId(row.Team);
                teams[id] = new Team { Id = id, Name = name, Source = OloraculoDataFiles.GroupsCsv };
            }

            foreach (var team in teams.Values)
            {
                var existing = await _db.Teams.FindAsync([team.Id], ct);
                if (existing is null)
                    _db.Teams.Add(team);
                else
                    existing.Name = team.Name;
            }

            foreach (var group in groupRows.GroupBy(r => r.Group.Trim()).OrderBy(g => g.Key))
            {
                _db.Groups.Add(new Group
                {
                    Name = group.Key,
                    TeamIds = group.Select(r => TeamNameNormalizer.ToId(r.Team)).ToList(),
                    Source = OloraculoDataFiles.GroupsCsv,
                });
            }
        }

        private async Task ImportRatingsAsync(CancellationToken ct)
        {
            _db.Ratings.RemoveRange(_db.Ratings);

            var eloRows = CsvParsingHelper.ReadCsv<EloCsvRow>(FullPath(OloraculoDataFiles.EloCsv));
            foreach (var row in eloRows)
            {
                if (!double.TryParse(row.Elo, NumberStyles.Float, CultureInfo.InvariantCulture, out var elo))
                    continue;

                await CreateTeamIfMissing(row.Team, OloraculoDataFiles.EloCsv, ct);
                _db.Ratings.Add(new Rating
                {
                    TeamId = TeamNameNormalizer.ToId(row.Team),
                    Type = RatingTypeEnum.Elo,
                    Value = elo,
                    AsOf = DateTimeOffset.UtcNow,
                    Source = OloraculoDataFiles.EloCsv
                });
            }

            var fifaRows = CsvParsingHelper.ReadCsv<FifaCsvRow>(FullPath(OloraculoDataFiles.FifaRankingsCsv));
            foreach (var row in fifaRows)
            {
                if (!double.TryParse(row.Points, NumberStyles.Float, CultureInfo.InvariantCulture, out var points))
                    continue;

                await CreateTeamIfMissing(row.Team, OloraculoDataFiles.FifaRankingsCsv, ct);
                _db.Ratings.Add(new Rating
                {
                    TeamId = TeamNameNormalizer.ToId(row.Team),
                    Type = RatingTypeEnum.Fifa,
                    Value = points,
                    AsOf = DateTimeOffset.UtcNow,
                    Source = OloraculoDataFiles.FifaRankingsCsv
                });
            }
        }

        private async Task ImportHistoricalResultsAsync(CancellationToken ct)
        {
            var manualResultKeys = await _db.Results
                .Where(r => r.Source == "manual")
                .Select(r => new ResultIdentity(r.HomeTeamId, r.AwayTeamId, r.Date, r.Tournament))
                .ToListAsync(ct);

            _db.Results.RemoveRange(_db.Results.Where(r => r.Source != "manual"));
            var rows = CsvParsingHelper.ReadCsv<HistoricalResultCsvRow>(FullPath(OloraculoDataFiles.HistoricalResultsCsv));
            var importedIds = new HashSet<string>(StringComparer.Ordinal);
            var manualIdentities = manualResultKeys.ToHashSet();

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
                if (manualIdentities.Contains(new ResultIdentity(homeId, awayId, date, row.Tournament)))
                    continue;

                var resultId = CryptoUtil.GetSha256($"{homeId}-{awayId}-{date:O}-{row.Tournament}-{homeScore}-{awayScore}");

                if (!importedIds.Add(resultId))
                    continue;

                await CreateTeamIfMissing(row.HomeTeam, OloraculoDataFiles.HistoricalResultsCsv, ct);
                await CreateTeamIfMissing(row.AwayTeam, OloraculoDataFiles.HistoricalResultsCsv, ct);

                _db.Results.Add(new MatchResult
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
                });
            }
        }

        private readonly record struct ResultIdentity(string HomeTeamId, string AwayTeamId, DateTimeOffset Date, string Tournament);

        private async Task GenerateFixturesAsync(CancellationToken ct)
        {
            var existingFixtureStates = await _db.Fixtures
                .Select(f => new FixtureState(
                    f.Id,
                    f.IsPlayed,
                    f.HomeGoals,
                    f.AwayGoals,
                    f.KickoffUtc,
                    f.Venue,
                    f.City,
                    f.Status,
                    f.NeutralVenue))
                .ToDictionaryAsync(f => f.FixtureId, ct);
            var manualResults = await _db.Results
                .Where(r => r.Source == "manual" && r.Tournament == "FIFA World Cup 2026")
                .ToListAsync(ct);
            var manualResultStates = manualResults
                .GroupBy(r => new { r.HomeTeamId, r.AwayTeamId })
                .Select(g => g.OrderByDescending(r => r.Date).First())
                .ToDictionary(
                r => (r.HomeTeamId, r.AwayTeamId),
                r => new FixtureState(string.Empty, true, r.HomeGoals, r.AwayGoals, r.Date, null, null, null, r.Neutral));

            _db.Fixtures.RemoveRange(_db.Fixtures);
            var groups = await _db.Groups.AsNoTracking().ToListAsync(ct);

            foreach (var group in groups.OrderBy(g => g.Name))
            {
                var teams = group.TeamIds;
                for (var i = 0; i < teams.Count; i++)
                {
                    for (var j = i + 1; j < teams.Count; j++)
                    {
                        var fixtureId = Fixture.GenerateFixtureId(group.Name, teams[i], teams[j]);
                        var fixture = new Fixture
                        {
                            Id = fixtureId,
                            Group = group.Name,
                            HomeTeamId = teams[i],
                            AwayTeamId = teams[j],
                            NeutralVenue = true,
                            Source = $"derivado de {OloraculoDataFiles.GroupsCsv}"
                        };

                        if (existingFixtureStates.TryGetValue(fixtureId, out var existingState)
                            || manualResultStates.TryGetValue((teams[i], teams[j]), out existingState))
                        {
                            fixture.IsPlayed = existingState.IsPlayed;
                            fixture.HomeGoals = existingState.HomeGoals;
                            fixture.AwayGoals = existingState.AwayGoals;
                            fixture.KickoffUtc = existingState.KickoffUtc;
                            fixture.Venue = existingState.Venue;
                            fixture.City = existingState.City;
                            fixture.Status = existingState.Status;
                            fixture.NeutralVenue = existingState.NeutralVenue;
                        }

                        _db.Fixtures.Add(fixture);
                    }
                }
            }
        }

        private readonly record struct FixtureState(
            string FixtureId,
            bool IsPlayed,
            int? HomeGoals,
            int? AwayGoals,
            DateTimeOffset? KickoffUtc,
            string? Venue,
            string? City,
            string? Status,
            bool NeutralVenue);

        private async Task CreateTeamIfMissing(string name, string sourceFile, CancellationToken ct)
        {
            var canonical = TeamNameNormalizer.CanonicalName(name);
            var id = TeamNameNormalizer.ToId(canonical);
            if (await _db.Teams.FindAsync([id], ct) is null)
                _db.Teams.Add(new Team { Id = id, Name = canonical, Source = sourceFile });
        }

        private async Task EnsureFixtureResultColumnsAsync(CancellationToken ct)
        {
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose)
                await connection.OpenAsync(ct);

            try
            {
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA table_info(\"Fixtures\")";
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        columns.Add(reader.GetString(1));
                }

                if (!columns.Contains("HomeGoals"))
                    await ExecuteSchemaAsync("ALTER TABLE \"Fixtures\" ADD COLUMN \"HomeGoals\" INTEGER NULL", ct);
                if (!columns.Contains("AwayGoals"))
                    await ExecuteSchemaAsync("ALTER TABLE \"Fixtures\" ADD COLUMN \"AwayGoals\" INTEGER NULL", ct);
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }

            async Task ExecuteSchemaAsync(string sql, CancellationToken token)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }
        }

        private async Task EnsureAvailabilityTablesAsync(CancellationToken ct)
        {
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose)
                await connection.OpenAsync(ct);

            try
            {
                await ExecuteSchemaAsync("""
                    CREATE TABLE IF NOT EXISTS "AvailabilitySources" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_AvailabilitySources" PRIMARY KEY AUTOINCREMENT,
                        "Url" TEXT NOT NULL,
                        "Title" TEXT NULL,
                        "Publisher" TEXT NULL,
                        "StatusCode" INTEGER NOT NULL,
                        "TextHash" TEXT NULL,
                        "LastFetchedAt" TEXT NOT NULL,
                        "Error" TEXT NULL
                    )
                    """, ct);
                await ExecuteSchemaAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_AvailabilitySources_Url" ON "AvailabilitySources" ("Url")""", ct);

                await ExecuteSchemaAsync($"""
                    CREATE TABLE IF NOT EXISTS "AvailabilityClaims" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_AvailabilityClaims" PRIMARY KEY AUTOINCREMENT,
                        "Player" TEXT NOT NULL,
                        "PlayerKey" TEXT NOT NULL,
                        "TeamId" TEXT NOT NULL,
                        "TeamName" TEXT NOT NULL,
                        "Status" INTEGER NOT NULL,
                        "Reason" TEXT NOT NULL,
                        "Confidence" TEXT NOT NULL,
                        "EvidenceLevel" INTEGER NOT NULL,
                        "SourceUrl" TEXT NOT NULL,
                        "Publisher" TEXT NULL,
                        "SupportingQuote" TEXT NOT NULL,
                        "ObservedDate" TEXT NULL,
                        "AffectsPrediction" INTEGER NOT NULL,
                        "ApiFootballPlayerId" INTEGER NULL,
                        "Position" TEXT NOT NULL DEFAULT '{PlayerPositions.Unknown}',
                        "PositionSource" TEXT NOT NULL DEFAULT '{PlayerPositions.Unknown}',
                        "PositionMatchedAt" TEXT NULL,
                        "AttackImpact" REAL NULL,
                        "DefenseImpact" REAL NULL,
                        "ImpactSource" TEXT NOT NULL DEFAULT '{PlayerImpactSources.Position}',
                        "WeightedInternationalGoals" REAL NULL,
                        "ApiGoals" INTEGER NULL,
                        "ApiAssists" INTEGER NULL,
                        "ApiMinutes" INTEGER NULL,
                        "ApiLineups" INTEGER NULL,
                        "ApiRating" REAL NULL,
                        "ImpactMatchedAt" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL
                    )
                    """, ct);
                await ExecuteSchemaAsync("""CREATE INDEX IF NOT EXISTS "IX_AvailabilityClaims_TeamId_PlayerKey_Status_SourceUrl" ON "AvailabilityClaims" ("TeamId", "PlayerKey", "Status", "SourceUrl")""", ct);

                var claimColumns = await ColumnsAsync("AvailabilityClaims", ct);
                if (!claimColumns.Contains("ApiFootballPlayerId"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ApiFootballPlayerId" INTEGER NULL""", ct);
                if (!claimColumns.Contains("Position"))
                    await ExecuteSchemaAsync($"""ALTER TABLE "AvailabilityClaims" ADD COLUMN "Position" TEXT NOT NULL DEFAULT '{PlayerPositions.Unknown}'""", ct);
                if (!claimColumns.Contains("PositionSource"))
                    await ExecuteSchemaAsync($"""ALTER TABLE "AvailabilityClaims" ADD COLUMN "PositionSource" TEXT NOT NULL DEFAULT '{PlayerPositions.Unknown}'""", ct);
                if (!claimColumns.Contains("PositionMatchedAt"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "PositionMatchedAt" TEXT NULL""", ct);
                if (!claimColumns.Contains("AttackImpact"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "AttackImpact" REAL NULL""", ct);
                if (!claimColumns.Contains("DefenseImpact"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "DefenseImpact" REAL NULL""", ct);
                if (!claimColumns.Contains("ImpactSource"))
                    await ExecuteSchemaAsync($"""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ImpactSource" TEXT NOT NULL DEFAULT '{PlayerImpactSources.Position}'""", ct);
                if (!claimColumns.Contains("WeightedInternationalGoals"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "WeightedInternationalGoals" REAL NULL""", ct);
                if (!claimColumns.Contains("ApiGoals"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ApiGoals" INTEGER NULL""", ct);
                if (!claimColumns.Contains("ApiAssists"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ApiAssists" INTEGER NULL""", ct);
                if (!claimColumns.Contains("ApiMinutes"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ApiMinutes" INTEGER NULL""", ct);
                if (!claimColumns.Contains("ApiLineups"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ApiLineups" INTEGER NULL""", ct);
                if (!claimColumns.Contains("ApiRating"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ApiRating" REAL NULL""", ct);
                if (!claimColumns.Contains("ImpactMatchedAt"))
                    await ExecuteSchemaAsync("""ALTER TABLE "AvailabilityClaims" ADD COLUMN "ImpactMatchedAt" TEXT NULL""", ct);

                var fixtureColumns = await ColumnsAsync("FixtureContexts", ct);
                if (fixtureColumns.Count > 0 && !fixtureColumns.Contains("HasAvailabilityNews"))
                    await ExecuteSchemaAsync("""ALTER TABLE "FixtureContexts" ADD COLUMN "HasAvailabilityNews" INTEGER NOT NULL DEFAULT 0""", ct);
                if (fixtureColumns.Count > 0 && !fixtureColumns.Contains("UnavailableHomeAttackImpact"))
                    await ExecuteSchemaAsync("""ALTER TABLE "FixtureContexts" ADD COLUMN "UnavailableHomeAttackImpact" REAL NOT NULL DEFAULT 0""", ct);
                if (fixtureColumns.Count > 0 && !fixtureColumns.Contains("UnavailableHomeDefenseImpact"))
                    await ExecuteSchemaAsync("""ALTER TABLE "FixtureContexts" ADD COLUMN "UnavailableHomeDefenseImpact" REAL NOT NULL DEFAULT 0""", ct);
                if (fixtureColumns.Count > 0 && !fixtureColumns.Contains("UnavailableAwayAttackImpact"))
                    await ExecuteSchemaAsync("""ALTER TABLE "FixtureContexts" ADD COLUMN "UnavailableAwayAttackImpact" REAL NOT NULL DEFAULT 0""", ct);
                if (fixtureColumns.Count > 0 && !fixtureColumns.Contains("UnavailableAwayDefenseImpact"))
                    await ExecuteSchemaAsync("""ALTER TABLE "FixtureContexts" ADD COLUMN "UnavailableAwayDefenseImpact" REAL NOT NULL DEFAULT 0""", ct);
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }

            async Task<HashSet<string>> ColumnsAsync(string table, CancellationToken token)
            {
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    columns.Add(reader.GetString(1));
                return columns;
            }

            async Task ExecuteSchemaAsync(string sql, CancellationToken token)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }
        }

        private async Task EnsureSnapshotColumnsAsync(CancellationToken ct)
        {
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose)
                await connection.OpenAsync(ct);

            try
            {
                var snapshotColumns = await ColumnsAsync("Snapshots", ct);
                if (snapshotColumns.Count > 0 && !snapshotColumns.Contains("BatchId"))
                    await ExecuteSchemaAsync("""ALTER TABLE "Snapshots" ADD COLUMN "BatchId" INTEGER NULL""", ct);

                if (snapshotColumns.Count > 0)
                    await ExecuteSchemaAsync("""CREATE INDEX IF NOT EXISTS "IX_Snapshots_Kind_BatchId_CreatedAt" ON "Snapshots" ("Kind", "BatchId", "CreatedAt")""", ct);
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }

            async Task<HashSet<string>> ColumnsAsync(string table, CancellationToken token)
            {
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    columns.Add(reader.GetString(1));
                return columns;
            }

            async Task ExecuteSchemaAsync(string sql, CancellationToken token)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }
        }

        private string FullPath(string fileName) => Path.Combine(_environment.ContentRootPath, "Data", fileName);
    }
}
