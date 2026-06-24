using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Models;
using Oloraculo.Web.Predictors;

namespace Oloraculo.Web.Services
{
    public class PredictionService
    {
        private readonly OloraculoDbContext _db;
        private readonly OloraculoConfig _config;

        public PredictionService(OloraculoDbContext db, IOptions<OloraculoConfig> config)
        {
            _db = db;
            _config = config.Value;
        }

        public async Task<MatchPredictionResult?> PredictFixtureAsync(string fixtureId, CancellationToken ct = default)
        {
            var fixture = await _db.Fixtures.FindAsync([fixtureId], ct);
            return fixture is null ? null : await PredictAsync(fixture, ct);
        }

        public async Task<MatchPredictionResult> PredictPairAsync(string homeId, string awayId, CancellationToken ct = default)
        {
            var fixture = new Fixture { Id = $"pair:{homeId}:{awayId}", HomeTeamId = homeId, AwayTeamId = awayId, NeutralVenue = true };
            return await PredictAsync(fixture, ct);
        }

        public async Task<IReadOnlyList<MatchPredictionResult>> PredictFixturesAsync(IEnumerable<Fixture> fixtures, CancellationToken ct = default)
        {
            var fixtureList = fixtures.ToList();
            var predictors = await BuildPredictorsAsync(ct);
            var results = new List<MatchPredictionResult>(fixtureList.Count);

            foreach (var fixture in fixtureList)
                results.Add(await PredictAsync(fixture, predictors, ct));

            return results;
        }

        public async Task<MatchPredictionResult> PredictAsync(Fixture fixture, CancellationToken ct = default)
        {
            var predictors = await BuildPredictorsAsync(ct);
            return await PredictAsync(fixture, predictors, ct);
        }

        private async Task<MatchPredictionResult> PredictAsync(Fixture fixture, IReadOnlyList<IPredictor> predictors, CancellationToken ct)
        {
            var context = await BuildContextAsync(fixture, ct);
            var ladder = predictors.Select(p => p.Predict(context)).ToList();
            var best = FinalPredictionSelector.Select(ladder);
            var baselineContext = WithoutFixtureContext(context);
            var baselineLadder = predictors.Select(p => p.Predict(baselineContext)).ToList();
            var baselineBest = FinalPredictionSelector.Select(baselineLadder);

            return new MatchPredictionResult
            {
                Fixture = fixture,
                HomeTeamName = context.HomeTeam.Name,
                AwayTeamName = context.AwayTeam.Name,
                Predictions = ladder,
                BestPrediction = best,
                Criteria = PredictionCriteriaBuilder.Build(context, ladder, best),
                AdjustmentComparison = BuildAdjustmentComparison(context, baselineBest, best)
            };
        }

        private static MatchContext WithoutFixtureContext(MatchContext context) => new()
        {
            Fixture = context.Fixture,
            HomeTeam = context.HomeTeam,
            AwayTeam = context.AwayTeam,
            HomeElo = context.HomeElo,
            AwayElo = context.AwayElo,
            HomeFifaRank = context.HomeFifaRank,
            AwayFifaRank = context.AwayFifaRank,
            HomeRecentMatchHistory = context.HomeRecentMatchHistory,
            AwayRecentMatchHistory = context.AwayRecentMatchHistory,
            FixtureContext = null
        };

