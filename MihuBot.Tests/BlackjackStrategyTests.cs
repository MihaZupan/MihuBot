using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BlackjackStrategyTests
{
    private static readonly BrowserBlackjackCommand[] s_actions =
    [
        BrowserBlackjackCommand.Hit, BrowserBlackjackCommand.Stand, BrowserBlackjackCommand.Double,
        BrowserBlackjackCommand.Split, BrowserBlackjackCommand.Surrender
    ];

    private static BlackjackHand Hand(params int[] ranks)
    {
        var hand = new BlackjackHand { Bet = 100 };
        hand.Cards.AddRange(ranks.Select(r => new BlackjackCard(r, 0)));
        return hand;
    }

    [Theory]
    [InlineData(10, 6, 10, BrowserBlackjackCommand.Surrender)]
    [InlineData(10, 5, 10, BrowserBlackjackCommand.Surrender)]
    [InlineData(8, 8, 10, BrowserBlackjackCommand.Split)]
    [InlineData(1, 1, 1, BrowserBlackjackCommand.Split)]
    [InlineData(4, 4, 6, BrowserBlackjackCommand.Split)]
    [InlineData(4, 4, 4, BrowserBlackjackCommand.Hit)]
    [InlineData(9, 9, 7, BrowserBlackjackCommand.Stand)]
    [InlineData(9, 9, 9, BrowserBlackjackCommand.Split)]
    [InlineData(6, 6, 7, BrowserBlackjackCommand.Hit)]
    [InlineData(5, 5, 6, BrowserBlackjackCommand.Double)]
    [InlineData(1, 7, 6, BrowserBlackjackCommand.Double)]
    [InlineData(1, 7, 2, BrowserBlackjackCommand.Stand)]
    [InlineData(1, 7, 1, BrowserBlackjackCommand.Hit)]
    [InlineData(1, 8, 6, BrowserBlackjackCommand.Stand)]
    [InlineData(6, 5, 1, BrowserBlackjackCommand.Hit)]
    [InlineData(4, 5, 2, BrowserBlackjackCommand.Hit)]
    public void BasicStrategyMatchesS17DasLateSurrender(int first, int second, int dealer, BrowserBlackjackCommand expected)
    {
        Assert.Equal(expected, BlackjackStrategy.BasicStrategy(Hand(first, second), dealer, s_actions));
    }

    [Fact]
    public void UnavailableSplitsDoublesAndSurrenderUseLegalFallbacks()
    {
        BrowserBlackjackCommand[] actions = [BrowserBlackjackCommand.Hit, BrowserBlackjackCommand.Stand];
        Assert.Equal(BrowserBlackjackCommand.Stand, BlackjackStrategy.BasicStrategy(Hand(1, 7), 6, actions));
        Assert.Equal(BrowserBlackjackCommand.Hit, BlackjackStrategy.BasicStrategy(Hand(6, 5), 6, actions));
        Assert.Equal(BrowserBlackjackCommand.Hit, BlackjackStrategy.BasicStrategy(Hand(8, 8), 10, actions));
        Assert.Equal(BrowserBlackjackCommand.Hit, BlackjackStrategy.BasicStrategy(Hand(10, 5), 10, actions));
    }

    [Theory]
    [InlineData(0, 12, 3, BrowserBlackjackCommand.Hit)]
    [InlineData(15, 12, 3, BrowserBlackjackCommand.Stand)]
    [InlineData(-1, 12, 4, BrowserBlackjackCommand.Hit)]
    [InlineData(0, 12, 4, BrowserBlackjackCommand.Stand)]
    [InlineData(30, 10, 10, BrowserBlackjackCommand.Double)]
    [InlineData(0, 10, 10, BrowserBlackjackCommand.Hit)]
    [InlineData(24, 15, 10, BrowserBlackjackCommand.Stand)]
    [InlineData(0, 15, 10, BrowserBlackjackCommand.Hit)]
    public void HiLoIndicesApplyOnBothSidesOfThreshold(int running, int total, int dealer, BrowserBlackjackCommand expected)
    {
        int[] exposed = Counts(running);
        var actions = s_actions.Where(a => a is not (BrowserBlackjackCommand.Split or BrowserBlackjackCommand.Surrender)).ToArray();
        var advice = BlackjackStrategy.Analyze(6, exposed, Hand(total - 5, 5), dealer, false, actions);
        Assert.Equal(expected, advice.Move);
        Assert.Equal(running, advice.RunningCount);
        Assert.Contains("index", advice.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void SurrenderTakesPrecedenceOverHitStandIndicesAndCanBeDeclinedAtLowCounts()
    {
        Assert.Equal(BrowserBlackjackCommand.Surrender,
            BlackjackStrategy.Analyze(6, Counts(0), Hand(10, 5), 10, false, s_actions).Move);
        Assert.Equal(BrowserBlackjackCommand.Hit,
            BlackjackStrategy.Analyze(6, Counts(-1), Hand(10, 5), 10, false, s_actions).Move);
        Assert.Equal(BrowserBlackjackCommand.Surrender,
            BlackjackStrategy.Analyze(6, Counts(24), Hand(10, 4), 10, false, s_actions).Move);
        Assert.Equal(BrowserBlackjackCommand.Surrender,
            BlackjackStrategy.Analyze(6, Counts(30), Hand(10, 6), 10, false, s_actions).Move);
        Assert.Equal(BrowserBlackjackCommand.Split,
            BlackjackStrategy.Analyze(6, Counts(30), Hand(8, 8), 10, false, s_actions).Move);
    }

    [Fact]
    public void SplittingTensRequiresTheCountAndAnAvailableSplit()
    {
        Assert.Equal(BrowserBlackjackCommand.Stand,
            BlackjackStrategy.Analyze(6, Counts(0), Hand(10, 13), 6, false, s_actions).Move);
        var high = BlackjackStrategy.Analyze(6, Counts(30), Hand(10, 13), 6, false, s_actions);
        Assert.Equal(BrowserBlackjackCommand.Split, high.Move);
        Assert.True(high.Deviation);
        Assert.Equal(BrowserBlackjackCommand.Stand,
            BlackjackStrategy.Analyze(6, Counts(30), Hand(10, 13), 6, false,
                s_actions.Where(a => a != BrowserBlackjackCommand.Split).ToArray()).Move);
    }

    [Fact]
    public void InsuranceUsesExactUnseenTensRatherThanReadingTheHoleCard()
    {
        BrowserBlackjackCommand[] actions = [BrowserBlackjackCommand.Insure, BrowserBlackjackCommand.DeclineInsurance];
        int[] exposed = new int[11];
        exposed[7] = 24;
        Assert.Equal(BrowserBlackjackCommand.DeclineInsurance,
            BlackjackStrategy.Analyze(6, exposed, Hand(10, 9), 1, true, actions).Move);
        exposed[8] = 1;
        var advice = BlackjackStrategy.Analyze(6, exposed, Hand(10, 9), 1, true, actions);
        Assert.Equal(0, advice.RunningCount);
        Assert.Equal(BrowserBlackjackCommand.Insure, advice.Move);
        Assert.Equal(BrowserBlackjackCommand.DeclineInsurance,
            BlackjackStrategy.Analyze(6, exposed, Hand(10, 9), 1, true, [BrowserBlackjackCommand.DeclineInsurance]).Move);
    }

    private static int[] Counts(int running)
    {
        int[] counts = new int[11];

        if (running >= 0)
        {
            for (int i = 0; i < running; i++)
            {
                counts[2 + (i % 5)]++;
            }
        }
        else
        {
            counts[10] = -running;
        }

        return counts;
    }
}
