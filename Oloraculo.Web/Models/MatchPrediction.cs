using Oloraculo.Web.Probability;

namespace Oloraculo.Web.Models
{
    public class MatchPrediction
    {
        public const string ContextAdjustedPredictionIdentity = "Oráculo final + contexto API";

        public string PredictorName { get; set; }
        public string? PredictionIdentity { get; set; }
        public int PredictorPriority { get; set; }
        public string FixtureId { get; set; }
        public string HomeTeamId { get; set; }
        public string AwayTeamId { get; set; }
        public OutcomeProbabilities Outcome { get; set; } = OutcomeProbabilities.Uniform;
        public double? ExpectedHomeGoals { get; set; }
        public double? ExpectedAwayGoals { get; set; }
        public ScorelineDistribution? Scoreline { get; set; }
        public (int Home, int Away)? MostLikelyScore { get; set; }
        public string Explanation { get; set; }
        public IReadOnlyList<string> Drivers { get; init; } = [];
        public IReadOnlyList<string> FeaturesUsed { get; init; } = [];
        public IReadOnlyList<string> FeaturesMissing { get; init; } = [];
        public IReadOnlyList<SourceMetadata> Sources { get; init; } = [];
        public bool Degraded { get; init; }
        public string EffectiveModelName => string.IsNullOrWhiteSpace(PredictionIdentity) ? PredictorName : PredictionIdentity;
        public bool IsContextAdjusted => string.Equals(PredictionIdentity, ContextAdjustedPredictionIdentity, StringComparison.Ordinal);
    }
}
