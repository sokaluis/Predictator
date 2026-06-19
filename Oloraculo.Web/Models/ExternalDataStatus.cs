namespace Oloraculo.Web.Models
{
    public sealed class ExternalDataStatus
    {
        public bool ApiFootballConfigured { get; init; }
        public bool OpenRouterConfigured { get; init; }
        public int ApiMappings { get; init; }
        public int FixtureContexts { get; init; }
        public int AvailabilitySources { get; init; }
        public int AvailabilityClaims { get; init; }
        public int ContextsWithAvailabilityNews { get; init; }
        public int ContextsWithLineups { get; init; }
        public int ContextsWithOdds { get; init; }
        public IReadOnlyList<string> LatestProviderErrors { get; init; } = [];
        public string Summary { get; init; } = "Sin datos externos: predicciones degradan a señales base.";
        public ExternalDataStatusTone Tone { get; init; } = ExternalDataStatusTone.Warning;
        public string? AccessValidationNote { get; init; }
    }

    public enum ExternalDataStatusTone
    {
        Info,
        Success,
        Warning,
        Error
    }
}
