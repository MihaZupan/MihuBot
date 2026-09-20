namespace MihuBot.Games.Blackjack;

public sealed record BrowserBlackjackBetOdds(int ReferenceCount, double Win, double Push, double Edge)
{
    public double Lose => 1 - Win - Push;
}

internal static class BlackjackBettingOdds
{
    internal const int MinimumCount = -10;
    internal const int MaximumCount = 10;

    // Comparable S17/DAS/LS basic-strategy variance, not a count-specific estimate.
    // https://wizardofodds.com/games/blackjack/variance/
    internal const double Variance = 1.303;

    // Approximate chart readings in percent, rounded to 0.1 percentage point.
    // Six decks, S17/DAS/LS, full Hi-Lo indexes, truncated TC, half-deck resolution.
    // https://www.blackjackincolor.com/truecount5.htm (win/push; TC 0 win from the accompanying text)
    // https://www.blackjackincolor.com/truecount2.htm (six-deck edge)
    private static readonly (double Win, double Push, double Edge)[] s_reference =
    [
        (41.9, 9.2, -4.6), // -10
        (42.1, 9.1, -4.3),
        (42.3, 9.0, -4.1),
        (42.4, 8.8, -3.7),
        (42.5, 8.7, -3.4),
        (42.7, 8.6, -2.9),
        (42.9, 8.6, -2.5),
        (43.0, 8.5, -2.0),
        (42.9, 8.4, -1.5),
        (42.9, 8.3, -1.0),
        (42.7, 8.1, -0.3), // 0
        (42.8, 8.2, 0.4),
        (42.7, 8.3, 1.0),
        (42.5, 9.3, 1.6),
        (42.5, 10.0, 2.3),
        (42.8, 10.1, 3.0),
        (42.8, 10.2, 3.7),
        (42.9, 10.4, 4.4),
        (42.8, 10.5, 5.3),
        (42.8, 10.6, 5.9),
        (43.0, 10.8, 6.7) // +10
    ];

    internal static BrowserBlackjackBetOdds ForCount(double trueCount)
    {
        if (!double.IsFinite(trueCount))
        {
            throw new ArgumentOutOfRangeException(nameof(trueCount), "The true count must be finite.");
        }

        int count = (int)Math.Clamp(Math.Truncate(trueCount), MinimumCount, MaximumCount);
        var (win, push, edge) = s_reference[count - MinimumCount];
        return new(count, win / 100, push / 100, edge / 100);
    }
}
