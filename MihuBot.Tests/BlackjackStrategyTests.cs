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
    [InlineData(2, 10)]
    [InlineData(3, 10)]
    [InlineData(4, 10)]
    [InlineData(5, 10)]
    [InlineData(7.999, 10)]
    [InlineData(8, 20)]
    [InlineData(10, 20)]
    [InlineData(20, 20)]
    public void BettingUsesReferenceEdgeAndApproximateHalfKelly(double trueCount, int amount)
    {
        var advice = BlackjackStrategy.RecommendBet(trueCount, 1_000, shuffleExpected: false);
        Assert.Equal((decimal)amount, advice.Amount);
        Assert.Equal(trueCount, advice.TrueCount);
        Assert.False(advice.ShuffleExpected);
        Assert.Equal(BlackjackBettingOdds.ForCount(trueCount), advice.Odds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(9.5, 0)]
    [InlineData(10, 10)]
    [InlineData(10.5, 10)]
    [InlineData(39.5, 10)]
    [InlineData(84.5, 10)]
    [InlineData(85, 10)]
    [InlineData(388.5, 10)]
    [InlineData(389, 10)]
    [InlineData(777.5, 10)]
    [InlineData(778, 20)]
    [InlineData(1166.5, 20)]
    [InlineData(1167, 30)]
    [InlineData(1555.5, 30)]
    [InlineData(1556, 40)]
    [InlineData(20_000, 500)]
    public void BettingUsesAvailableBankrollRoundsDownAndCapsAtTableLimit(decimal balance, int amount)
    {
        var advice = BlackjackStrategy.RecommendBet(10, balance, shuffleExpected: false);
        Assert.Equal(amount == 0 ? (decimal?)null : amount, advice.Amount);

        if (advice.Amount is decimal bet)
        {
            Assert.True(bet <= balance);
            Assert.Equal(0, bet % BlackjackStrategy.BettingUnit);

            if (balance >= BlackjackTable.MinimumBet * 8.5m)
            {
                Assert.True(bet * 8.5m <= balance);
            }
            else
            {
                Assert.Equal(BlackjackTable.MinimumBet, bet);
                Assert.Contains("limited reserves", advice.Explanation, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Contains("Minimum bet", advice.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BettingBeforeAShuffleUsesZeroInsteadOfTheOldCount()
    {
        var advice = BlackjackStrategy.RecommendBet(10, 1_000, shuffleExpected: true);
        Assert.Equal(BlackjackTable.MinimumBet, advice.Amount);
        Assert.Equal(0, advice.TrueCount);
        Assert.True(advice.ShuffleExpected);
        Assert.Equal(BlackjackBettingOdds.ForCount(0), advice.Odds);
        Assert.Contains("shuffle", advice.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1000, -10)]
    [InlineData(-10.9, -10)]
    [InlineData(-2.9, -2)]
    [InlineData(-0.9, 0)]
    [InlineData(0.9, 0)]
    [InlineData(2.9, 2)]
    [InlineData(10.9, 10)]
    [InlineData(1000, 10)]
    public void PublishedReferenceUsesTruncatedCountsAndDoesNotExtrapolate(double count, int reference)
    {
        Assert.Equal(reference, BlackjackBettingOdds.ForCount(count).ReferenceCount);
    }

    [Fact]
    public void ReferenceProbabilitiesAreNormalizedAndEdgeIsNotWinMinusLoss()
    {
        for (int count = BlackjackBettingOdds.MinimumCount; count <= BlackjackBettingOdds.MaximumCount; count++)
        {
            var odds = BlackjackBettingOdds.ForCount(count);
            Assert.InRange(odds.Win, 0, 1);
            Assert.InRange(odds.Push, 0, 1);
            Assert.InRange(odds.Lose, 0, 1);
            Assert.Equal(1, odds.Win + odds.Push + odds.Lose, 12);
        }

        var zero = BlackjackBettingOdds.ForCount(0);
        Assert.Equal(0.427, zero.Win, 12);
        Assert.Equal(0.081, zero.Push, 12);
        Assert.Equal(-0.003, zero.Edge, 12);
        var positive = BlackjackBettingOdds.ForCount(5);
        Assert.Equal(0.03, positive.Edge, 12);
        Assert.True(positive.Win < positive.Lose);
        Assert.NotEqual(positive.Edge, positive.Win - positive.Lose);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidCountsAreRejected(double count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackjackBettingOdds.ForCount(count));
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
