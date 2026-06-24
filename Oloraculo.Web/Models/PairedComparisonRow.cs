namespace Oloraculo.Web.Models
{
    public class PairedComparisonRow
    {
        public int PairCount { get; init; }

        public double BaselineMeanBrier { get; init; }
        public double BaselineMeanRps { get; init; }
        public double BaselineMeanLogLoss { get; init; }
        public double BaselineTopPickAccuracy { get; init; }

        public double ContextMeanBrier { get; init; }
        public double ContextMeanRps { get; init; }
        public double ContextMeanLogLoss { get; init; }
        public double ContextTopPickAccuracy { get; init; }

        public double DeltaBrier { get; init; }
        public double DeltaRps { get; init; }
        public double DeltaLogLoss { get; init; }
        public double DeltaTopPickAccuracy { get; init; }
    }
}
