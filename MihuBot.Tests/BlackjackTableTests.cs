using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BlackjackTableTests
{
    private static readonly DateTime s_now = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static BlackjackTable Table(params int[] ranks) => BrowserBlackjackTests.Table(ranks);

    private static void Join(BlackjackTable table, ulong player, decimal bet = 100) =>
        Assert.Null(table.Join(player, $"Player {player}", bet, s_now));

    private static void Act(BlackjackTable table, BlackjackAction action) =>
        Assert.True(table.TryAct(table.Game.ActivePlayer.Id, table.CustomId(action), s_now));

    [Fact]
    public void ChangingBetsPreservesSeatOrderHostAndDeadlineAndAdjustsOnlyTheDifference()
    {
        var table = Table();
        Join(table, 1);
        Join(table, 2, 50);
        BlackjackPlayer host = table.Seats[0];
        string round = table.Id;
        DateTime deadline = table.Deadline;

        foreach (decimal wager in new[] { 500m, 500m, 25m, 100m })
        {
            Assert.Null(table.Join(1, "First", wager, s_now.AddSeconds(10)));
            Assert.Same(host, table.Seats[0]);
            Assert.Equal(new ulong[] { 1, 2 }, table.Seats.Select(p => p.Id));
            Assert.Equal(1ul, table.HostId);
            Assert.Equal(round, table.Id);
            Assert.Equal(deadline, table.Deadline);
            Assert.Equal(1_000 - wager, table.GetBalance(1));
            Assert.Equal(1_000 - wager, host.Balance);
            Assert.Equal(wager, host.Hands[0].Bet);
            Assert.Equal(1_000, host.StartingBalance);
            Assert.Equal(950, table.GetBalance(2));
        }

        Assert.Null(table.Leave(1, s_now.AddSeconds(11)));
        Assert.Equal(1_000, table.GetBalance(1));
        Assert.Equal(2ul, table.HostId);
        Assert.NotNull(table.Leave(1, s_now));
        Assert.Equal(1_000, table.GetBalance(1));
    }

    [Fact]
    public void ChangingBetsIncludesTheExistingStakeButCannotOverdraw()
    {
        var table = Table(10, 10, 6, 9, 10, 10, 6, 9);

        foreach (int bet in new[] { 500, 100 })
        {
            Join(table, 1, bet);
            Assert.Null(table.Deal(1, s_now));
            Act(table, BlackjackAction.Stand);
        }

        Assert.Equal(400, table.GetBalance(1));
        Join(table, 1);
        int revision = table.Revision;
        Assert.NotNull(table.Join(1, "Player", 500, s_now));
        Assert.Equal(300, table.GetBalance(1));
        Assert.Equal(100, table.Seats[0].Hands[0].Bet);
        Assert.Equal(revision, table.Revision);
        Assert.Null(table.Join(1, "Player", 400, s_now));
        Assert.Equal(0, table.GetBalance(1));
        Assert.Null(table.Leave(1, s_now));
        Assert.Equal(400, table.GetBalance(1));
    }

    [Fact]
    public void OnlyTheHostCanDealAndCannotRedealAnActiveRound()
    {
        var table = Table(10, 10, 8, 7);
        Assert.NotNull(table.Deal(1, s_now));
        Join(table, 1);
        Assert.NotNull(table.Deal(2, s_now));
        Assert.True(table.IsLobby);
        Assert.Null(table.Deal(1, s_now));
        BlackjackGame game = table.Game;
        Assert.NotNull(table.Deal(1, s_now));
        Assert.Same(game, table.Game);
        Assert.Equal(308, table.Shoe.Remaining);
        Assert.Equal(900, table.GetBalance(1));
    }

    [Fact]
    public void AllSplitHandsFinishBeforeNextPlayerAndTheSharedDealer()
    {
        var table = Table(8, 10, 6, 8, 9, 10, 3, 10, 2, 10, 10);
        Join(table, 1);
        Join(table, 2, 50);
        table.Deal(1, s_now);
        Act(table, BlackjackAction.Split);
        Act(table, BlackjackAction.Double);
        Assert.Equal(1ul, table.Game.ActivePlayer.Id);
        Assert.Equal(1, table.Game.ActivePlayer.ActiveHandIndex);
        Act(table, BlackjackAction.Double);
        Assert.Equal(2ul, table.Game.ActivePlayer.Id);
        Assert.Equal(2, table.Game.Dealer.Count);
        Assert.Equal(600, table.GetBalance(1));
        Assert.Equal(950, table.GetBalance(2));
        Act(table, BlackjackAction.Stand);
        Assert.Equal(1_400, table.GetBalance(1));
        Assert.Equal(1_050, table.GetBalance(2));
        Assert.Equal(3, table.Game.Dealer.Count);
    }

    [Fact]
    public void InsuranceDoesNotPeekUntilAllSeatsDecideIncludingNaturals()
    {
        var table = Table(10, 1, 1, 8, 13, 10);
        Join(table, 1);
        Join(table, 2);
        table.Deal(1, s_now);
        Act(table, BlackjackAction.Insure);
        Assert.True(table.Game.OfferingInsurance);
        Assert.False(table.Game.IsComplete);
        Assert.Equal(2ul, table.Game.ActivePlayer.Id);
        Assert.Equal(850, table.GetBalance(1));
        Assert.False(table.Game.CanAct(BlackjackAction.Surrender));
        Act(table, BlackjackAction.DeclineInsurance);
        Assert.True(table.Game.IsComplete);
        Assert.Equal(1_000, table.GetBalance(1));
        Assert.Equal(1_000, table.GetBalance(2));
        Assert.Equal(150, table.Game.Players[0].InsuranceReturned);
    }

    [Fact]
    public void TimeoutOnlyStandsTheCurrentPlayersSplitHands()
    {
        var table = Table(8, 10, 10, 8, 9, 7, 2, 10);
        Join(table, 1);
        Join(table, 2);
        table.Deal(1, s_now);
        Act(table, BlackjackAction.Split);
        DateTime deadline = table.Deadline;
        Assert.False(table.Expire(deadline.AddTicks(-1)));
        Assert.False(table.TryAct(1, table.CustomId(BlackjackAction.Stand), deadline));
        Assert.True(table.Expire(deadline));
        Assert.Equal(2ul, table.Game.ActivePlayer.Id);
        Assert.All(table.Game.Players[0].Hands, h => Assert.True(h.Finished));
        Assert.False(table.Game.Players[1].Hands[0].Finished);
        Assert.False(table.Game.IsComplete);
        Assert.Equal(deadline + BlackjackTable.TurnTime, table.Deadline);
        Assert.False(table.Expire(deadline));
        Assert.True(table.Expire(table.Deadline));
        Assert.True(table.Game.IsComplete);
        Assert.Equal(1_000, table.GetBalance(1));
        Assert.Equal(1_100, table.GetBalance(2));
    }

    [Fact]
    public void DuplicateAndPreviousRoundActionsCannotPlayAnotherHand()
    {
        var table = Table(2, 10, 3, 7, 4);
        Join(table, 1);
        table.Deal(1, s_now);
        string hit = table.CustomId(BlackjackAction.Hit);
        Assert.False(table.TryAct(2, hit, s_now));
        Assert.True(table.TryAct(1, hit, s_now));
        Assert.False(table.TryAct(1, hit, s_now));
        string stand = table.CustomId(BlackjackAction.Stand);
        Assert.True(table.TryAct(1, stand, s_now));
        Join(table, 1);
        table.Deal(1, s_now);
        Assert.False(table.TryAct(1, stand, s_now));
    }

    [Fact]
    public void ShoeReservesEnoughCardsForEverySeatSplitting()
    {
        var lowCards = new BlackjackShoe(Enumerable.Repeat(new BlackjackCard(2, 0), 100));
        Assert.True(lowCards.PrepareRound(4));
        Assert.Equal(312, lowCards.Remaining);
        Assert.Equal(2, lowCards.Number);
        var highCards = new BlackjackShoe(Enumerable.Repeat(new BlackjackCard(10, 0), 100));
        Assert.False(highCards.PrepareRound(4));
        Assert.Equal(100, highCards.Remaining);
    }
}
