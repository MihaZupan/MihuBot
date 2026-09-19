using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BlackjackTests
{
    private static BlackjackGame Game(decimal balance, decimal bet, params int[] ranks)
    {
        var cards = new Queue<BlackjackCard>(ranks.Select(r => new BlackjackCard(r, 0)));
        return new BlackjackGame(cards.Dequeue, [new BlackjackPlayer(42, "Player", balance, bet)]);
    }

    [Theory]
    [InlineData(new[] { 1, 1 }, 12, true)]
    [InlineData(new[] { 1, 1, 9 }, 21, true)]
    [InlineData(new[] { 1, 6, 10 }, 17, false)]
    [InlineData(new[] { 1, 1, 10, 9 }, 21, false)]
    [InlineData(new[] { 10, 12, 2 }, 22, false)]
    public void EvaluatesAcesAndFaceCards(int[] ranks, int total, bool soft)
    {
        Assert.Equal((total, soft), BlackjackHand.Evaluate(ranks.Select(r => new BlackjackCard(r, 0))));
    }

    [Fact]
    public void NaturalPaysThreeToTwoWithoutDrawingDealerCards()
    {
        BlackjackGame game = Game(1_000, 11, 1, 6, 13, 9);

        Assert.True(game.IsComplete);
        Assert.Equal(1_016.5m, game.Players[0].Balance);
        Assert.Equal(27.5m, game.Players[0].Hands[0].Returned);
        Assert.Equal(2, game.Dealer.Count);
        Assert.False(game.TryAct(BlackjackAction.Hit));
        game.AutoStand();
        Assert.Equal(1_016.5m, game.Players[0].Balance);
    }

    [Fact]
    public void DealerTenPeeksBeforeAllowingAnyActions()
    {
        BlackjackGame game = Game(1_000, 100, 8, 13, 8, 1);

        Assert.True(game.IsComplete);
        Assert.Equal(900, game.Players[0].Balance);
        Assert.False(game.TryAct(BlackjackAction.Surrender));
        Assert.False(game.TryAct(BlackjackAction.Split));
    }

    [Fact]
    public void TwoNaturalsPush()
    {
        BlackjackGame game = Game(1_000, 100, 1, 10, 13, 1);

        Assert.True(game.IsComplete);
        Assert.Equal(1_000, game.Players[0].Balance);
        Assert.Equal("Push", game.Players[0].Hands[0].Result);
    }

    [Fact]
    public void InsurancePrecedesPeekAndPaysTwoToOne()
    {
        BlackjackGame game = Game(1_000, 100, 9, 1, 8, 13);

        Assert.True(game.OfferingInsurance);
        Assert.False(game.IsComplete);
        Assert.False(game.TryAct(BlackjackAction.Hit));
        Assert.False(game.TryAct(BlackjackAction.Surrender));
        Assert.True(game.TryAct(BlackjackAction.Insure));
        Assert.True(game.IsComplete);
        Assert.Equal(50, game.Players[0].InsuranceBet);
        Assert.Equal(150, game.Players[0].InsuranceReturned);
        Assert.Equal(1_000, game.Players[0].Balance);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    public void InsuringANaturalIsEvenMoney(int holeCard)
    {
        BlackjackGame game = Game(1_000, 100, 1, 1, 13, holeCard);

        Assert.False(game.IsComplete);
        Assert.True(game.TryAct(BlackjackAction.Insure));
        Assert.True(game.IsComplete);
        Assert.Equal(1_100, game.Players[0].Balance);
    }

    [Fact]
    public void LosingInsuranceDoesNotPreventPlayingMainHand()
    {
        BlackjackGame game = Game(1_000, 100, 10, 1, 13, 8);

        Assert.True(game.TryAct(BlackjackAction.Insure));
        Assert.False(game.OfferingInsurance);
        Assert.Equal(850, game.Players[0].Balance);
        Assert.False(game.TryAct(BlackjackAction.Insure));
        Assert.True(game.TryAct(BlackjackAction.Stand));
        Assert.Equal(1_050, game.Players[0].Balance);
        Assert.Equal(0, game.Players[0].InsuranceReturned);
    }

    [Fact]
    public void InsuranceRequiresFundsButCanAlwaysBeDeclined()
    {
        BlackjackGame game = Game(100, 100, 10, 1, 8, 9);

        Assert.False(game.TryAct(BlackjackAction.Insure));
        Assert.Equal(0, game.Players[0].Balance);
        Assert.True(game.TryAct(BlackjackAction.DeclineInsurance));
        Assert.True(game.TryAct(BlackjackAction.Stand));
        Assert.Equal(0, game.Players[0].Balance);
    }

    [Fact]
    public void DealerStandsOnSoftSeventeen()
    {
        BlackjackGame game = Game(1_000, 100, 10, 1, 8, 6);

        game.TryAct(BlackjackAction.DeclineInsurance);
        game.TryAct(BlackjackAction.Stand);

        Assert.Equal(2, game.Dealer.Count);
        Assert.Equal((17, true), BlackjackHand.Evaluate(game.Dealer));
        Assert.Equal(1_100, game.Players[0].Balance);
    }

    [Fact]
    public void DealerHitsSoftSixteenAndRevaluesAce()
    {
        BlackjackGame game = Game(1_000, 100, 10, 1, 8, 5, 10, 2);

        game.TryAct(BlackjackAction.DeclineInsurance);
        game.TryAct(BlackjackAction.Stand);

        Assert.Equal(4, game.Dealer.Count);
        Assert.Equal((18, false), BlackjackHand.Evaluate(game.Dealer));
        Assert.Equal(1_000, game.Players[0].Balance);
    }

    [Fact]
    public void BustLosesImmediatelyWithoutUnnecessaryDealerDraws()
    {
        BlackjackGame game = Game(1_000, 100, 10, 6, 8, 10, 5);

        Assert.True(game.TryAct(BlackjackAction.Hit));
        Assert.True(game.IsComplete);
        Assert.Equal("Bust", game.Players[0].Hands[0].Result);
        Assert.Equal(900, game.Players[0].Balance);
        Assert.Equal(2, game.Dealer.Count);
    }

    [Fact]
    public void DealerBustPaysEvenMoney()
    {
        BlackjackGame game = Game(1_000, 100, 10, 6, 8, 10, 10);

        game.TryAct(BlackjackAction.Stand);

        Assert.True(game.IsComplete);
        Assert.Equal(1_100, game.Players[0].Balance);
    }

    [Fact]
    public void DoubleTakesOneCardAndDoublesBothStakeAndPayout()
    {
        BlackjackGame game = Game(1_000, 100, 5, 10, 6, 8, 10);

        Assert.True(game.TryAct(BlackjackAction.Double));
        Assert.True(game.IsComplete);
        Assert.Equal(3, game.Players[0].Hands[0].Cards.Count);
        Assert.Equal(200, game.Players[0].Hands[0].Bet);
        Assert.Equal(400, game.Players[0].Hands[0].Returned);
        Assert.Equal(1_200, game.Players[0].Balance);
        Assert.False(game.TryAct(BlackjackAction.Double));
    }

    [Fact]
    public void DoubleBustLosesBothBets()
    {
        BlackjackGame game = Game(1_000, 100, 10, 6, 5, 10, 10);

        game.TryAct(BlackjackAction.Double);

        Assert.True(game.IsComplete);
        Assert.Equal(800, game.Players[0].Balance);
    }

    [Fact]
    public void HitDisablesDoubleAndSurrender()
    {
        BlackjackGame game = Game(1_000, 100, 2, 10, 3, 8, 4);

        game.TryAct(BlackjackAction.Hit);

        Assert.False(game.TryAct(BlackjackAction.Double));
        Assert.False(game.TryAct(BlackjackAction.Surrender));
        Assert.Equal(900, game.Players[0].Balance);
    }

    [Fact]
    public void DoubleAndSplitCannotOverdrawBankroll()
    {
        BlackjackGame game = Game(199, 100, 8, 10, 8, 8);

        Assert.False(game.TryAct(BlackjackAction.Double));
        Assert.False(game.TryAct(BlackjackAction.Split));
        Assert.Equal(99, game.Players[0].Balance);
        Assert.Single(game.Players[0].Hands);
    }

    [Fact]
    public void LateSurrenderReturnsHalfAndDoesNotDrawDealerCards()
    {
        BlackjackGame game = Game(1_000, 11, 10, 6, 6, 10);

        Assert.True(game.TryAct(BlackjackAction.Surrender));
        Assert.True(game.IsComplete);
        Assert.Equal(994.5m, game.Players[0].Balance);
        Assert.Equal(2, game.Dealer.Count);
    }

    [Fact]
    public void SplitHandsAreDealtInOrderAndAllowDoubling()
    {
        BlackjackGame game = Game(1_000, 100, 8, 6, 8, 10, 3, 10, 2, 10, 10);

        Assert.True(game.TryAct(BlackjackAction.Split));
        Assert.Equal(800, game.Players[0].Balance);
        Assert.Equal(2, game.Players[0].Hands.Count);
        Assert.Single(game.Players[0].Hands[1].Cards);
        Assert.False(game.CanAct(BlackjackAction.Surrender));
        Assert.True(game.TryAct(BlackjackAction.Double));
        Assert.Equal(1, game.ActivePlayer.ActiveHandIndex);
        Assert.Equal(10, game.ActiveHand.Value.Total);
        Assert.True(game.TryAct(BlackjackAction.Double));

        Assert.True(game.IsComplete);
        Assert.Equal(1_400, game.Players[0].Balance);
        Assert.All(game.Players[0].Hands, h => Assert.Equal(200, h.Bet));
    }

    [Fact]
    public void SplitAcesGetOnlyOneCardAndTwentyOneIsNotNatural()
    {
        BlackjackGame game = Game(1_000, 100, 1, 10, 1, 7, 13, 1);

        Assert.True(game.TryAct(BlackjackAction.Split));
        Assert.True(game.IsComplete);
        Assert.All(game.Players[0].Hands, h => Assert.Equal(2, h.Cards.Count));
        Assert.All(game.Players[0].Hands, h => Assert.False(h.IsBlackjack));
        Assert.Equal(200, game.Players[0].Hands[0].Returned);
        Assert.Equal(0, game.Players[0].Hands[1].Returned);
        Assert.Equal(1_000, game.Players[0].Balance);
    }

    [Fact]
    public void NonAceSplitTwentyOneIsAlsoNotNatural()
    {
        BlackjackGame game = Game(1_000, 100, 10, 10, 13, 7, 1, 1);

        Assert.True(game.TryAct(BlackjackAction.Split));
        Assert.True(game.IsComplete);
        Assert.Equal(1_200, game.Players[0].Balance);
        Assert.All(game.Players[0].Hands, h => Assert.False(h.IsBlackjack));
    }

    [Fact]
    public void ResplitsAreLimitedToFourHands()
    {
        BlackjackGame game = Game(1_000, 100, 8, 10, 8, 7, 8, 8, 8, 10, 10, 10);

        Assert.True(game.TryAct(BlackjackAction.Split));
        Assert.True(game.TryAct(BlackjackAction.Split));
        Assert.True(game.TryAct(BlackjackAction.Split));
        Assert.False(game.TryAct(BlackjackAction.Split));
        Assert.Equal(4, game.Players[0].Hands.Count);
        Assert.Equal(600, game.Players[0].Balance);
        game.AutoStand();

        Assert.True(game.IsComplete);
        Assert.Equal(1_200, game.Players[0].Balance);
    }

    [Fact]
    public void OnlyEqualValueCardsCanSplit()
    {
        BlackjackGame game = Game(1_000, 100, 8, 10, 9, 7);

        Assert.False(game.TryAct(BlackjackAction.Split));
        Assert.Equal(900, game.Players[0].Balance);
    }

    [Fact]
    public void TimeoutDeclinesInsuranceAndSettlesExactlyOnce()
    {
        BlackjackGame game = Game(1_000, 100, 10, 1, 8, 10);

        game.AutoStand();
        game.AutoStand();

        Assert.True(game.IsComplete);
        Assert.Equal(0, game.Players[0].InsuranceBet);
        Assert.Equal(900, game.Players[0].Balance);
    }

    [Fact]
    public void ShoeContainsSixOfEachCardAndShufflesOnlyBetweenRoundsAtCutCard()
    {
        var shoe = new BlackjackShoe();

        Assert.True(shoe.PrepareRound());
        Assert.Equal(312, shoe.Remaining);
        Assert.Equal(1, shoe.Number);
        Assert.False(shoe.PrepareRound());

        var dealt = new List<BlackjackCard>();

        for (int i = 0; i < 233; i++)
        {
            dealt.Add(shoe.Draw());
        }

        Assert.False(shoe.PrepareRound());
        dealt.Add(shoe.Draw());
        Assert.Equal(78, shoe.Remaining);

        while (shoe.Remaining > 0)
        {
            dealt.Add(shoe.Draw());
        }

        Assert.Equal(52, dealt.Distinct().Count());
        Assert.All(dealt.GroupBy(c => c), g => Assert.Equal(6, g.Count()));
        Assert.Equal(1, shoe.Number);
        Assert.True(shoe.PrepareRound());
        Assert.Equal(312, shoe.Remaining);
        Assert.Equal(2, shoe.Number);
    }

    [Fact]
    public void CutCardTriggersAtExactlySeventyFivePercent()
    {
        var shoe = new BlackjackShoe();
        shoe.PrepareRound();

        for (int i = 0; i < 234; i++)
        {
            shoe.Draw();
        }

        Assert.True(shoe.PrepareRound());
        Assert.Equal(312, shoe.Remaining);
    }

    [Fact]
    public void FullShoesSupportRepeatedRoundsWithoutMidRoundShufflesOrAccountingDrift()
    {
        var shoe = new BlackjackShoe();
        var random = new Random(42);
        BlackjackAction[] actions = Enum.GetValues<BlackjackAction>();

        for (int round = 0; round < 2_000; round++)
        {
            int seats = random.Next(1, BlackjackGame.MaxPlayers + 1);
            shoe.PrepareRound(seats);
            int shoeNumber = shoe.Number;
            int remaining = shoe.Remaining;
            var game = new BlackjackGame(shoe.Draw, Enumerable.Range(0, seats)
                .Select(i => new BlackjackPlayer((ulong)i, $"Player {i}", 1_000, random.Next(10, 501))).ToArray());
            int moves = 0;

            while (!game.IsComplete)
            {
                BlackjackAction[] available = actions.Where(game.CanAct).ToArray();
                Assert.NotEmpty(available);
                Assert.True(game.TryAct(available[random.Next(available.Length)]));
                Assert.All(game.Players, p => Assert.InRange(p.Balance, 0, decimal.MaxValue));
                Assert.InRange(++moves, 1, 100);
            }

            Assert.Equal(shoeNumber, shoe.Number);
            Assert.Equal(remaining - game.Dealer.Count - game.Players.SelectMany(p => p.Hands).Sum(h => h.Cards.Count), shoe.Remaining);

            foreach (BlackjackPlayer player in game.Players)
            {
                Assert.InRange(player.Hands.Count, 1, 4);
                Assert.All(player.Hands, h => Assert.True(h.Finished));
                Assert.Equal(1_000 - player.Hands.Sum(h => h.Bet) - player.InsuranceBet +
                    player.Hands.Sum(h => h.Returned) + player.InsuranceReturned, player.Balance);
            }
        }
    }

    [Fact]
    public void InvalidInitialBetsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Game(100, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => Game(100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Game(100, -1));
    }
}
