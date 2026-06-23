using Oloraculo.Web.Models;

namespace Oloraculo.Web.Services
{
    public static class PredictionCriteriaBuilder
    {
        public static PredictionCriteria Build(
            MatchContext context,
            IReadOnlyList<MatchPrediction> ladder,
            MatchPrediction bestPrediction)
        {
            var signals = new List<PredictionSignal>();
            var ordered = ladder.OrderBy(p => p.PredictorPriority).ToList();
            var selectedPredictor = ordered.LastOrDefault(p => p.PredictorPriority == bestPrediction.PredictorPriority && !p.Degraded)
                                    ?? ordered.FirstOrDefault(p => p.PredictorPriority == bestPrediction.PredictorPriority);
            var rankingBiasApplied = bestPrediction.Drivers.Any(d => d.Contains("calibración Elo/FIFA", StringComparison.OrdinalIgnoreCase));
            var fc = context.FixtureContext;

            // --- Elo ---
            var eloAvailable = context.HomeElo is not null && context.AwayElo is not null;
            var eloApplied = AnyFeatureUsed(bestPrediction, "Elo") || rankingBiasApplied;
            signals.Add(Signal("Ratings Elo", SignalCategory.Ranking, eloApplied, eloAvailable,
                "Usados en la predicción final",
                "Ratings Elo disponibles para ambos equipos, no aplicados en el escalón seleccionado",
                "Faltan ratings Elo para uno o ambos equipos"));

            // --- FIFA ---
            var fifaAvailable = context.HomeFifaRank is not null && context.AwayFifaRank is not null;
            var fifaApplied = AnyFeatureUsed(bestPrediction, "FIFA") || rankingBiasApplied;
            signals.Add(Signal("Ranking FIFA", SignalCategory.Ranking, fifaApplied, fifaAvailable,
                "Usado en la predicción final",
                "Ranking FIFA disponible para ambos equipos, no aplicado en el escalón seleccionado",
                "Faltan datos de ranking FIFA para uno o ambos equipos"));

            // --- Recent history ---
            var historyAvailable = context.HomeRecentMatchHistory.Count > 0 && context.AwayRecentMatchHistory.Count > 0;
            var historyApplied = AnyFeatureUsed(bestPrediction, "Resultados recientes")
                                 || AnyFeatureUsed(bestPrediction, "Historial reciente");
            signals.Add(Signal("Historial reciente", SignalCategory.Form, historyApplied, historyAvailable,
                "Resultados recientes usados en la predicción",
                "Historial reciente disponible para ambos equipos, no aplicado",
                "Falta historial reciente para uno o ambos equipos"));

            // --- Goal model ---
            var goalApplied = AnyFeatureUsed(bestPrediction, "Modelo de goles")
                              || AnyFeatureUsed(bestPrediction, "Fuerza de ataque")
                              || AnyFeatureUsed(bestPrediction, "Vulnerabilidad defensiva");
            signals.Add(Signal("Modelo de goles (Poisson/Dixon-Coles)", SignalCategory.GoalModel, goalApplied, true,
                "Modelo de goles aplicado con fuerzas de ataque/defensa",
                "Modelo de goles disponible pero no fue el escalón seleccionado",
                null));

            // --- Player availability ---
            var hasPlayerCounts = fc is not null && (fc.UnavailableHomePlayers > 0 || fc.UnavailableAwayPlayers > 0);
            var hasRoleAware = fc is not null
                               && (fc.UnavailableHomeAttackImpact > 0
                                   || fc.UnavailableHomeDefenseImpact > 0
                                   || fc.UnavailableAwayAttackImpact > 0
                                   || fc.UnavailableAwayDefenseImpact > 0);
            var playerDataAvailable = hasPlayerCounts || hasRoleAware;
            var playerApplied = AnyFeatureUsed(bestPrediction, "Disponibilidad de jugadores");

            var playerAppliedDetail = hasRoleAware
                ? $"Impacto por rol aplicado (ataque/defensa). Bajas: equipo A {fc?.UnavailableHomePlayers ?? 0}, equipo B {fc?.UnavailableAwayPlayers ?? 0}"
                : $"Disponibilidad aplicada por recuento. Bajas: equipo A {fc?.UnavailableHomePlayers ?? 0}, equipo B {fc?.UnavailableAwayPlayers ?? 0}";
            var playerAvailableDetail = hasRoleAware
                ? "Impacto por rol disponible pero no aplicado (el predictor seleccionado no lo usó)"
                : $"Datos de bajas disponibles (equipo A: {fc?.UnavailableHomePlayers ?? 0}, equipo B: {fc?.UnavailableAwayPlayers ?? 0}), no aplicados";

            signals.Add(Signal("Disponibilidad de jugadores", SignalCategory.PlayerAvailability,
                playerApplied, playerDataAvailable,
                playerAppliedDetail, playerAvailableDetail,
                "Sin datos de disponibilidad de jugadores"));

            // --- Lineups (never applied in current scoring) ---
            signals.Add(Signal("Alineaciones", SignalCategory.Lineups, false, fc?.HasLineups == true,
                null,
                "Datos de alineaciones disponibles vía API-Football, sin modelo de conversión a scoring",
                "Sin datos de alineaciones"));

            // --- Odds (never applied in current scoring) ---
            signals.Add(Signal("Cuotas (odds)", SignalCategory.Odds, false, fc?.HasOdds == true,
                null,
                "Cuotas disponibles vía API-Football, sin modelo de calibración por cuotas",
                "Sin datos de cuotas"));

            // --- Availability news ---
            var newsAvailable = fc?.HasAvailabilityNews == true;
            var newsApplied = bestPrediction.Sources.Any(s =>
                s.Name.Contains("Availability", StringComparison.OrdinalIgnoreCase));
            signals.Add(Signal("Noticias de disponibilidad", SignalCategory.Context, newsApplied, newsAvailable,
                "Noticias de disponibilidad usadas como fuente",
                "Noticias de disponibilidad disponibles, no usadas como fuente en el escalón seleccionado",
                "Sin noticias de disponibilidad"));

            // --- Ranking bias (special: only appears when applied) ---
            if (rankingBiasApplied)
            {
                signals.Add(new PredictionSignal
                {
                    Name = "Calibración Elo/FIFA",
                    Category = SignalCategory.Ranking,
                    Status = SignalStatus.Applied,
                    Detail = "Aplicada porque ambos modelos de ranking coincidieron contra el predictor seleccionado (peso 15%)"
                });
            }

            return new PredictionCriteria
            {
                Signals = signals,
                SelectedPredictorName = selectedPredictor?.PredictorName,
                HasRankingBias = rankingBiasApplied
            };
        }

        private static bool AnyFeatureUsed(MatchPrediction prediction, string fragment) =>
            prediction.FeaturesUsed.Any(f => f.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        private static PredictionSignal Signal(
            string name,
            SignalCategory category,
            bool isApplied,
            bool isAvailable,
            string? appliedDetail,
            string? availableDetail,
            string? missingDetail)
        {
            if (isApplied)
            {
                return new PredictionSignal
                {
                    Name = name,
                    Category = category,
                    Status = SignalStatus.Applied,
                    Detail = appliedDetail
                };
            }

            if (isAvailable)
            {
                return new PredictionSignal
                {
                    Name = name,
                    Category = category,
                    Status = SignalStatus.Available,
                    Detail = availableDetail
                };
            }

            return new PredictionSignal
            {
                Name = name,
                Category = category,
                Status = SignalStatus.Missing,
                Detail = missingDetail
            };
        }
    }
}
