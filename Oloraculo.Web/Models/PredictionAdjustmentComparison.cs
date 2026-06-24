namespace Oloraculo.Web.Models
{
    public sealed class PredictionAdjustmentComparison
    {
        public required MatchPrediction BaselinePrediction { get; init; }
        public required MatchPrediction AdjustedPrediction { get; init; }
        public required string BaselineMethodName { get; init; }
        public required string AdjustedMethodName { get; init; }
        public IReadOnlyList<PredictionAdjustmentSignal> Signals { get; init; } = [];

        public double? HomeExpectedGoalsDelta => Delta(AdjustedPrediction.ExpectedHomeGoals, BaselinePrediction.ExpectedHomeGoals);
        public double? AwayExpectedGoalsDelta => Delta(AdjustedPrediction.ExpectedAwayGoals, BaselinePrediction.ExpectedAwayGoals);
        public string BaselinePick => BaselinePrediction.Outcome.TopPick;
        public string AdjustedPick => AdjustedPrediction.Outcome.TopPick;
        public bool PickChanged => !string.Equals(BaselinePick, AdjustedPick, StringComparison.Ordinal);
        public bool HasModeledSignal => Signals.Any(signal => signal.Modeled);
        public bool HasAppliedModeledSignal => Signals.Any(signal => signal.Modeled && signal.Applied);
        public bool HasExpectedGoalsDelta =>
            Math.Abs(HomeExpectedGoalsDelta ?? 0) >= 0.005 ||
            Math.Abs(AwayExpectedGoalsDelta ?? 0) >= 0.005;
        public bool HasConfidenceDelta => Math.Abs(Confidence(AdjustedPrediction) - Confidence(BaselinePrediction)) >= 0.005;
        public bool HasModeledContextEffect => HasAppliedModeledSignal && (HasExpectedGoalsDelta || PickChanged || HasConfidenceDelta);

        private static double? Delta(double? adjusted, double? baseline) =>
            adjusted.HasValue && baseline.HasValue
                ? Math.Round(adjusted.Value - baseline.Value, 2)
                : null;

        private static double Confidence(MatchPrediction prediction) =>
            Math.Max(prediction.Outcome.HomeWin, Math.Max(prediction.Outcome.Draw, prediction.Outcome.AwayWin));
    }

    public sealed class PredictionAdjustmentSignal
    {
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public bool Applied { get; init; }
        public bool Available { get; init; }
        public bool Modeled { get; init; }
    }
}
