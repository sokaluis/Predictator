namespace Oloraculo.Web.Models
{
    public class PredictionCriteria
    {
        public IReadOnlyList<PredictionSignal> Signals { get; init; } = [];

        public IEnumerable<PredictionSignal> Applied => Signals.Where(s => s.Status == SignalStatus.Applied);
        public IEnumerable<PredictionSignal> Available => Signals.Where(s => s.Status == SignalStatus.Available);
        public IEnumerable<PredictionSignal> Missing => Signals.Where(s => s.Status == SignalStatus.Missing);

        public string? SelectedPredictorName { get; init; }
        public bool HasRankingBias { get; init; }
    }
}
