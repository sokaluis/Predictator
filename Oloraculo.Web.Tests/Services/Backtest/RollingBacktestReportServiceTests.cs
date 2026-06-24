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
    public void Render_PrintsPoissonDeltaVsBaselineBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo base", 2, 0.600, 0.900, 0.300, 0.50)),
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo de goles (Poisson)", 2, 0.550, 0.950, 0.250, 0.75))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Delta vs baseline by match type", output);
        Assert.Contains("| Segment | Targets | ΔBrier | ΔLogLoss | ΔRPS | ΔTopPickAccuracy |", output);
        Assert.Contains("| Friendlies | 2 | -0.0500 | +0.0500 | -0.0500 | +25.0 pp |", output);
    }

    [Fact]
    public void Render_PrintsConfidenceGuidanceWhenPoissonDeltaRowsExist()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [])
        {
            SegmentSummaries =
            [
                SegmentSummary(BacktestMatchSegmentClassifier.Friendlies, "Modelo base", 2_238, 0.6000, 0.9000, 0.3000, 0.5000),
                SegmentSummary(BacktestMatchSegmentClassifier.Friendlies, "Modelo de goles (Poisson)", 2_238, 0.4964, 0.7497, 0.2530, 0.5480),
                SegmentSummary(BacktestMatchSegmentClassifier.WorldCupQualifiers, "Modelo base", 1_767, 0.7000, 1.1000, 0.3000, 0.4500),
                SegmentSummary(BacktestMatchSegmentClassifier.WorldCupQualifiers, "Modelo de goles (Poisson)", 1_767, 0.4947, 0.7954, 0.2089, 0.6220),
                SegmentSummary(BacktestMatchSegmentClassifier.WorldCupFinals, "Modelo base", 136, 0.5000, 0.8000, 0.2000, 0.4000),
                SegmentSummary(BacktestMatchSegmentClassifier.WorldCupFinals, "Modelo de goles (Poisson)", 136, 0.4254, 0.6967, 0.1673, 0.5690),
                SegmentSummary(BacktestMatchSegmentClassifier.OtherOfficialTournaments, "Modelo base", 3_925, 0.6500, 1.0000, 0.2500, 0.4800),
                SegmentSummary(BacktestMatchSegmentClassifier.OtherOfficialTournaments, "Modelo de goles (Poisson)", 3_925, 0.5004, 0.7814, 0.1841, 0.6170)
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Confidence guidance by match type", output);
        Assert.Contains("| Friendlies | Lower confidence / noisy |", output);
        Assert.Contains("rotation and experimental lineups", output);
        Assert.Contains("| World Cup qualifiers | High confidence / strongest signal |", output);
        Assert.Contains("| World Cup finals | Limited sample — cautious |", output);
        Assert.Contains("| Other official tournaments | Good confidence |", output);
        Assert.Contains("broad and heterogeneous", output);
    }

    [Fact]
    public void Render_PrintsFocusedPoissonDeltaOnlyForSelectedSegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [])
        {
            Options = new BacktestReportOptions(Segment: BacktestMatchSegmentClassifier.WorldCupQualifiers),
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    new BacktestModelSummary("Modelo base", 3, 0.700, 1.100, 0.400, 0.333)),
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    new BacktestModelSummary("Modelo de goles (Poisson)", 3, 0.650, 1.000, 0.350, 0.667))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("Segment: World Cup qualifiers", output);
        Assert.Contains("| World Cup qualifiers | 3 | -0.0500 | -0.1000 | -0.0500 | +33.4 pp |", output);
        Assert.DoesNotContain("| Friendlies |", output);
    }

    [Fact]
    public void Render_PrintsFocusedConfidenceGuidanceOnlyForSelectedSegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [])
        {
            Options = new BacktestReportOptions(Segment: BacktestMatchSegmentClassifier.WorldCupQualifiers),
            SegmentSummaries =
            [
                SegmentSummary(BacktestMatchSegmentClassifier.WorldCupQualifiers, "Modelo base", 1_767, 0.7000, 1.1000, 0.3000, 0.4500),
                SegmentSummary(BacktestMatchSegmentClassifier.WorldCupQualifiers, "Modelo de goles (Poisson)", 1_767, 0.4947, 0.7954, 0.2089, 0.6220)
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Confidence guidance by match type", output);
        Assert.Contains("| World Cup qualifiers | High confidence / strongest signal |", output);
        Assert.DoesNotContain("| Friendlies |", output);
        Assert.DoesNotContain("| World Cup finals |", output);
    }

    [Fact]
    public void Render_SkipsDeltaTableWhenPoissonComparisonIsMissing()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [])
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
        Assert.DoesNotContain("## Delta vs baseline by match type", output);
        Assert.DoesNotContain("## Confidence guidance by match type", output);
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

    [Fact]
    public void GenerateFromCsv_IncludesCoverageInReport()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine,
            [
                "date,home_team,away_team,home_score,away_score,tournament,neutral",
                "2024-01-01,C,A,1,1,Friendly,TRUE",
                "2024-01-01,B,C,2,0,Friendly,TRUE",
                "2024-01-02,A,B,1,0,FIFA World Cup,TRUE"
            ]));

            var report = new RollingBacktestReportService().GenerateFromCsv(path,
                new BacktestReportOptions(MinimumPriorMatchesPerTeam: 1));

            Assert.NotNull(report.Coverage);
            Assert.Equal(1, report.Coverage.EligibleTargets);
            Assert.Equal(0, report.Coverage.EloCoveredTargets);
            Assert.Equal(0, report.Coverage.FifaCoveredTargets);
            Assert.False(report.Coverage.EloEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PrintsCoverageInfoWhenCoverageAvailable()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)])
        {
            Coverage = new BacktestCoverageInfo(500, 450, 200, true, true)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("Rating snapshot coverage", output);
        Assert.Contains("Eligible targets: 500", output);
        Assert.Contains("Elo: 450/500 targets (90.0%) — enabled", output);
        Assert.Contains("FIFA: 200/500 targets (40.0%) — enabled", output);
        Assert.Contains("RecentForm: enabled (requires Elo coverage)", output);
    }

    [Fact]
    public void Render_PrintsCoverageInfoWithDisabledModels()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)])
        {
            Coverage = new BacktestCoverageInfo(500, 0, 0, false, false)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("Rating snapshot coverage", output);
        Assert.Contains("Eligible targets: 500", output);
        Assert.Contains("Elo: 0/500 targets (0.0%) — disabled, no as-of snapshot pairs", output);
        Assert.Contains("FIFA: 0/500 targets (0.0%) — disabled, no as-of snapshot pairs", output);
        Assert.Contains("RecentForm: disabled (requires Elo coverage)", output);
        Assert.Contains("Limitations: Elo, FIFA ranking, and RecentForm are intentionally excluded", output);
    }

    [Fact]
    public void Render_PrintsCoverageInfoWithEloOnly()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(3, 2, 1, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)])
        {
            Coverage = new BacktestCoverageInfo(300, 250, 0, true, false)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("Elo: 250/300 targets (83.3%) — enabled", output);
        Assert.Contains("FIFA: 0/300 targets (0.0%) — disabled, no as-of snapshot pairs", output);
        Assert.Contains("RecentForm: enabled (requires Elo coverage)", output);
        Assert.DoesNotContain("Limitations:", output);
    }

    [Fact]
    public void Render_PrintsReadinessSectionWhenRatingAwareModelsPresent()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Elo", 2, 0.500, 0.800, 0.250, 0.6)
                {
                    SignalBackedCount = 2,
                    DegradedCount = 0
                },
                new BacktestModelSummary("Ranking FIFA", 2, 0.550, 0.900, 0.280, 0.4)
                {
                    SignalBackedCount = 1,
                    DegradedCount = 1
                }
            ])
        {
            Coverage = new BacktestCoverageInfo(2, 2, 2, true, true)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Model readiness and degraded coverage", output);
        Assert.Contains("| Model | Evaluated | Signal-backed | Degraded | Readiness |", output);
        Assert.Contains("| Elo | 2 | 2 | 0 | 100.0%", output);
        Assert.Contains("| Ranking FIFA | 2 | 1 | 1 | 50.0% — ⚠ degraded fallback", output);
        Assert.Contains("Ranking FIFA: 1/2 predictions fell back", output);
        Assert.DoesNotContain("Non-rating-dependent models always signal-backed:", output);
    }

    [Fact]
    public void Render_SkipsReadinessSectionWhenNoRatingAwareModels()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Modelo de goles (Poisson)", 2, 0.550, 0.900, 0.280, 0.6)
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Model readiness and degraded coverage", output);
    }

    [Fact]
    public void Render_ReadinessSectionShowsOnlyRatingDependentModels()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Modelo de goles (Poisson)", 2, 0.550, 0.900, 0.280, 0.6),
                new BacktestModelSummary("Elo", 2, 0.500, 0.800, 0.250, 0.6)
                {
                    SignalBackedCount = 2,
                    DegradedCount = 0
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Model readiness and degraded coverage", output);
        Assert.Contains("| Elo | 2 | 2 | 0 | 100.0%", output);
        var readinessStart = output.IndexOf("## Model readiness and degraded coverage", StringComparison.Ordinal);
        var metricsStart = output.IndexOf("| Model | Count |", StringComparison.Ordinal);
        var readinessSection = output[readinessStart..metricsStart];
        // The readiness table should NOT contain non-rating-dependent models
        Assert.DoesNotContain("| Modelo base |", readinessSection);
        Assert.DoesNotContain("| Modelo de goles (Poisson) |", readinessSection);
    }

    [Fact]
    public void Render_ReadinessSectionShowsAllDegradedWhenNoSnapshotPairs()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Elo", 3, 0.667, 1.099, 0.333, 0.5)
                {
                    SignalBackedCount = 0,
                    DegradedCount = 3
                }
            ])
        {
            Coverage = new BacktestCoverageInfo(3, 0, 0, false, false)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("| Elo | 3 | 0 | 3 | 0.0% — ⚠ degraded fallback", output);
        Assert.Contains("Elo: 3/3 predictions fell back", output);
    }

    [Fact]
    public void Render_ReadinessSectionIncludesDegradedReasonBreakdown()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Elo", 4, 0.500, 0.800, 0.250, 0.6)
                {
                    SignalBackedCount = 0,
                    DegradedCount = 4,
                    DegradedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ratings Elo"] = 4
                    }
                },
                new BacktestModelSummary("Ranking FIFA", 2, 0.550, 0.900, 0.280, 0.4)
                {
                    SignalBackedCount = 0,
                    DegradedCount = 2,
                    DegradedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ranking FIFA"] = 2
                    }
                }
            ])
        {
            Coverage = new BacktestCoverageInfo(6, 0, 0, false, false)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Model readiness and degraded coverage", output);
        Assert.Contains("| Elo | 4 | 0 | 4 | 0.0% — ⚠ degraded fallback", output);
        Assert.Contains("| Ranking FIFA | 2 | 0 | 2 | 0.0% — ⚠ degraded fallback", output);
        Assert.Contains("- Elo: 4/4 predictions fell back", output);
        Assert.Contains("  • ratings Elo: 4", output);
        Assert.Contains("- Ranking FIFA: 2/2 predictions fell back", output);
        Assert.Contains("  • ranking FIFA: 2", output);
    }

    [Fact]
    public void Render_ReadinessSectionOmitsReasonBulletsWhenNoReasonCountsAvailable()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Elo", 3, 0.667, 1.099, 0.333, 0.5)
                {
                    SignalBackedCount = 0,
                    DegradedCount = 3,
                    DegradedReasonCounts = new Dictionary<string, int>()
                }
            ])
        {
            Coverage = new BacktestCoverageInfo(3, 0, 0, false, false)
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("- Elo: 3/3 predictions fell back", output);
        // The reason bullet prefix should NOT appear when no reasons are tracked
        Assert.DoesNotContain("  • ", output);
    }

    [Fact]
    public void Render_PrintsOracleSelectorBreakdownWhenOracleSummaryExists()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 2, 0.450, 0.750, 0.200, 0.75)
                {
                    ChosenPredictorCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = 2
                    }
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — selector breakdown", output);
        Assert.Contains("fixture-context signals are not replayed here", output);
        Assert.Contains("| Chosen predictor | Count |", output);
        Assert.Contains("| Modelo de goles (Poisson) | 2 |", output);
    }

    [Fact]
    public void Render_OmitsOracleSelectorBreakdownWhenOracleMissing()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final", output);
    }

    [Fact]
    public void Render_OmitsOracleSelectorBreakdownWhenNoChosenPredictorCounts()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 2, 0.450, 0.750, 0.200, 0.75)
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — selector breakdown", output);
    }

    [Fact]
    public void Render_OracleSelectorBreakdownOrdersByDescendingCount()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)
                {
                    ChosenPredictorCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = 3,
                        ["Goles + contexto reciente"] = 2,
                        ["Elo"] = 1
                    }
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        var breakdownIndex = output.IndexOf("## Oráculo final — selector breakdown", StringComparison.Ordinal);
        var modelGolesIndex = output.IndexOf("| Modelo de goles (Poisson) |", breakdownIndex, StringComparison.Ordinal);
        var contextoIndex = output.IndexOf("| Goles + contexto reciente |", breakdownIndex, StringComparison.Ordinal);
        var eloIndex = output.IndexOf("| Elo |", breakdownIndex, StringComparison.Ordinal);

        Assert.True(breakdownIndex >= 0);
        Assert.True(modelGolesIndex < contextoIndex);
        Assert.True(contextoIndex < eloIndex);
    }

    [Fact]
    public void Render_PrintsOracleChosenPredictorCountsBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 6, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)
            ])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracle(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 4,
                    new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = 3,
                        ["Elo"] = 1
                    }),
                SegmentSummaryWithOracle(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["Goles + contexto reciente"] = 2
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — chosen predictor counts by segment", output);
        Assert.Contains("| Segment | Chosen predictor | Count |", output);
        Assert.Contains("| Friendlies | Modelo de goles (Poisson) | 3 |", output);
        Assert.Contains("| Friendlies | Elo | 1 |", output);
        Assert.Contains("| World Cup qualifiers | Goles + contexto reciente | 2 |", output);
    }

    [Fact]
    public void Render_OmitsOracleSegmentBreakdownWhenNoOracleSegments()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 4, 0.667, 1.099, 0.333, 0.5)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo base", 2, 0.500, 0.700, 0.200, 1.0))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor counts by segment", output);
    }

    [Fact]
    public void Render_OmitsOracleSegmentBreakdownWhenOracleSegmentsHaveNoChosenCounts()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Oráculo final", 2, 0.400, 0.700, 0.190, 0.80))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor counts by segment", output);
    }

    [Fact]
    public void Render_OracleSegmentBreakdownKeepsSegmentSummaryOrder()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracle(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    new Dictionary<string, int>(StringComparer.Ordinal) { ["Elo"] = 2 }),
                SegmentSummaryWithOracle(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2,
                    new Dictionary<string, int>(StringComparer.Ordinal) { ["Elo"] = 2 })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf("## Oráculo final — chosen predictor counts by segment", StringComparison.Ordinal);
        var friendliesIndex = output.IndexOf("| Friendlies |", sectionStart, StringComparison.Ordinal);
        var wcQualifiersIndex = output.IndexOf("| World Cup qualifiers |", sectionStart, StringComparison.Ordinal);

        Assert.True(wcQualifiersIndex < friendliesIndex);
    }

    [Fact]
    public void Render_PrintsOracleRankingBiasBreakdown()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 2, 0.450, 0.750, 0.200, 0.75)
                {
                    RankingBiasAppliedCount = 1,
                    RankingBiasNotAppliedCount = 1
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias", output);
        Assert.Contains("Shows how often the Elo/FIFA calibration was applied", output);
        Assert.Contains("| Bias applied? | Count | Pct |", output);
        Assert.Contains("| Yes | 1 | 50.0% |", output);
        Assert.Contains("| No | 1 | 50.0% |", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasBreakdownWhenOracleMissing()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasBreakdownWhenOracleHasZeroEvaluations()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 0, 0.0, 0.0, 0.0, 0.0)
                {
                    RankingBiasAppliedCount = 0,
                    RankingBiasNotAppliedCount = 0
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias", output);
    }

    [Fact]
    public void Render_PrintsOracleRankingBiasBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBias(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 3, applied: 2, notApplied: 1),
                SegmentSummaryWithOracleBias(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 3, applied: 1, notApplied: 2)
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias by segment", output);
        Assert.Contains("| Segment | Applied | Not applied | Applied % |", output);
        Assert.Contains("| Friendlies | 2 | 1 | 66.7% |", output);
        Assert.Contains("| World Cup qualifiers | 1 | 2 | 33.3% |", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasBySegmentWhenNoOracleSegments()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 4, 0.667, 1.099, 0.333, 0.5)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo base", 2, 0.500, 0.700, 0.200, 1.0))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias by segment", output);
    }

    [Fact]
    public void Render_RankingBiasBySegmentRespectsSegmentOrder()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBias(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2, applied: 1, notApplied: 1),
                SegmentSummaryWithOracleBias(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2, applied: 1, notApplied: 1)
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf("## Oráculo final — ranking bias by segment", StringComparison.Ordinal);
        var friendliesIndex = output.IndexOf("| Friendlies |", sectionStart, StringComparison.Ordinal);
        var wcQualifiersIndex = output.IndexOf("| World Cup qualifiers |", sectionStart, StringComparison.Ordinal);

        Assert.True(wcQualifiersIndex < friendliesIndex);
    }

    [Fact]
    public void Render_PrintsOracleRankingBiasSubgroupMetrics()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)
                {
                    RankingBiasAppliedCount = 3,
                    RankingBiasNotAppliedCount = 3,
                    RankingBiasAppliedSummary = new BacktestBiasGroupSummary(3, 0.3500, 0.6200, 0.1700, 0.85),
                    RankingBiasNotAppliedSummary = new BacktestBiasGroupSummary(3, 0.4500, 0.7800, 0.2100, 0.75)
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias subgroup metrics", output);
        Assert.Contains("this is not a same-fixture counterfactual", output);
        Assert.Contains("Compares separate backtest subsets where the Elo/FIFA ranking bias calibration was applied", output);
        Assert.Contains("| Bias applied? | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |", output);
        Assert.Contains("| Yes | 3 | 0.3500 | 0.6200 | 0.1700 | 85.0 % |", output);
        Assert.Contains("| No | 3 | 0.4500 | 0.7800 | 0.2100 | 75.0 % |", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasSubgroupMetricsWhenOracleMissing()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias subgroup metrics", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasSubgroupMetricsWhenNoSubgroupSummaries()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 2, 0.450, 0.750, 0.200, 0.75)
                {
                    RankingBiasAppliedCount = 1,
                    RankingBiasNotAppliedCount = 1
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias subgroup metrics", output);
    }

    [Fact]
    public void Render_PrintsRankingBiasSubgroupMetricsWithOnlyAppliedGroup()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 2, 0.350, 0.620, 0.170, 0.85)
                {
                    RankingBiasAppliedCount = 2,
                    RankingBiasNotAppliedCount = 0,
                    RankingBiasAppliedSummary = new BacktestBiasGroupSummary(2, 0.3500, 0.6200, 0.1700, 0.85)
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias subgroup metrics", output);
        Assert.Contains("| Yes | 2 | 0.3500 | 0.6200 | 0.1700 | 85.0 % |", output);
        Assert.DoesNotContain("| No | 0.0000 |", output);
    }

    [Fact]
    public void Render_PrintsOracleRankingBiasSubgroupMetricsBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 3,
                    appliedSummary: new BacktestBiasGroupSummary(2, 0.3500, 0.6200, 0.1700, 0.85),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4500, 0.7800, 0.2100, 0.75)),
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 3,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3200, 0.5800, 0.1600, 0.90),
                    notAppliedSummary: new BacktestBiasGroupSummary(2, 0.4800, 0.8200, 0.2300, 0.70))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias subgroup metrics by segment", output);
        Assert.Contains("this is not a same-fixture counterfactual", output);
        Assert.Contains("| Segment | Ranking bias | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |", output);
        Assert.Contains("| Friendlies | Yes | 2 | 0.3500 | 0.6200 | 0.1700 | 85.0 % |", output);
        Assert.Contains("| Friendlies | No | 1 | 0.4500 | 0.7800 | 0.2100 | 75.0 % |", output);
        Assert.Contains("| World Cup qualifiers | Yes | 1 | 0.3200 | 0.5800 | 0.1600 | 90.0 % |", output);
        Assert.Contains("| World Cup qualifiers | No | 2 | 0.4800 | 0.8200 | 0.2300 | 70.0 % |", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasSubgroupMetricsBySegmentWhenNoOracleSegments()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 4, 0.667, 1.099, 0.333, 0.5)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo base", 2, 0.500, 0.700, 0.200, 1.0))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias subgroup metrics by segment", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasSubgroupMetricsBySegmentWhenOracleSegmentsHaveNoSubgroupData()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)
            {
                RankingBiasAppliedCount = 2,
                RankingBiasNotAppliedCount = 2
            }])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Oráculo final", 2, 0.400, 0.700, 0.190, 0.80)
                    {
                        RankingBiasAppliedCount = 1,
                        RankingBiasNotAppliedCount = 1
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias subgroup metrics by segment", output);
    }

    [Fact]
    public void Render_RankingBiasSubgroupMetricsBySegmentRespectsSegmentOrder()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3200, 0.5800, 0.1600, 0.90),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4800, 0.8200, 0.2300, 0.70)),
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3500, 0.6200, 0.1700, 0.85),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4500, 0.7800, 0.2100, 0.75))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf(
            "## Oráculo final — ranking bias subgroup metrics by segment", StringComparison.Ordinal);
        var friendliesIndex = output.IndexOf("| Friendlies |", sectionStart, StringComparison.Ordinal);
        var wcQualifiersIndex = output.IndexOf("| World Cup qualifiers |", sectionStart, StringComparison.Ordinal);

        Assert.True(wcQualifiersIndex < friendliesIndex);
    }

    [Fact]
    public void Render_PrintsRankingBiasSubgroupMetricsBySegment_FocusedSegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            Options = new BacktestReportOptions(Segment: BacktestMatchSegmentClassifier.Friendlies),
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 4,
                    appliedSummary: new BacktestBiasGroupSummary(2, 0.3500, 0.6200, 0.1700, 0.85),
                    notAppliedSummary: new BacktestBiasGroupSummary(2, 0.4500, 0.7800, 0.2100, 0.75))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias subgroup metrics by segment", output);
        Assert.Contains("| Friendlies | Yes | 2 | 0.3500 | 0.6200 | 0.1700 | 85.0 % |", output);
        Assert.Contains("| Friendlies | No | 2 | 0.4500 | 0.7800 | 0.2100 | 75.0 % |", output);
        Assert.DoesNotContain("| World Cup qualifiers |", output);
    }

    [Fact]
    public void Render_PrintsOracleRankingBiasDeltaSummary()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)
                {
                    RankingBiasAppliedCount = 3,
                    RankingBiasNotAppliedCount = 3,
                    RankingBiasAppliedSummary = new BacktestBiasGroupSummary(3, 0.3500, 0.6200, 0.1700, 0.85),
                    RankingBiasNotAppliedSummary = new BacktestBiasGroupSummary(3, 0.4500, 0.7800, 0.2100, 0.75)
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias delta summary (descriptive)", output);
        Assert.Contains("descriptive subgroup comparison, not causal lift", output);
        Assert.Contains("| Metric | Δ (Applied − Not applied) |", output);
        Assert.Contains("| ΔMeanBrier | -0.1000 |", output);
        Assert.Contains("| ΔMeanLogLoss | -0.1600 |", output);
        Assert.Contains("| ΔMeanRPS | -0.0400 |", output);
        Assert.Contains("| ΔTopPickAccuracy | +10.0 pp |", output);
    }

    [Fact]
    public void Render_OmitsRankingBiasDeltaSummaryWhenOneSideMissing()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 2, 0.350, 0.620, 0.170, 0.85)
                {
                    RankingBiasAppliedCount = 2,
                    RankingBiasNotAppliedCount = 0,
                    RankingBiasAppliedSummary = new BacktestBiasGroupSummary(2, 0.3500, 0.6200, 0.1700, 0.85)
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — ranking bias delta summary", output);
    }

    [Fact]
    public void Render_PrintsOracleRankingBiasDeltaSummaryBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 3,
                    appliedSummary: new BacktestBiasGroupSummary(2, 0.3500, 0.6200, 0.1700, 0.85),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4500, 0.7800, 0.2100, 0.75)),
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 3,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3200, 0.5800, 0.1600, 0.90),
                    notAppliedSummary: new BacktestBiasGroupSummary(2, 0.4800, 0.8200, 0.2300, 0.70))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias delta summary by segment (descriptive)", output);
        Assert.Contains("| Segment | ΔMeanBrier | ΔMeanLogLoss | ΔMeanRPS | ΔTopPickAccuracy |", output);
        Assert.Contains("| Friendlies | -0.1000 | -0.1600 | -0.0400 | +10.0 pp |", output);
        Assert.Contains("| World Cup qualifiers | -0.1600 | -0.2400 | -0.0700 | +20.0 pp |", output);
    }

    [Fact]
    public void Render_RankingBiasDeltaSummaryBySegmentRespectsSegmentOrder()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3200, 0.5800, 0.1600, 0.90),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4800, 0.8200, 0.2300, 0.70)),
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3500, 0.6200, 0.1700, 0.85),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4500, 0.7800, 0.2100, 0.75))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf(
            "## Oráculo final — ranking bias delta summary by segment", StringComparison.Ordinal);
        var sectionEnd = output.IndexOf("## Performance by match type", sectionStart, StringComparison.Ordinal);
        var deltaSection = output[sectionStart..sectionEnd];
        var friendliesIndex = deltaSection.IndexOf("| Friendlies |", StringComparison.Ordinal);
        var wcQualifiersIndex = deltaSection.IndexOf("| World Cup qualifiers |", StringComparison.Ordinal);

        Assert.True(wcQualifiersIndex >= 0);
        Assert.True(friendliesIndex >= 0);
        Assert.True(wcQualifiersIndex < friendliesIndex);
    }

    [Fact]
    public void Render_OmitsRankingBiasDeltaSummaryBySegmentWhenOneSideMissingForSegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2,
                    appliedSummary: new BacktestBiasGroupSummary(1, 0.3500, 0.6200, 0.1700, 0.85),
                    notAppliedSummary: new BacktestBiasGroupSummary(1, 0.4500, 0.7800, 0.2100, 0.75)),
                SegmentSummaryWithOracleBiasSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    appliedSummary: new BacktestBiasGroupSummary(2, 0.3200, 0.5800, 0.1600, 0.90))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — ranking bias delta summary by segment", output);
        var sectionStart = output.IndexOf(
            "## Oráculo final — ranking bias delta summary by segment", StringComparison.Ordinal);
        var sectionEnd = output.IndexOf("## Performance by match type", sectionStart, StringComparison.Ordinal);
        var deltaSection = output[sectionStart..sectionEnd];

        Assert.Contains("| Friendlies |", deltaSection);
        Assert.DoesNotContain("| World Cup qualifiers |", deltaSection);
    }

    [Fact]
    public void Render_PrintsOracleChosenPredictorSubgroupMetrics()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)
                {
                    ChosenPredictorSubgroupMetrics = new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = new(3, 0.3500, 0.6200, 0.1700, 0.85),
                        ["Elo"] = new(2, 0.4500, 0.7800, 0.2100, 0.75),
                        ["Goles + contexto reciente"] = new(1, 0.4800, 0.8200, 0.2300, 0.70)
                    }
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — chosen predictor subgroup metrics", output);
        Assert.Contains("do not interpret as causal comparisons between predictors", output);
        Assert.Contains("| Chosen predictor | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |", output);
        Assert.Contains("| Modelo de goles (Poisson) | 3 | 0.3500 | 0.6200 | 0.1700 | 85.0 % |", output);
        Assert.Contains("| Elo | 2 | 0.4500 | 0.7800 | 0.2100 | 75.0 % |", output);
        Assert.Contains("| Goles + contexto reciente | 1 | 0.4800 | 0.8200 | 0.2300 | 70.0 % |", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_OmitsOracleChosenPredictorSubgroupMetricsWhenNoData(bool oraclePresent)
    {
        BacktestModelSummary[] summaries = oraclePresent
            ? [new BacktestModelSummary("Oráculo final", 2, 0.450, 0.750, 0.200, 0.75)
                {
                    ChosenPredictorCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = 2
                    }
                }]
            : [new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5)];

        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            summaries);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor subgroup metrics", output);
    }

    [Fact]
    public void Render_PrintsOracleChosenPredictorSubgroupMetricsBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleChosenSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 3,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = new(2, 0.3500, 0.6200, 0.1700, 0.85),
                        ["Elo"] = new(1, 0.4500, 0.7800, 0.2100, 0.75)
                    }),
                SegmentSummaryWithOracleChosenSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 3,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Goles + contexto reciente"] = new(2, 0.3200, 0.5800, 0.1600, 0.90),
                        ["Modelo de goles (Poisson)"] = new(1, 0.4800, 0.8200, 0.2300, 0.70)
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — chosen predictor subgroup metrics by segment", output);
        Assert.Contains("| Segment | Chosen predictor | Count | MeanBrier | MeanLogLoss | MeanRPS | TopPickAccuracy |", output);
        Assert.Contains("| Friendlies | Modelo de goles (Poisson) | 2 | 0.3500 | 0.6200 | 0.1700 | 85.0 % |", output);
        Assert.Contains("| Friendlies | Elo | 1 | 0.4500 | 0.7800 | 0.2100 | 75.0 % |", output);
        Assert.Contains("| World Cup qualifiers | Goles + contexto reciente | 2 | 0.3200 | 0.5800 | 0.1600 | 90.0 % |", output);
        Assert.Contains("| World Cup qualifiers | Modelo de goles (Poisson) | 1 | 0.4800 | 0.8200 | 0.2300 | 70.0 % |", output);
    }

    [Fact]
    public void Render_OmitsOracleChosenPredictorSubgroupMetricsBySegmentWhenNoOracleSegments()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Modelo base", 4, 0.667, 1.099, 0.333, 0.5)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Modelo base", 2, 0.500, 0.700, 0.200, 1.0))
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor subgroup metrics by segment", output);
    }

    [Fact]
    public void Render_OmitsOracleChosenPredictorSubgroupMetricsBySegmentWhenOracleSegmentsHaveEmptyData()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Oráculo final", 2, 0.400, 0.700, 0.190, 0.80)
                    {
                        ChosenPredictorCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            ["Modelo de goles (Poisson)"] = 2
                        }
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor subgroup metrics by segment", output);
    }

    [Fact]
    public void Render_ChosenPredictorSubgroupMetricsBySegmentRespectsSegmentOrder()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleChosenSubgroup(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Elo"] = new(2, 0.3200, 0.5800, 0.1600, 0.90)
                    }),
                SegmentSummaryWithOracleChosenSubgroup(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Elo"] = new(2, 0.3500, 0.6200, 0.1700, 0.85)
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf(
            "## Oráculo final — chosen predictor subgroup metrics by segment", StringComparison.Ordinal);
        var sectionEnd = output.IndexOf("## Performance by match type", sectionStart, StringComparison.Ordinal);
        var section = output[sectionStart..sectionEnd];
        var friendliesIndex = section.IndexOf("| Friendlies |", StringComparison.Ordinal);
        var wcQualifiersIndex = section.IndexOf("| World Cup qualifiers |", StringComparison.Ordinal);

        Assert.True(wcQualifiersIndex >= 0);
        Assert.True(friendliesIndex >= 0);
        Assert.True(wcQualifiersIndex < friendliesIndex);
    }

    [Fact]
    public void Render_ChosenPredictorSubgroupMetricsOrderedByDescendingCount()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)
                {
                    ChosenPredictorSubgroupMetrics = new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Goles + contexto reciente"] = new(2, 0.4800, 0.8200, 0.2300, 0.70),
                        ["Modelo de goles (Poisson)"] = new(3, 0.3500, 0.6200, 0.1700, 0.85),
                        ["Elo"] = new(1, 0.4500, 0.7800, 0.2100, 0.75)
                    }
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf(
            "## Oráculo final — chosen predictor subgroup metrics", StringComparison.Ordinal);
        var modelGolesIndex = output.IndexOf("| Modelo de goles (Poisson) |", sectionStart, StringComparison.Ordinal);
        var contextoIndex = output.IndexOf("| Goles + contexto reciente |", sectionStart, StringComparison.Ordinal);
        var eloIndex = output.IndexOf("| Elo |", sectionStart, StringComparison.Ordinal);

        Assert.True(modelGolesIndex < contextoIndex);
        Assert.True(contextoIndex < eloIndex);
    }

    [Fact]
    public void Render_PrintsOracleChosenPredictorDeltaVsOverall()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [
                new BacktestModelSummary("Modelo base", 2, 0.667, 1.099, 0.333, 0.5),
                new BacktestModelSummary("Oráculo final", 6, 0.4000, 0.7000, 0.1900, 0.80)
                {
                    ChosenPredictorSubgroupMetrics = new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = new(3, 0.3500, 0.6200, 0.1700, 0.85),
                        ["Elo"] = new(2, 0.4500, 0.7800, 0.2100, 0.75),
                        ["Goles + contexto reciente"] = new(1, 0.4800, 0.8200, 0.2300, 0.70)
                    }
                }
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — chosen predictor delta vs overall (descriptive)", output);
        Assert.Contains("Delta = chosen-predictor subgroup minus Oráculo overall", output);
        Assert.Contains("descriptive only; not causal and not a same-fixture counterfactual", output);
        Assert.Contains("| Chosen predictor | ΔMeanBrier | ΔMeanLogLoss | ΔMeanRPS | ΔTopPickAccuracy |", output);
        Assert.Contains("| Modelo de goles (Poisson) | -0.0500 | -0.0800 | -0.0200 | +5.0 pp |", output);
        Assert.Contains("| Elo | +0.0500 | +0.0800 | +0.0200 | -5.0 pp |", output);
        Assert.Contains("| Goles + contexto reciente | +0.0800 | +0.1200 | +0.0400 | -10.0 pp |", output);
    }

    [Fact]
    public void Render_OmitsOracleChosenPredictorDeltaVsOverallWhenNoSubgroups()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [
                new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)
            ]);

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor delta vs overall", output);
    }

    [Fact]
    public void Render_PrintsOracleChosenPredictorDeltaBySegment()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(6, 6, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 6, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleChosenSubgroupAndMetrics(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 3,
                    oracleBrier: 0.3800, oracleLogLoss: 0.6500, oracleRps: 0.1800, oracleTopPick: 0.82,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Modelo de goles (Poisson)"] = new(2, 0.3500, 0.6200, 0.1700, 0.85),
                        ["Elo"] = new(1, 0.4500, 0.7800, 0.2100, 0.75)
                    }),
                SegmentSummaryWithOracleChosenSubgroupAndMetrics(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 3,
                    oracleBrier: 0.4200, oracleLogLoss: 0.7500, oracleRps: 0.2000, oracleTopPick: 0.78,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Goles + contexto reciente"] = new(2, 0.3200, 0.5800, 0.1600, 0.90),
                        ["Modelo de goles (Poisson)"] = new(1, 0.4800, 0.8200, 0.2300, 0.70)
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.Contains("## Oráculo final — chosen predictor delta by segment (descriptive)", output);
        Assert.Contains("Delta = chosen-predictor subgroup minus that segment's Oráculo summary", output);
        Assert.Contains("| Segment | Chosen predictor | ΔMeanBrier | ΔMeanLogLoss | ΔMeanRPS | ΔTopPickAccuracy |", output);
        // Friendlies: Poisson delta = 0.3500 - 0.3800 = -0.0300
        Assert.Contains("| Friendlies | Modelo de goles (Poisson) | -0.0300 | -0.0300 | -0.0100 | +3.0 pp |", output);
        // Friendlies: Elo delta = 0.4500 - 0.3800 = +0.0700
        Assert.Contains("| Friendlies | Elo | +0.0700 | +0.1300 | +0.0300 | -7.0 pp |", output);
        // WCQ: Goles + contexto delta = 0.3200 - 0.4200 = -0.1000
        Assert.Contains("| World Cup qualifiers | Goles + contexto reciente | -0.1000 | -0.1700 | -0.0400 | +12.0 pp |", output);
    }

    [Fact]
    public void Render_ChosenPredictorDeltaBySegmentRespectsSegmentOrder()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                SegmentSummaryWithOracleChosenSubgroupAndMetrics(
                    BacktestMatchSegmentClassifier.WorldCupQualifiers,
                    "Oráculo final", 2,
                    oracleBrier: 0.3200, oracleLogLoss: 0.5800, oracleRps: 0.1600, oracleTopPick: 0.90,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Elo"] = new(2, 0.3200, 0.5800, 0.1600, 0.90)
                    }),
                SegmentSummaryWithOracleChosenSubgroupAndMetrics(
                    BacktestMatchSegmentClassifier.Friendlies,
                    "Oráculo final", 2,
                    oracleBrier: 0.3500, oracleLogLoss: 0.6200, oracleRps: 0.1700, oracleTopPick: 0.85,
                    new Dictionary<string, BacktestBiasGroupSummary>(StringComparer.Ordinal)
                    {
                        ["Elo"] = new(2, 0.3500, 0.6200, 0.1700, 0.85)
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        var sectionStart = output.IndexOf(
            "## Oráculo final — chosen predictor delta by segment", StringComparison.Ordinal);
        var sectionEnd = output.IndexOf("## Performance by match type", sectionStart, StringComparison.Ordinal);
        var section = output[sectionStart..sectionEnd];
        var friendliesIndex = section.IndexOf("| Friendlies |", StringComparison.Ordinal);
        var wcQualifiersIndex = section.IndexOf("| World Cup qualifiers |", StringComparison.Ordinal);

        Assert.True(wcQualifiersIndex >= 0);
        Assert.True(friendliesIndex >= 0);
        Assert.True(wcQualifiersIndex < friendliesIndex);
    }

    [Fact]
    public void Render_OmitsOracleChosenPredictorDeltaBySegmentWhenNoSubgroups()
    {
        var report = new BacktestReport(
            new BacktestReportLoadResult(4, 4, 0, 0, 0, []),
            [new BacktestModelSummary("Oráculo final", 4, 0.400, 0.700, 0.190, 0.80)])
        {
            SegmentSummaries =
            [
                new BacktestSegmentModelSummary(
                    BacktestMatchSegmentClassifier.Friendlies,
                    new BacktestModelSummary("Oráculo final", 2, 0.400, 0.700, 0.190, 0.80)
                    {
                        ChosenPredictorCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            ["Modelo de goles (Poisson)"] = 2
                        }
                    })
            ]
        };

        var output = RollingBacktestReportService.Render(report);

        Assert.DoesNotContain("## Oráculo final — chosen predictor delta by segment", output);
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

    private static BacktestSegmentModelSummary SegmentSummary(
        string segment,
        string model,
        int count,
        double brier,
        double logLoss,
        double rps,
        double topPickAccuracy) =>
        new(segment, new BacktestModelSummary(model, count, brier, logLoss, rps, topPickAccuracy));

    private static BacktestSegmentModelSummary SegmentSummaryWithOracle(
        string segment,
        string model,
        int count,
        IReadOnlyDictionary<string, int> chosenPredictorCounts) =>
        new(segment, new BacktestModelSummary(model, count, 0.400, 0.700, 0.190, 0.80)
        {
            ChosenPredictorCounts = chosenPredictorCounts
        });

    private static BacktestSegmentModelSummary SegmentSummaryWithOracleBias(
        string segment,
        string model,
        int count,
        int applied,
        int notApplied) =>
        new(segment, new BacktestModelSummary(model, count, 0.400, 0.700, 0.190, 0.80)
        {
            RankingBiasAppliedCount = applied,
            RankingBiasNotAppliedCount = notApplied
        });

    private static BacktestSegmentModelSummary SegmentSummaryWithOracleChosenSubgroup(
        string segment,
        string model,
        int count,
        IReadOnlyDictionary<string, BacktestBiasGroupSummary> chosenPredictorSubgroupMetrics) =>
        new(segment, new BacktestModelSummary(model, count, 0.400, 0.700, 0.190, 0.80)
        {
            ChosenPredictorSubgroupMetrics = chosenPredictorSubgroupMetrics
        });

    private static BacktestSegmentModelSummary SegmentSummaryWithOracleChosenSubgroupAndMetrics(
        string segment,
        string model,
        int count,
        double oracleBrier,
        double oracleLogLoss,
        double oracleRps,
        double oracleTopPick,
        IReadOnlyDictionary<string, BacktestBiasGroupSummary> chosenPredictorSubgroupMetrics) =>
        new(segment, new BacktestModelSummary(model, count, oracleBrier, oracleLogLoss, oracleRps, oracleTopPick)
        {
            ChosenPredictorSubgroupMetrics = chosenPredictorSubgroupMetrics
        });

    private static BacktestSegmentModelSummary SegmentSummaryWithOracleBiasSubgroup(
        string segment,
        string model,
        int count,
        BacktestBiasGroupSummary? appliedSummary = null,
        BacktestBiasGroupSummary? notAppliedSummary = null) =>
        new(segment, new BacktestModelSummary(model, count, 0.400, 0.700, 0.190, 0.80)
        {
            RankingBiasAppliedSummary = appliedSummary,
            RankingBiasNotAppliedSummary = notAppliedSummary
        });
}
