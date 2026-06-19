using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.ApiFootballModels;
using Oloraculo.Web.Models.CsvModels;
using System.Globalization;

namespace Oloraculo.Web.Services
{
    public class PlayerImpactService
    {
        private const double PenaltyGoalWeight = 0.4;
        private const double OpenPlayGoalWeight = 1.0;
        private const double GoalScorerBoostCap = 0.040;
        private const double GoalScorerBoostBase = 0.010;
        private const double GoalScorerBoostPerWeightedGoal = 0.006;
        private const double MaxWeightedGoalsForBoost = 5.0;
        private const double ApiAttackBoostCap = 0.030;
        private const double ApiAttackBoostPerContribution = 0.006;
        private const double AssistAttackContributionWeight = 0.7;
        private const double MaxApiAttackContributionForBoost = 5.0;
        private const double RegularDefensiveRoleBoost = 0.015;
        private const double PlayerAttackImpactCap = 0.085;
        private const double PlayerDefenseImpactCap = 0.085;
        internal const int RegularLineupsThreshold = 3;
        internal const int RegularMinutesThreshold = 270;
        internal const double RegularRatingThreshold = 6.8;

        private readonly HttpClient _http;
        private readonly IWebHostEnvironment _environment;
        private readonly OloraculoConfig _config;
        private Task<IReadOnlyDictionary<string, double>>? _goalscorerIndexTask;

        public PlayerImpactService(HttpClient http, IWebHostEnvironment environment, IOptions<OloraculoConfig> config)
        {
            _http = http;
            _environment = environment;
            _config = config.Value;
        }

        public async Task<PlayerImpactResult> CalculateAsync(
            string teamId,
            string playerName,
            string playerKey,
            string? position,
            IReadOnlyList<ApiPlayerStatistic>? apiStatistics = null,
            CancellationToken ct = default)
        {
            var normalizedPosition = AvailabilityNewsService.NormalizePosition(position);
            var baseImpact = AvailabilityNewsService.ImpactForPosition(normalizedPosition);
            var normalizedPlayerKey = string.IsNullOrWhiteSpace(playerKey)
                ? AvailabilityNewsService.NormalizePlayerKey(playerName)
                : playerKey;
            var weightedGoals = await WeightedGoalsAsync(teamId, normalizedPlayerKey, ct);
            var goalScorerBoost = weightedGoals > 0
                ? Math.Min(GoalScorerBoostCap, GoalScorerBoostBase + (GoalScorerBoostPerWeightedGoal * Math.Min(weightedGoals.Value, MaxWeightedGoalsForBoost)))
                : 0.0;

            var apiSnapshot = PlayerApiStatSnapshot.From(apiStatistics);
            var apiAttackContribution = (apiSnapshot.Goals ?? 0) + (AssistAttackContributionWeight * (apiSnapshot.Assists ?? 0));
            var apiAttackBoost = apiSnapshot.HasAnyStats
                ? Math.Min(ApiAttackBoostCap, ApiAttackBoostPerContribution * Math.Min(apiAttackContribution, MaxApiAttackContributionForBoost))
                : 0.0;
            var selectedAttackBoost = Math.Max(goalScorerBoost, apiAttackBoost);
            var regularDefensiveBoost = apiSnapshot.IsRegular &&
                PlayerPositions.IsRegularDefensiveRole(normalizedPosition)
                    ? RegularDefensiveRoleBoost
                    : 0.0;

            var source = SourceText(goalScorerBoost, apiAttackBoost, regularDefensiveBoost);
            return new PlayerImpactResult(
                Math.Min(PlayerAttackImpactCap, baseImpact.Attack + selectedAttackBoost),
                Math.Min(PlayerDefenseImpactCap, baseImpact.Defense + regularDefensiveBoost),
                source,
                weightedGoals,
                apiSnapshot.Goals,
                apiSnapshot.Assists,
                apiSnapshot.Minutes,
                apiSnapshot.Lineups,
                apiSnapshot.Rating);
        }

        public static PlayerImpactResult FallbackImpact(string? position)
        {
            var impact = AvailabilityNewsService.ImpactForPosition(position);
            return new PlayerImpactResult(impact.Attack, impact.Defense, PlayerImpactSources.Position, null, null, null, null, null, null);
        }

        public static IReadOnlyDictionary<string, double> BuildGoalscorerIndex(
            IEnumerable<GoalscorerCsvRow> rows,
            DateOnly today,
            int lookbackYears)
        {
            var cutoff = today.AddYears(-Math.Max(0, lookbackYears));
            var index = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Team) || string.IsNullOrWhiteSpace(row.Scorer))
                    continue;
                if (!DateTime.TryParse(row.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
                    continue;

                var date = DateOnly.FromDateTime(parsedDate);
                if (date < cutoff)
                    continue;
                if (bool.TryParse(row.OwnGoal, out var ownGoal) && ownGoal)
                    continue;

                var teamId = TeamNameNormalizer.ToId(row.Team);
                var playerKey = AvailabilityNewsService.NormalizePlayerKey(row.Scorer);
                if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(playerKey))
                    continue;

                var isPenalty = bool.TryParse(row.Penalty, out var penalty) && penalty;
                var value = isPenalty ? PenaltyGoalWeight : OpenPlayGoalWeight;
                var key = GoalscorerKey(teamId, playerKey);
                index[key] = index.TryGetValue(key, out var existing) ? existing + value : value;
            }

