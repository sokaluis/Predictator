using Oloraculo.Web.Models.CsvModels;
using Oloraculo.Web.Services.Backtest;

namespace Oloraculo.Web.Tests.Services.Backtest;

public class RollingBacktestReportServiceTests
{
    [Fact]
    public void LoadScoredResults_FiltersUnplayedInvalidNegativeAndDuplicateRows()
    {
        var rows = new[]
        {
            Row("2024-01-01", "A", "B", "1", "0"),
            Row("2024-01-01", "A", "B", "1", "0"),
            Row("2024-01-02", "A", "C", "NA", "NA"),
            Row("not-a-date", "A", "D", "1", "0"),
            Row("2024-01-03", "A", "E", "-1", "0")
        };

        var result = RollingBacktestReportService.LoadScoredResults(rows);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(1, result.IncludedRows);
        Assert.Equal(3, result.ExcludedUnscoredOrInvalid);
        Assert.Equal(1, result.DuplicateRows);
        Assert.Equal("a", result.Results[0].HomeTeamId);
        Assert.Equal("b", result.Results[0].AwayTeamId);
    }

    [Fact]
    public void LoadScoredResults_AppliesDateTournamentAndTakeOptions()
    {
        var rows = new[]
        {
            Row("2024-01-01", "A", "B", "1", "0", tournament: "Friendly"),
            Row("2024-01-02", "B", "C", "1", "1", tournament: "Cup"),
            Row("2024-01-03", "C", "D", "0", "1", tournament: "Cup"),
            Row("2024-01-04", "D", "E", "2", "0", tournament: "Cup")
        };
        var options = new BacktestReportOptions(
            From: DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
            To: DateTimeOffset.Parse("2024-01-04T00:00:00Z"),
            Tournament: "Cup",
            Take: 2);

        var result = RollingBacktestReportService.LoadScoredResults(rows, options);

        Assert.Equal(2, result.IncludedRows);
        Assert.Equal(2, result.FilteredOutByOptions);
        Assert.Equal(["b", "c"], result.Results.Select(match => match.HomeTeamId));
    }

