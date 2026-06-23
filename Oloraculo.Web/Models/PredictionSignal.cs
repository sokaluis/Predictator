namespace Oloraculo.Web.Models
{
    public class PredictionSignal
    {
        public required string Name { get; init; }
        public SignalCategory Category { get; init; }
        public SignalStatus Status { get; init; }
        public string? Detail { get; init; }
    }
}
