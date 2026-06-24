using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Probability;
using System.Text.Json;

namespace Oloraculo.Web.Services
{
    public class EvaluationService
    {
        private readonly OloraculoDbContext _db;

        public EvaluationService(OloraculoDbContext db) => _db = db;

        public async Task<int> EvaluateLatestSnapshotAsync(Fixture fixture, int homeGoals, int awayGoals, CancellationToken ct = default)
        {
            var snapshots = (await _db.Snapshots
                .Where(s => s.Kind == "match"
                    && s.FixtureId == fixture.Id
                    && s.HomeWin.HasValue
                    && s.Draw.HasValue
                    && s.AwayWin.HasValue)
                .ToListAsync(ct))
                .GroupBy(s => s.ModelName)
                .Select(g => g
                    .OrderByDescending(s => s.CreatedAt)
                    .ThenByDescending(s => s.Id)
                    .First())
                .ToList();

            if (snapshots.Count == 0)
                return 0;

            var actual = OutcomeFromGoals(homeGoals, awayGoals);

            var existingEvaluations = await _db.Evaluations
                .Where(e => e.FixtureId == fixture.Id)
                .ToListAsync(ct);
            var seenKeys = existingEvaluations
                .Select(e => (e.ModelName, e.PredictedAt))
                .ToHashSet();

            foreach (var evaluation in existingEvaluations)
                UpdateEvaluationActual(evaluation, homeGoals, awayGoals, actual);

            var resultDate = fixture.KickoffUtc ?? DateTimeOffset.UtcNow;
            var manualResults = await _db.Results
                .Where(r => r.Source == "manual"
                    && r.HomeTeamId == fixture.HomeTeamId
                    && r.AwayTeamId == fixture.AwayTeamId
                    && r.Tournament == "FIFA World Cup 2026")
                .ToListAsync(ct);
            var existingManualResult = fixture.KickoffUtc.HasValue
                ? manualResults.FirstOrDefault(r => r.Date == fixture.KickoffUtc.Value) ?? manualResults.OrderByDescending(r => r.Date).FirstOrDefault()
                : manualResults.OrderByDescending(r => r.Date).FirstOrDefault();

            var shouldRecordResult = !fixture.IsPlayed
                || fixture.HomeGoals != homeGoals
                || fixture.AwayGoals != awayGoals
                || existingManualResult is null;

            var added = 0;
            foreach (var snapshot in snapshots)
            {
                // Top-level evaluation from snapshot columns
                var snapshotKey = (snapshot.ModelName, snapshot.CreatedAt);
                if (seenKeys.Add(snapshotKey))
                {
                    var predicted = new OutcomeProbabilities(snapshot.HomeWin!.Value, snapshot.Draw!.Value, snapshot.AwayWin!.Value).Normalize();
                    _db.Evaluations.Add(new PredictionEvaluation
                    {
                        ModelName = snapshot.ModelName,
                        FixtureId = fixture.Id,
                        HomeTeamId = fixture.HomeTeamId,
                        AwayTeamId = fixture.AwayTeamId,
                        HomeGoals = homeGoals,
                        AwayGoals = awayGoals,
                        HomeWin = predicted.HomeWin,
                        Draw = predicted.Draw,
                        AwayWin = predicted.AwayWin,
                        Actual = actual,
                        BrierScore = ProbabilityHelper.BrierScore(predicted, actual),
                        RankedProbabilityScore = ProbabilityHelper.RankedProbabilityScore(predicted, actual),
                        LogLoss = ProbabilityHelper.LogLoss(predicted, actual),
                        TopPickCorrect = predicted.TopPick == actual,
                        PredictedAt = snapshot.CreatedAt
                    });
                    added++;
                }

                // Synthesized baseline evaluation from AdjustmentComparison.BaselinePrediction in payload
                var baseline = TryExtractBaseline(snapshot.PayloadJson, snapshot.ModelName);
                if (baseline != null)
                {
                    var baselineKey = (baseline.ModelName, snapshot.CreatedAt);
                    if (seenKeys.Add(baselineKey))
                    {
                        _db.Evaluations.Add(new PredictionEvaluation
                        {
                            ModelName = baseline.ModelName,
                            FixtureId = fixture.Id,
                            HomeTeamId = fixture.HomeTeamId,
                            AwayTeamId = fixture.AwayTeamId,
                            HomeGoals = homeGoals,
                            AwayGoals = awayGoals,
                            HomeWin = baseline.Probabilities.HomeWin,
                            Draw = baseline.Probabilities.Draw,
                            AwayWin = baseline.Probabilities.AwayWin,
                            Actual = actual,
                            BrierScore = ProbabilityHelper.BrierScore(baseline.Probabilities, actual),
                            RankedProbabilityScore = ProbabilityHelper.RankedProbabilityScore(baseline.Probabilities, actual),
                            LogLoss = ProbabilityHelper.LogLoss(baseline.Probabilities, actual),
                            TopPickCorrect = baseline.Probabilities.TopPick == actual,
                            PredictedAt = snapshot.CreatedAt
                        });
                        added++;
                    }
                }
            }

            if (shouldRecordResult)
            {
                if (existingManualResult is null)
                {
                    _db.Results.Add(new MatchResult
                    {
                        Id = CryptoUtil.GetSha256($"manual|{resultDate:O}|{fixture.HomeTeamId}|{fixture.AwayTeamId}"),
                        HomeTeamId = fixture.HomeTeamId,
                        AwayTeamId = fixture.AwayTeamId,
                        HomeGoals = homeGoals,
                        AwayGoals = awayGoals,
                        Date = resultDate,
                        Tournament = "FIFA World Cup 2026",
                        Neutral = fixture.NeutralVenue,
                        Source = "manual"
                    });
                }
                else
                {
                    existingManualResult.HomeGoals = homeGoals;
                    existingManualResult.AwayGoals = awayGoals;
                    existingManualResult.Date = resultDate;
                    existingManualResult.Neutral = fixture.NeutralVenue;
                }
            }
            fixture.IsPlayed = true;
            fixture.HomeGoals = homeGoals;
            fixture.AwayGoals = awayGoals;
            await _db.SaveChangesAsync(ct);
            return added;
        }

