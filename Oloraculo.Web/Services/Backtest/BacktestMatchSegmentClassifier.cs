namespace Oloraculo.Web.Services.Backtest;

public static class BacktestMatchSegmentClassifier
{
    public const string AllMatches = "All matches";
    public const string Friendlies = "Friendlies";
    public const string WorldCupQualifiers = "World Cup qualifiers";
    public const string WorldCupFinals = "World Cup finals";
    public const string OtherOfficialTournaments = "Other official tournaments";

    public static IReadOnlyList<string> OrderedSegments { get; } =
    [
        AllMatches,
        Friendlies,
        WorldCupQualifiers,
        WorldCupFinals,
        OtherOfficialTournaments
    ];

    public static string Classify(string? tournament)
    {
        if (string.IsNullOrWhiteSpace(tournament))
            return OtherOfficialTournaments;

        var normalized = tournament.Trim();
        if (string.Equals(normalized, "Friendly", StringComparison.OrdinalIgnoreCase))
            return Friendlies;

        if (string.Equals(normalized, "FIFA World Cup qualification", StringComparison.OrdinalIgnoreCase))
            return WorldCupQualifiers;

        if (string.Equals(normalized, "FIFA World Cup", StringComparison.OrdinalIgnoreCase))
            return WorldCupFinals;

        return OtherOfficialTournaments;
    }
}
