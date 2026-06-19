using Oloraculo.Web.Services.Backtest;

namespace Oloraculo.Web.Tests.Services.Backtest;

public class BacktestMatchSegmentClassifierTests
{
    [Theory]
    [InlineData("Friendly", BacktestMatchSegmentClassifier.Friendlies)]
    [InlineData(" friendly ", BacktestMatchSegmentClassifier.Friendlies)]
    [InlineData("FIFA World Cup qualification", BacktestMatchSegmentClassifier.WorldCupQualifiers)]
    [InlineData("FIFA World Cup", BacktestMatchSegmentClassifier.WorldCupFinals)]
    [InlineData("Copa América", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    [InlineData("UEFA Euro qualification", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    [InlineData("CONIFA World Cup qualification", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    [InlineData("", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    public void Classify_MapsKnownHistoricalTournamentValuesToSegments(string tournament, string expected)
    {
        Assert.Equal(expected, BacktestMatchSegmentClassifier.Classify(tournament));
    }
}