        private static PredictionAdjustmentComparison? BuildAdjustmentComparison(
            MatchContext context,
            MatchPrediction baselineBest,
            MatchPrediction adjustedBest)
        {
            var fixtureContext = context.FixtureContext;
            if (fixtureContext is null)
                return null;

            var signals = new List<PredictionAdjustmentSignal>();
            var availabilityApplied = adjustedBest.FeaturesUsed.Any(f => f.Contains("Disponibilidad de jugadores", StringComparison.OrdinalIgnoreCase));
            var hasPlayerContext = fixtureContext.UnavailableHomePlayers > 0
                                   || fixtureContext.UnavailableAwayPlayers > 0
                                   || fixtureContext.UnavailableHomeAttackImpact > 0
                                   || fixtureContext.UnavailableHomeDefenseImpact > 0
                                   || fixtureContext.UnavailableAwayAttackImpact > 0
                                   || fixtureContext.UnavailableAwayDefenseImpact > 0;

            if (hasPlayerContext)
            {
                signals.Add(new PredictionAdjustmentSignal
                {
                    Name = "Disponibilidad de jugadores",
                    Applied = availabilityApplied,
                    Available = true,
                    Modeled = true,
                    Detail = $"Bajas: {context.HomeTeam.Name} {fixtureContext.UnavailableHomePlayers}, {context.AwayTeam.Name} {fixtureContext.UnavailableAwayPlayers}. Impacto por bajas propias: {context.HomeTeam.Name} ataque {fixtureContext.UnavailableHomeAttackImpact:P1}, defensa {fixtureContext.UnavailableHomeDefenseImpact:P1}; {context.AwayTeam.Name} ataque {fixtureContext.UnavailableAwayAttackImpact:P1}, defensa {fixtureContext.UnavailableAwayDefenseImpact:P1}."
                });
            }

            if (fixtureContext.HasLineups)
            {
                signals.Add(new PredictionAdjustmentSignal
                {
                    Name = "Alineaciones",
                    Applied = false,
                    Available = true,
                    Modeled = false,
                    Detail = "Disponibles vía API-Football Pro; no aplicadas porque todavía no hay conversión validada a scoring."
                });
            }

            if (fixtureContext.HasOdds)
            {
                signals.Add(new PredictionAdjustmentSignal
                {
                    Name = "Cuotas (odds)",
                    Applied = false,
                    Available = true,
                    Modeled = false,
                    Detail = "Disponibles vía API-Football Pro; no aplicadas porque todavía no hay calibración validada por cuotas."
                });
            }

            return new PredictionAdjustmentComparison
            {
                BaselinePrediction = baselineBest,
                AdjustedPrediction = adjustedBest,
                BaselineMethodName = SelectedMethodName(baselineBest),
                AdjustedMethodName = SelectedMethodName(adjustedBest),
                Signals = signals
            };
        }

        private static string SelectedMethodName(MatchPrediction prediction) =>
            prediction.Sources.FirstOrDefault(source =>
                string.Equals(source.Name, "model ladder", StringComparison.OrdinalIgnoreCase))?.Notes
            ?? prediction.PredictorName;

        public async Task<MatchContext> BuildContextAsync(Fixture fixture, CancellationToken ct = default)
        {
            var home = await _db.Teams.FindAsync([fixture.HomeTeamId], ct) ?? new Team { Id = fixture.HomeTeamId, Name = fixture.HomeTeamId };
            var away = await _db.Teams.FindAsync([fixture.AwayTeamId], ct) ?? new Team { Id = fixture.AwayTeamId, Name = fixture.AwayTeamId };

            return new MatchContext
            {
                Fixture = fixture,
                HomeTeam = home,
                AwayTeam = away,
                HomeElo = await LatestRatingAsync(fixture.HomeTeamId, RatingTypeEnum.Elo, ct),
                AwayElo = await LatestRatingAsync(fixture.AwayTeamId, RatingTypeEnum.Elo, ct),
                HomeFifaRank = await LatestRatingAsync(fixture.HomeTeamId, RatingTypeEnum.Fifa, ct),
                AwayFifaRank = await LatestRatingAsync(fixture.AwayTeamId, RatingTypeEnum.Fifa, ct),
                HomeRecentMatchHistory = await RecentResultsAsync(fixture.HomeTeamId, ct),
                AwayRecentMatchHistory = await RecentResultsAsync(fixture.AwayTeamId, ct),
                FixtureContext = await _db.FixtureContexts.FindAsync([fixture.Id], ct)
            };
        }

        public async Task<IReadOnlyList<IPredictor>> BuildPredictorsAsync(CancellationToken ct = default)
        {
            var results = await _db.Results.AsNoTracking().ToListAsync(ct);
            var goal = new GoalModel(results, _config.GoalModelYearsWindow);

            return
            [
                new NullModel(),
                new FifaRankingModel(),
                new EloModel(),
                new RecentFormModel(),
                goal,
                new GoalPlusRecentContextModel(goal)
            ];
        }

        private async Task<Rating?> LatestRatingAsync(string teamId, RatingTypeEnum type, CancellationToken ct)
        {
            return (await _db.Ratings.AsNoTracking()
                .Where(r => r.TeamId == teamId && r.Type == type)
                .ToListAsync(ct))
                .OrderByDescending(r => r.AsOf)
                .FirstOrDefault();
        }

        private async Task<IReadOnlyList<MatchResult>> RecentResultsAsync(string teamId, CancellationToken ct)
        {
            return (await _db.Results.AsNoTracking()
                .Where(r => r.HomeTeamId == teamId || r.AwayTeamId == teamId)
                .ToListAsync(ct))
                .OrderByDescending(r => r.Date)
                .Take(_config.RecentResultCount)
                .ToList();
        }
    }
}
