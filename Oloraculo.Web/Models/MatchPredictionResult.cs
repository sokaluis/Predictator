namespace Oloraculo.Web.Models
{
    public class MatchPredictionResult
    {
        public required Fixture Fixture { get; set; }
        public required string HomeTeamName { get; set; }
        public required string AwayTeamName { get; set; }
        public IReadOnlyList<MatchPrediction> Predictions { get; set; } = Array.Empty<MatchPrediction>();
        public required MatchPrediction BestPrediction { get; init; }
        public PredictionCriteria? Criteria { get; set; }
    }
}