        public async Task<FixtureEvaluationRefreshReport> EvaluateUnevaluatedPlayedFixturesAsync(CancellationToken ct = default)
        {
            var fixtures = await _db.Fixtures
                .Where(f => f.IsPlayed && f.HomeGoals.HasValue && f.AwayGoals.HasValue)
                .ToListAsync(ct);

            var evaluated = 0;
            var skippedAlreadyEvaluated = 0;
            var skippedWithoutSnapshot = 0;

            foreach (var fixture in fixtures)
            {
                var hasSnapshot = await _db.Snapshots
                    .AnyAsync(s => s.Kind == "match"
                        && s.FixtureId == fixture.Id
                        && s.HomeWin.HasValue
                        && s.Draw.HasValue
                        && s.AwayWin.HasValue, ct);
                if (!hasSnapshot)
                {
                    skippedWithoutSnapshot++;
                    continue;
                }

                var count = await EvaluateLatestSnapshotAsync(fixture, fixture.HomeGoals!.Value, fixture.AwayGoals!.Value, ct);
                if (count == 0)
                    skippedAlreadyEvaluated++;
                else
                    evaluated += count;
            }

            return new FixtureEvaluationRefreshReport(
                evaluated,
                skippedAlreadyEvaluated,
                skippedWithoutSnapshot);
        }

        private static void UpdateEvaluationActual(PredictionEvaluation evaluation, int homeGoals, int awayGoals, string actual)
        {
            var predicted = new OutcomeProbabilities(evaluation.HomeWin, evaluation.Draw, evaluation.AwayWin).Normalize();
            evaluation.HomeGoals = homeGoals;
            evaluation.AwayGoals = awayGoals;
            evaluation.Actual = actual;
            evaluation.BrierScore = ProbabilityHelper.BrierScore(predicted, actual);
            evaluation.RankedProbabilityScore = ProbabilityHelper.RankedProbabilityScore(predicted, actual);
            evaluation.LogLoss = ProbabilityHelper.LogLoss(predicted, actual);
            evaluation.TopPickCorrect = predicted.TopPick == actual;
        }

        public async Task<IReadOnlyList<ModelPerformanceRow>> PerformanceAsync(CancellationToken ct = default)
        {
            var rows = await _db.Evaluations.AsNoTracking().ToListAsync(ct);
            return rows.GroupBy(e => e.ModelName)
                .Select(g => new ModelPerformanceRow
                {
                    ModelName = g.Key,
                    Count = g.Count(),
                    TopPickAccuracy = g.Average(e => e.TopPickCorrect ? 1.0 : 0.0),
                    MeanBrier = g.Average(e => e.BrierScore),
                    MeanRps = g.Average(e => e.RankedProbabilityScore),
                    MeanLogLoss = g.Average(e => e.LogLoss)
                })
                .OrderBy(r => r.MeanRps)
                .ToList();
        }

        public async Task<PairedComparisonRow?> PairedOracleContextComparisonAsync(CancellationToken ct = default)
        {
            var allRows = await _db.Evaluations.AsNoTracking().ToListAsync(ct);

            const string baselineKey = "Oráculo final";
            var contextKey = MatchPrediction.ContextAdjustedPredictionIdentity;

            var baselineDict = allRows
                .Where(e => e.ModelName == baselineKey)
                .GroupBy(e => (e.FixtureId, e.PredictedAt))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Id).First());