            return index;
        }

        private async Task<double?> WeightedGoalsAsync(string teamId, string playerKey, CancellationToken ct)
        {
            var index = await LoadGoalscorerIndexAsync(ct);
            return index.TryGetValue(GoalscorerKey(teamId, playerKey), out var weightedGoals)
                ? weightedGoals
                : null;
        }

        private Task<IReadOnlyDictionary<string, double>> LoadGoalscorerIndexAsync(CancellationToken ct) =>
            _goalscorerIndexTask ??= LoadGoalscorerIndexCoreAsync(ct);

        private async Task<IReadOnlyDictionary<string, double>> LoadGoalscorerIndexCoreAsync(CancellationToken ct)
        {
            var csvContent = await ReadGoalscorersCsvAsync(ct);
            if (string.IsNullOrWhiteSpace(csvContent))
                return new Dictionary<string, double>(StringComparer.Ordinal);

            try
            {
                using var reader = new StringReader(csvContent);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    TrimOptions = TrimOptions.Trim,
                    MissingFieldFound = null,
                    HeaderValidated = null
                });
                var rows = csv.GetRecords<GoalscorerCsvRow>().ToList();
                return BuildGoalscorerIndex(rows, DateOnly.FromDateTime(DateTime.UtcNow), _config.GoalscorerLookbackYears);
            }
            catch
            {
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }
        }

        private async Task<string?> ReadGoalscorersCsvAsync(CancellationToken ct)
        {
            var path = Path.Combine(_environment.ContentRootPath, "Data", OloraculoDataFiles.GoalscorersCsv);
            var localFileExists = File.Exists(path);
            if (localFileExists && !IsGoalscorersCsvStale(path))
                return await File.ReadAllTextAsync(path, ct);

            if (string.IsNullOrWhiteSpace(_config.GoalscorersRawUrl))
                return localFileExists ? await File.ReadAllTextAsync(path, ct) : null;

            try
            {
                var content = await _http.GetStringAsync(_config.GoalscorersRawUrl, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, ct);
                return content;
            }
            catch
            {
                return localFileExists ? await File.ReadAllTextAsync(path, ct) : null;
            }
        }

        private bool IsGoalscorersCsvStale(string path)
        {
            var maxAgeDays = Math.Max(0, _config.GoalscorersRefreshMaxAgeDays);
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            return DateTime.UtcNow - lastWriteUtc > TimeSpan.FromDays(maxAgeDays);
        }

        private static string SourceText(double goalScorerBoost, double apiAttackBoost, double regularDefensiveBoost)
        {
            string? attackingSource = null;
            if (goalScorerBoost > 0 || apiAttackBoost > 0)
                attackingSource = goalScorerBoost >= apiAttackBoost ? PlayerImpactSources.Goalscorers : PlayerImpactSources.ApiStats;

            var defensiveSource = regularDefensiveBoost > 0 ? PlayerImpactSources.ApiStats : null;
            return PlayerImpactSources.Combine(attackingSource, defensiveSource);
        }

        private static string GoalscorerKey(string teamId, string playerKey) => $"{teamId}|{playerKey}";
    }

    public sealed record PlayerImpactResult(
        double Attack,
        double Defense,
        string Source,
        double? WeightedInternationalGoals,
        int? ApiGoals,
        int? ApiAssists,
        int? ApiMinutes,
        int? ApiLineups,
        double? ApiRating);

    public sealed record PlayerApiStatSnapshot(
        int? Goals,
        int? Assists,
        int? Minutes,
        int? Lineups,
        double? Rating)
    {
        public bool HasAnyStats =>
            Goals.HasValue || Assists.HasValue || Minutes.HasValue || Lineups.HasValue || Rating.HasValue;

        public bool IsRegular =>
            (Lineups ?? 0) >= PlayerImpactService.RegularLineupsThreshold ||
            (Minutes ?? 0) >= PlayerImpactService.RegularMinutesThreshold ||
            (Rating ?? 0) >= PlayerImpactService.RegularRatingThreshold;

        public static PlayerApiStatSnapshot From(IReadOnlyList<ApiPlayerStatistic>? statistics)
        {
            if (statistics is null || statistics.Count == 0)
                return new PlayerApiStatSnapshot(null, null, null, null, null);

            var goals = SumNullable(statistics.Select(s => s.Goals.Total));
            var assists = SumNullable(statistics.Select(s => s.Goals.Assists));
            var minutes = SumNullable(statistics.Select(s => s.Games.Minutes));
            var lineups = SumNullable(statistics.Select(s => s.Games.Lineups));
            var ratings = statistics.Select(s => s.Games.Rating).Where(r => r.HasValue).Select(r => r!.Value).ToList();
            var rating = ratings.Count == 0 ? (double?)null : Math.Round(ratings.Average(), 2);

            return new PlayerApiStatSnapshot(goals, assists, minutes, lineups, rating);
        }

        private static int? SumNullable(IEnumerable<int?> values)
        {
            var materialized = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return materialized.Count == 0 ? null : materialized.Sum();
        }
    }
}
