namespace Oloraculo.Web.Models
{
    public sealed record MatchSnapshotChip(
        string FixtureId,
        string? ScoreText,
        string TopPick,
        bool IsContextAdjusted);
}
