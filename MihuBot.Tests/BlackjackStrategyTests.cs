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

    [Theory]
    [InlineData(4, 5, 2, BrowserBlackjackCommand.Double)]
    [InlineData(6, 5, 1, BrowserBlackjackCommand.Double)]
    [InlineData(1, 2, 4, BrowserBlackjackCommand.Double)]
    [InlineData(1, 3, 4, BrowserBlackjackCommand.Double)]
    [InlineData(6, 6, 7, BrowserBlackjackCommand.Split)]
    [InlineData(7, 7, 8, BrowserBlackjackCommand.Split)]
    [InlineData(10, 6, 9, BrowserBlackjackCommand.Hit)]
    [InlineData(10, 6, 10, BrowserBlackjackCommand.Surrender)]
    [InlineData(10, 6, 1, BrowserBlackjackCommand.Surrender)]
    [InlineData(1, 7, 1, BrowserBlackjackCommand.Hit)]
    public void DoubleDeckAdviceUsesItsOwnRulesRatherThanShoeIndices(
        int first, int second, int dealer, BrowserBlackjackCommand expected)
    {
        var advice = BlackjackStrategy.Analyze(2, Counts(0), Hand(first, second), dealer, false, s_actions);
        Assert.Equal(expected, advice.Move);
        Assert.Contains("Two-deck", advice.Explanation, StringComparison.Ordinal);
        Assert.Equal(2, advice.UnseenDecks);
    }

    [Fact]
    public void DoubleDeckMulticardSoftEighteenStandsAgainstAce()
    {
        var actions = new[] { BrowserBlackjackCommand.Hit, BrowserBlackjackCommand.Stand };
        Assert.Equal(BrowserBlackjackCommand.Stand,
            BlackjackStrategy.Analyze(2, Counts(0), Hand(1, 2, 5), 1, false, actions).Move);
        Assert.Equal(BrowserBlackjackCommand.Hit,
            BlackjackStrategy.Analyze(4, Counts(0), Hand(1, 2, 5), 1, false, actions).Move);
    }

    [Fact]
    public void DoubleDeckInsuranceStillUsesExactUnseenCardOdds()
    {
        int[] exposed = Counts(0);
        exposed[7] = 8;
        BrowserBlackjackCommand[] actions = [BrowserBlackjackCommand.Insure, BrowserBlackjackCommand.DeclineInsurance];
        Assert.Equal(BrowserBlackjackCommand.DeclineInsurance,
            BlackjackStrategy.Analyze(2, exposed, Hand(10, 9), 1, true, actions).Move);
        exposed[8] = 1;
        var advice = BlackjackStrategy.Analyze(2, exposed, Hand(10, 9), 1, true, actions);
        Assert.Equal(BrowserBlackjackCommand.Insure, advice.Move);
        Assert.Equal(95 / 52d, advice.UnseenDecks);
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

    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(1.999, 10)]
    [InlineData(2, 20)]
    [InlineData(2.999, 20)]
    [InlineData(3, 30)]
    [InlineData(3.999, 30)]
    [InlineData(4, 40)]
    [InlineData(20, 40)]
    public void BettingRampUsesWholeTenChipUnitsAndCapsAtFour(double trueCount, int amount)
    {
        var advice = BlackjackStrategy.RecommendBet(trueCount, 1_000, shuffleExpected: false);
        Assert.Equal((decimal)amount, advice.Amount);
        Assert.Equal(trueCount, advice.TrueCount);
        Assert.False(advice.ShuffleExpected);
        Assert.Contains("Training guideline", advice.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(39.5, 0)]
    [InlineData(40, 10)]
    [InlineData(79.5, 10)]
    [InlineData(80, 20)]
    [InlineData(119.5, 20)]
    [InlineData(120, 30)]
    [InlineData(159.5, 30)]
    [InlineData(160, 40)]
    public void BettingCapReservesThreeBetsAndRoundsDown(decimal balance, int amount)
    {
        var advice = BlackjackStrategy.RecommendBet(10, balance, shuffleExpected: false);
        Assert.Equal(amount == 0 ? (decimal?)null : amount, advice.Amount);

        if (advice.Amount is decimal bet)
        {
            Assert.True(bet * 4 <= balance);
            Assert.Equal(0, bet % BlackjackStrategy.BettingUnit);
        }
        else
        {
            Assert.Contains("Sit out", advice.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BettingBeforeAShuffleUsesZeroInsteadOfTheOldCount()
    {
        var advice = BlackjackStrategy.RecommendBet(10, 1_000, shuffleExpected: true);
        Assert.Equal(10m, advice.Amount);
        Assert.Equal(0, advice.TrueCount);
        Assert.True(advice.ShuffleExpected);
        Assert.Contains("shuffle", advice.Explanation, StringComparison.Ordinal);
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