    [Fact]
    public void GenerateFromCsv_EvaluationWindowFiltersTargetsWithoutDroppingPriorHistory()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine,
            [
                "date,home_team,away_team,home_score,away_score,tournament,neutral",
                "2024-01-01,C,A,1,1,Friendly,TRUE",
                "2024-01-01,B,C,2,0,Friendly,TRUE",
                "2024-01-02,A,B,1,0,Friendly,TRUE",
                "2024-01-03,A,B,0,1,Friendly,TRUE"
            ]));
            var options = new BacktestReportOptions(
                EvaluateFrom: DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
                EvaluateTo: DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
                MinimumPriorMatchesPerTeam: 1);

            var report = new RollingBacktestReportService().GenerateFromCsv(path, options);

            Assert.Equal(4, report.LoadResult.IncludedRows);
            Assert.All(report.Summaries, summary => Assert.Equal(1, summary.Count));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseOptions_RecognizesEvaluationWindow()
    {
        var options = RollingBacktestReportService.ParseOptions(
        [
            "--backtest-report",
            "--evaluate-from=2024-01-02",
            "--evaluate-to=2024-01-03"
        ]);

        Assert.Equal(DateTimeOffset.Parse("2024-01-02T00:00:00Z"), options.EvaluateFrom);
        Assert.Equal(DateTimeOffset.Parse("2024-01-03T00:00:00Z"), options.EvaluateTo);
    }

    [Theory]
    [InlineData("all", BacktestMatchSegmentClassifier.AllMatches)]
    [InlineData("all-matches", BacktestMatchSegmentClassifier.AllMatches)]
    [InlineData("friendly", BacktestMatchSegmentClassifier.Friendlies)]
    [InlineData("friendlies", BacktestMatchSegmentClassifier.Friendlies)]
    [InlineData("world-cup-qualifiers", BacktestMatchSegmentClassifier.WorldCupQualifiers)]
    [InlineData("wc-qualifiers", BacktestMatchSegmentClassifier.WorldCupQualifiers)]
    [InlineData("qualifiers", BacktestMatchSegmentClassifier.WorldCupQualifiers)]
    [InlineData("world-cup-finals", BacktestMatchSegmentClassifier.WorldCupFinals)]
    [InlineData("wc-finals", BacktestMatchSegmentClassifier.WorldCupFinals)]
    [InlineData("world-cup", BacktestMatchSegmentClassifier.WorldCupFinals)]
    [InlineData("other-official", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    [InlineData("official", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    [InlineData("other", BacktestMatchSegmentClassifier.OtherOfficialTournaments)]
    [InlineData("World Cup qualifiers", BacktestMatchSegmentClassifier.WorldCupQualifiers)]
    public void ParseOptions_RecognizesSegmentAliases(string value, string expected)
    {
        var options = RollingBacktestReportService.ParseOptions(["--backtest-report", $"--segment={value}"]);

        Assert.Equal(expected, options.Segment);
    }

    [Fact]
    public void ParseOptions_RejectsInvalidSegment()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            RollingBacktestReportService.ParseOptions(["--backtest-report", "--segment=made-up"]));

        Assert.Contains("Unknown --segment value 'made-up'", exception.Message);
        Assert.Contains("Accepted values", exception.Message);
    }

    [Fact]
    public void Render_PrintsRequiredMetricColumnsAndLimitations()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("| Model | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |", output);
        Assert.Contains("| Modelo base | 2 | 0.6670 | 1.0990 | 0.3330 | 50.0 % |", output);
        Assert.Contains("Elo, FIFA ranking, and RecentForm are intentionally excluded", output);
    }

    [Fact]
    public void Render_PrintsSegmentSummaryTableWhenPresent()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo base", 1, 0.500, 0.700, 0.200, 1.0))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Performance by match type", output);
        Assert.Contains("| Segment | Model | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |", output);
        Assert.Contains("| Friendlies | Modelo base | 1 | 0.5000 | 0.7000 | 0.2000 | 100.0 % |", output);
    }

    [Fact]
    public void Render_PrintsSelectedSegmentWhenPresent()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [])
        {
            Options = new BacktestReportOptions(Segment: BacktestMatchSegmentClassifier.WorldCupQualifiers)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("Segment: World Cup qualifiers", output);
    }

    [Fact]
    public void GenerateFromCsv_AddsSegmentSummariesFromEvaluatedTargets()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine,
            [
                "date,home_team,away_team,home_score,away_score,tournament,neutral",
                "2024-01-01,C,A,1,1,Friendly,TRUE",
                "2024-01-01,B,C,2,0,Friendly,TRUE",
                "2024-01-02,A,B,1,0,FIFA World Cup,TRUE",
                "2024-01-03,A,B,0,1,FIFA World Cup qualification,TRUE"
            ]));

            var report = new RollingBacktestReportService().GenerateFromCsv(path, new BacktestReportOptions(MinimumPriorMatchesPerTeam: 1));

            Assert.Contains(report.SegmentSummaries, summary =>
                summary.SegmentName == BacktestMatchSegmentClassifier.AllMatches &&
                summary.Summary.ModelName == "Modelo base" &&
                summary.Summary.Count == 2);
            Assert.Contains(report.SegmentSummaries, summary =>
                summary.SegmentName == BacktestMatchSegmentClassifier.WorldCupFinals &&
                summary.Summary.ModelName == "Modelo base" &&
                summary.Summary.Count == 1);
            Assert.Contains(report.SegmentSummaries, summary =>
                summary.SegmentName == BacktestMatchSegmentClassifier.WorldCupQualifiers &&
                summary.Summary.ModelName == "Modelo base" &&
                summary.Summary.Count == 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GenerateFromCsv_SegmentFiltersTargetsWithoutDroppingPriorHistory()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine,
            [
                "date,home_team,away_team,home_score,away_score,tournament,neutral",
                "2024-01-01,C,A,1,1,Friendly,TRUE",
                "2024-01-01,B,C,2,0,Friendly,TRUE",
                "2024-01-02,A,B,1,0,FIFA World Cup,TRUE",
                "2024-01-03,A,B,0,1,Friendly,TRUE"
            ]));
            var options = new BacktestReportOptions(
                Segment: BacktestMatchSegmentClassifier.WorldCupFinals,
                MinimumPriorMatchesPerTeam: 1);

            var report = new RollingBacktestReportService().GenerateFromCsv(path, options);

            Assert.Equal(4, report.LoadResult.IncludedRows);
            Assert.All(report.Summaries, summary => Assert.Equal(1, summary.Count));
            Assert.Contains(report.SegmentSummaries, summary =>
                summary.SegmentName == BacktestMatchSegmentClassifier.WorldCupFinals &&
                summary.Summary.ModelName == "Modelo base" &&
                summary.Summary.Count == 1);
            Assert.DoesNotContain(report.SegmentSummaries, summary =>
                summary.SegmentName == BacktestMatchSegmentClassifier.AllMatches);
            Assert.DoesNotContain(report.SegmentSummaries, summary =>
                summary.SegmentName == BacktestMatchSegmentClassifier.Friendlies);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PrintsEvaluationWindowWhenPresent()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [])
        {
            Options = new BacktestReportOptions(
                EvaluateFrom: DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
                EvaluateTo: DateTimeOffset.Parse("2024-01-03T00:00:00Z"))
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("Evaluation window: 2024-01-02 to 2024-01-03", output);
    }

    private static HistoricalResultCsvRow Row(
        string date,
        string home,
        string away,
        string homeScore,
        string awayScore,
        string tournament = "Friendly") =>
        new()
        {
            Date = date,
            HomeTeam = home,
            AwayTeam = away,
            HomeScore = homeScore,
            AwayScore = awayScore,
            Tournament = tournament,
            Neutral = "TRUE"
        };
}
