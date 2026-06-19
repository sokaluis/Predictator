using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.ApiFootballModels;

namespace Oloraculo.Web.Services
{
    public sealed class ExternalDataStatusService(OloraculoDbContext db, IOptions<OloraculoConfig> options)
    {
        private readonly OloraculoConfig _config = options.Value;

        public async Task<ExternalDataStatus> GetAsync(ApiFootballRefreshReport? latestApiReport = null, CancellationToken ct = default)
        {
            var counts = new ExternalDataCounts(
                ApiMappings: await db.ApiMappings.AsNoTracking().CountAsync(ct),
                FixtureContexts: await db.FixtureContexts.AsNoTracking().CountAsync(ct),
                AvailabilitySources: await db.AvailabilitySources.AsNoTracking().CountAsync(ct),
                AvailabilityClaims: await db.AvailabilityClaims.AsNoTracking().CountAsync(ct),
                ContextsWithAvailabilityNews: await db.FixtureContexts.AsNoTracking().CountAsync(c => c.HasAvailabilityNews, ct),
                ContextsWithLineups: await db.FixtureContexts.AsNoTracking().CountAsync(c => c.HasLineups, ct),
                ContextsWithOdds: await db.FixtureContexts.AsNoTracking().CountAsync(c => c.HasOdds, ct));

            return Create(
                !string.IsNullOrWhiteSpace(_config.ApiFootballApiKey),
                !string.IsNullOrWhiteSpace(_config.OpenRouterApiKey),
                counts,
                latestApiReport?.Errors ?? []);
        }

        public static ExternalDataStatus Create(
            bool apiFootballConfigured,
            bool openRouterConfigured,
            ExternalDataCounts counts,
            IReadOnlyList<string>? latestProviderErrors = null)
        {
            var providerErrors = latestProviderErrors?.Where(error => !string.IsNullOrWhiteSpace(error)).ToList() ?? [];
            var hasPersistedEnrichment = counts.ContextsWithAvailabilityNews > 0 || counts.ContextsWithLineups > 0 || counts.ContextsWithOdds > 0 || counts.AvailabilityClaims > 0;
            var hasExternalRows = counts.ApiMappings > 0 || counts.FixtureContexts > 0 || counts.AvailabilitySources > 0 || counts.AvailabilityClaims > 0;
            var apiAccessUnknown = apiFootballConfigured && counts.ApiMappings == 0 && providerErrors.Count == 0;

            var summary = (apiFootballConfigured, openRouterConfigured, providerErrors.Count, hasPersistedEnrichment, hasExternalRows) switch
            {
                (false, false, _, _, _) => "Fuentes externas no configuradas: predicciones degradan a señales base.",
                (_, _, > 0, _, _) => "Sin acceso/datos externos: predicciones degradan a señales base hasta resolver proveedor o plan.",
                (_, _, _, true, _) => "Enriquecimiento parcial/activo: hay contexto externo aplicado a predicciones.",
                (_, _, _, false, true) => "Datos externos parciales: hay filas guardadas, pero sin señales enriquecidas activas todavía.",
                _ => "Sin datos externos: predicciones degradan a señales base."
            };

            var tone = providerErrors.Count > 0 || (!apiFootballConfigured && !openRouterConfigured)
                ? ExternalDataStatusTone.Error
                : hasPersistedEnrichment
                    ? ExternalDataStatusTone.Success
                    : ExternalDataStatusTone.Warning;

            return new ExternalDataStatus
            {
                ApiFootballConfigured = apiFootballConfigured,
                OpenRouterConfigured = openRouterConfigured,
                ApiMappings = counts.ApiMappings,
                FixtureContexts = counts.FixtureContexts,
                AvailabilitySources = counts.AvailabilitySources,
                AvailabilityClaims = counts.AvailabilityClaims,
                ContextsWithAvailabilityNews = counts.ContextsWithAvailabilityNews,
                ContextsWithLineups = counts.ContextsWithLineups,
                ContextsWithOdds = counts.ContextsWithOdds,
                LatestProviderErrors = providerErrors,
                Summary = summary,
                Tone = tone,
                AccessValidationNote = apiAccessUnknown
                    ? "No hay mapeos API guardados. El último error de proveedor no se persiste: ejecutá refresh para validar acceso actual."
                    : null
            };
        }
    }

    public sealed record ExternalDataCounts(
        int ApiMappings,
        int FixtureContexts,
        int AvailabilitySources,
        int AvailabilityClaims,
        int ContextsWithAvailabilityNews,
        int ContextsWithLineups,
        int ContextsWithOdds);
}
