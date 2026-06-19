using Oloraculo.Web.Models;
using Oloraculo.Web.Services;

namespace Oloraculo.Web.Tests;

public class ExternalDataStatusServiceTests
{
    [Fact]
    public void ExternalDataStatus_ReportsProviderErrorsAsAccessDegradation()
    {
        var status = ExternalDataStatusService.Create(
            apiFootballConfigured: true,
            openRouterConfigured: true,
            new ExternalDataCounts(0, 72, 2, 0, 0, 0, 0),
            ["partidos: plan: Free plans do not have access to this season."]);

        Assert.Equal(ExternalDataStatusTone.Error, status.Tone);
        Assert.Contains("Sin acceso/datos externos", status.Summary);
        Assert.Contains(status.LatestProviderErrors, error => error.Contains("plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExternalDataStatus_DistinguishesConfiguredButUnvalidatedAccess()
    {
        var status = ExternalDataStatusService.Create(
            apiFootballConfigured: true,
            openRouterConfigured: false,
            new ExternalDataCounts(0, 0, 0, 0, 0, 0, 0));

        Assert.Equal(ExternalDataStatusTone.Warning, status.Tone);
        Assert.Contains("Sin datos externos", status.Summary);
        Assert.Contains("ejecutá refresh", status.AccessValidationNote);
    }

    [Fact]
    public void ExternalDataStatus_ReportsActiveEnrichmentWhenContextSignalsExist()
    {
        var status = ExternalDataStatusService.Create(
            apiFootballConfigured: true,
            openRouterConfigured: true,
            new ExternalDataCounts(72, 72, 2, 3, 2, 1, 1));

        Assert.Equal(ExternalDataStatusTone.Success, status.Tone);
        Assert.Contains("Enriquecimiento parcial/activo", status.Summary);
    }
}