            var contextDict = allRows
                .Where(e => e.ModelName == contextKey)
                .GroupBy(e => (e.FixtureId, e.PredictedAt))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Id).First());

            var commonKeys = baselineDict.Keys.Intersect(contextDict.Keys).ToList();
            if (commonKeys.Count == 0)
                return null;

            var baselineBrier = 0.0;
            var baselineRps = 0.0;
            var baselineLogLoss = 0.0;
            var baselineTopPick = 0.0;
            var contextBrier = 0.0;
            var contextRps = 0.0;
            var contextLogLoss = 0.0;
            var contextTopPick = 0.0;

            foreach (var key in commonKeys)
            {
                var b = baselineDict[key];
                var c = contextDict[key];
                baselineBrier += b.BrierScore;
                baselineRps += b.RankedProbabilityScore;
                baselineLogLoss += b.LogLoss;
                baselineTopPick += b.TopPickCorrect ? 1.0 : 0.0;
                contextBrier += c.BrierScore;
                contextRps += c.RankedProbabilityScore;
                contextLogLoss += c.LogLoss;
                contextTopPick += c.TopPickCorrect ? 1.0 : 0.0;
            }

            var n = commonKeys.Count;
            baselineBrier /= n;
            baselineRps /= n;
            baselineLogLoss /= n;
            baselineTopPick /= n;
            contextBrier /= n;
            contextRps /= n;
            contextLogLoss /= n;
            contextTopPick /= n;

            return new PairedComparisonRow
            {
                PairCount = n,
                BaselineMeanBrier = baselineBrier,
                BaselineMeanRps = baselineRps,
                BaselineMeanLogLoss = baselineLogLoss,
                BaselineTopPickAccuracy = baselineTopPick,
                ContextMeanBrier = contextBrier,
                ContextMeanRps = contextRps,
                ContextMeanLogLoss = contextLogLoss,
                ContextTopPickAccuracy = contextTopPick,
                DeltaBrier = contextBrier - baselineBrier,
                DeltaRps = contextRps - baselineRps,
                DeltaLogLoss = contextLogLoss - baselineLogLoss,
                DeltaTopPickAccuracy = contextTopPick - baselineTopPick
            };
        }

        public async Task<IReadOnlyList<PredictionEvaluation>> BestCallsAsync(int take = 8, CancellationToken ct = default) =>
            await _db.Evaluations.AsNoTracking().OrderBy(e => e.RankedProbabilityScore).Take(take).ToListAsync(ct);

        public async Task<IReadOnlyList<PredictionEvaluation>> OverconfidentFailuresAsync(int take = 8, CancellationToken ct = default) =>
            await _db.Evaluations.AsNoTracking()
                .Where(e => !e.TopPickCorrect)
                .OrderByDescending(e => Math.Max(e.HomeWin, Math.Max(e.Draw, e.AwayWin)))
                .Take(take)
                .ToListAsync(ct);

        public static string OutcomeFromGoals(int homeGoals, int awayGoals) =>
            homeGoals > awayGoals ? "Home" : awayGoals > homeGoals ? "Away" : "Draw";

        // ----- Baseline extraction from persisted AdjustmentComparison -----

        private sealed record BaselineExtraction(string ModelName, OutcomeProbabilities Probabilities);

        private static BaselineExtraction? TryExtractBaseline(string payloadJson, string topLevelModelName)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("AdjustmentComparison", out var adjElement) || adjElement.ValueKind != JsonValueKind.Object)
                    return null;

                if (!adjElement.TryGetProperty("BaselinePrediction", out var baselineElement) || baselineElement.ValueKind != JsonValueKind.Object)
                    return null;

                var predictionIdentity = TryGetPropString(baselineElement, "PredictionIdentity");
                var predictorName = TryGetPropString(baselineElement, "PredictorName");
                var effectiveModelName = !string.IsNullOrWhiteSpace(predictionIdentity) ? predictionIdentity : predictorName;
                if (string.IsNullOrWhiteSpace(effectiveModelName))
                    return null;

                if (string.Equals(effectiveModelName, topLevelModelName, StringComparison.Ordinal))
                    return null;

                if (!baselineElement.TryGetProperty("Outcome", out var outcomeElement) || outcomeElement.ValueKind != JsonValueKind.Object)
                    return null;

                var homeWin = TryGetPropDouble(outcomeElement, "HomeWin");
                var draw = TryGetPropDouble(outcomeElement, "Draw");
                var awayWin = TryGetPropDouble(outcomeElement, "AwayWin");
                if (homeWin is null || draw is null || awayWin is null)
                    return null;

                var probabilities = new OutcomeProbabilities(homeWin.Value, draw.Value, awayWin.Value);
                if (!probabilities.IsValid)
                    return null;

                return new BaselineExtraction(effectiveModelName, probabilities.Normalize());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? TryGetPropString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;

        private static double? TryGetPropDouble(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.TryGetDouble(out var value) ? value : null
                : null;
    }

    public sealed record FixtureEvaluationRefreshReport(
        int Evaluated,
        int SkippedAlreadyEvaluated,
        int SkippedWithoutSnapshot);
}
