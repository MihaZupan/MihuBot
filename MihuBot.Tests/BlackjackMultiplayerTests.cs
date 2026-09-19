using Discord;
using MihuBot.Discord.Commands;
using MihuBot.Discord.Games;
using SkiaSharp;

namespace MihuBot.Tests;

public sealed class BlackjackMultiplayerTests
{
    private static readonly DateTime s_now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static BlackjackTable Table(params int[] ranks)
    {
        var cards = Enumerable.Range(0, 6).SelectMany(_ => Enumerable.Range(0, 4)
            .SelectMany(s => Enumerable.Range(1, 13).Select(r => new BlackjackCard(r, s)))).ToList();
        var front = new List<BlackjackCard>();

        foreach (int rank in ranks)
        {
            var card = new BlackjackCard(rank, 0);
            Assert.True(cards.Remove(card));
            front.Add(card);
        }

        return new BlackjackTable(new BlackjackShoe(front.Concat(cards)));
    }

    private static void Join(BlackjackTable table, ulong player, decimal bet = 100)
    {
        Assert.Null(table.Join(player, $"Player {player}", bet, s_now));
    }

    private static void Act(BlackjackTable table, BlackjackAction action)
    {
        Assert.True(table.TryAct(table.Game.ActivePlayer.Id, table.CustomId(action), s_now));
    }

    [Fact]
    public void JoiningReservesBetsWithoutDealingAndDoesNotExtendBettingWindow()
    {
        var table = Table();
        Join(table, 1);
        string joinId = BetMenu(table).CustomId;
        Assert.Null(table.Join(2, "Second", 50, s_now.AddSeconds(20)));

        Assert.True(table.IsLobby);
        Assert.Null(table.Game);
        Assert.Equal(900, table.GetBalance(1));
        Assert.Equal(950, table.GetBalance(2));
        Assert.Equal(312, table.Shoe.Remaining);
        Assert.Equal(s_now + BlackjackTable.BettingTime, table.Deadline);
        Assert.Equal(joinId, BetMenu(table).CustomId);
        Assert.Null(table.Join(1, "Player 1", 10, s_now));
        Assert.Equal(990, table.GetBalance(1));
        Assert.Equal(2, table.Seats.Count);
    }

    [Fact]
    public void TableLimitIsFourAndOnlyTheHostCanDealEarly()
    {
        var table = Table(2, 3, 4, 5, 10, 3, 4, 5, 6, 7);

        for (ulong id = 1; id <= 4; id++)
        {
            Join(table, id);
        }

        Assert.NotNull(table.Join(5, "Too late", 10, s_now));
        Assert.Equal(1_000, table.GetBalance(5));
        Assert.NotNull(table.Deal(2, s_now));
        Assert.True(table.IsLobby);
        Assert.Null(table.Deal(1, s_now));
        Assert.False(table.IsLobby);
        Assert.NotNull(table.Join(5, "Late arrival", 10, s_now));
        Assert.NotNull(table.Leave(1, s_now));
        Assert.NotNull(table.Rebuy(1));
    }

    [Fact]
    public void LeavingRefundsExactlyOnceAndTransfersHost()
    {
        var table = Table();
        Join(table, 1);
        Join(table, 2, 250);

        Assert.Null(table.Leave(1, s_now));
        Assert.Equal(1_000, table.GetBalance(1));
        Assert.Equal(2ul, table.HostId);
        Assert.NotNull(table.Leave(1, s_now));
        Assert.Null(table.Leave(2, s_now));
        Assert.Equal(1_000, table.GetBalance(2));
        Assert.False(table.IsActive);
        Assert.False(table.Expire(s_now.AddMinutes(1)));
        Assert.Equal(312, table.Shoe.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(501)]
    [InlineData(10.5)]
    public void InvalidWagersDoNotCreateASeat(decimal bet)
    {
        var table = Table();

        Assert.NotNull(table.Join(1, "Player", bet, s_now));
        Assert.Empty(table.Seats);
        Assert.Equal(1_000, table.GetBalance(1));
    }

    [Fact]
    public void LobbyDeadlineAutoDealsAndRejectsLateJoinsOrRefunds()
    {
        var table = Table(10, 10, 8, 7);
        Join(table, 1);

        Assert.False(table.Expire(table.Deadline.AddTicks(-1)));
        Assert.NotNull(table.Join(2, "Late", 100, table.Deadline));
        Assert.NotNull(table.Leave(1, table.Deadline));
        DateTime now = table.Deadline;
        Assert.True(table.Expire(now));
        Assert.False(table.IsLobby);
        Assert.Equal(now + BlackjackTable.TurnTime, table.Deadline);
        Assert.Equal(900, table.GetBalance(1));
        Assert.Equal(308, table.Shoe.Remaining);
    }

    [Fact]
    public void PlayersShareOneDealerAndCardsAreDealtAroundTheTable()
    {
        var table = Table(10, 9, 6, 8, 7, 10, 5);
        Join(table, 1);
        Join(table, 2, 50);
        Assert.Null(table.Deal(1, s_now));
        BlackjackGame game = table.Game;

        Assert.Equal(new[] { 10, 8 }, game.Players[0].Hands[0].Cards.Select(c => c.Rank));
        Assert.Equal(new[] { 9, 7 }, game.Players[1].Hands[0].Cards.Select(c => c.Rank));
        Assert.Equal(new[] { 6, 10 }, game.Dealer.Select(c => c.Rank));
        Assert.Equal(1ul, game.ActivePlayer.Id);
        Act(table, BlackjackAction.Stand);
        Assert.Equal(2ul, game.ActivePlayer.Id);
        Assert.Equal(2, game.Dealer.Count);
        Assert.False(game.IsComplete);
        Act(table, BlackjackAction.Stand);
        Assert.Equal(new[] { 6, 10, 5 }, game.Dealer.Select(c => c.Rank));
        Assert.True(game.IsComplete);
        Assert.Equal(900, table.GetBalance(1));
        Assert.Equal(950, table.GetBalance(2));
        Assert.False(table.Expire(s_now.AddHours(1)));
    }

    [Fact]
    public void OnlyCurrentSeatCanActAndDuplicateOrUnavailableClicksDoNotMutateState()
    {
        var table = Table(2, 9, 10, 3, 7, 8, 4, 5);
        Join(table, 1);
        Join(table, 2);
        table.Deal(1, s_now);
        string hit = table.CustomId(BlackjackAction.Hit);
        DateTime deadline = table.Deadline;
        int revision = table.Revision;

        Assert.False(table.TryAct(2, hit, s_now));
        Assert.False(table.TryAct(999, hit, s_now));
        Assert.False(table.TryAct(1, table.CustomId(BlackjackAction.Split), s_now));
        Assert.Equal(revision, table.Revision);
        Assert.Equal(deadline, table.Deadline);
        Assert.True(table.TryAct(1, hit, s_now.AddSeconds(10)));
        Assert.Equal(s_now.AddSeconds(10) + BlackjackTable.TurnTime, table.Deadline);
        Assert.False(table.TryAct(1, hit, s_now.AddSeconds(10)));
        Assert.Equal(3, table.Game.ActiveHand.Cards.Count);
        Act(table, BlackjackAction.Stand);
        Assert.False(table.TryAct(1, table.CustomId(BlackjackAction.Hit), s_now));
    }

    [Fact]
    public void APlayersSplitsFinishBeforeTheNextSeatAndDealerWaits()
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
    public void InsuranceDoesNotPeekUntilEverySeatHasDecided()
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
        Assert.Contains("hidden card", BlackjackCommand.BuildTranscript(table), StringComparison.Ordinal);
        Act(table, BlackjackAction.DeclineInsurance);
        Assert.True(table.Game.IsComplete);
        Assert.Equal(1_000, table.GetBalance(1));
        Assert.Equal(1_000, table.GetBalance(2));
        Assert.Contains("insurance bet 50, net +100", BlackjackCommand.BuildTranscript(table), StringComparison.Ordinal);
        AssertImages(table);
    }

    [Fact]
    public void NaturalSkipsItsTurnButDoesNotExposeDealerOrForceSettlementForOtherPlayers()
    {
        var table = Table(1, 10, 6, 13, 8, 10, 10);
        Join(table, 1);
        Join(table, 2);
        table.Deal(1, s_now);

        Assert.Equal(2ul, table.Game.ActivePlayer.Id);
        Assert.False(table.Game.IsComplete);
        Assert.Equal(2, table.Game.Dealer.Count);
        Assert.Contains("hidden card", BlackjackCommand.BuildTranscript(table), StringComparison.Ordinal);
        Act(table, BlackjackAction.Stand);
        Assert.Equal(1_150, table.GetBalance(1));
        Assert.Equal(1_100, table.GetBalance(2));
    }

    [Fact]
    public void TimingOutOnlyStandsTheCurrentPlayersSplitHands()
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
    public void InsuranceTimeoutDoesNotChooseForOtherSeats()
    {
        var table = Table(10, 9, 1, 8, 7, 6);
        Join(table, 1);
        Join(table, 2);
        table.Deal(1, s_now);

        Assert.True(table.Expire(table.Deadline));
        Assert.True(table.Game.OfferingInsurance);
        Assert.Equal(2ul, table.Game.ActivePlayer.Id);
        Assert.Equal(0, table.Game.Players[0].InsuranceBet);
        Assert.True(table.Expire(table.Deadline));
        Assert.False(table.Game.OfferingInsurance);
        Assert.Equal(1ul, table.Game.ActivePlayer.Id);
        Assert.False(table.Game.IsComplete);
    }

    [Fact]
    public void RebuyAndNextRoundDoNotReapplyOldPayoutsOrOldButtons()
    {
        var table = Table(10, 10, 8, 7);
        Join(table, 1);
        table.Deal(1, s_now);
        string oldStand = table.CustomId(BlackjackAction.Stand);
        Act(table, BlackjackAction.Stand);
        Assert.Equal(1_100, table.GetBalance(1));
        Assert.NotNull(table.Rebuy(1));
        Assert.Null(table.Join(2, "New player", 10, s_now));
        Assert.Single(table.Seats);
        Assert.Equal(1_100, table.GetBalance(1));
        Assert.Equal(990, table.GetBalance(2));
        Assert.False(table.TryAct(1, oldStand, s_now));
        Assert.False(table.Expire(s_now));
        Assert.Equal(1_100, table.GetBalance(1));
    }

    [Fact]
    public void InsolventPlayerCanRebuyButCannotBorrowFromOtherSeats()
    {
        // Two losing all-in rounds, with a fresh betting window between them.
        var table = Table(10, 10, 6, 9, 10, 10, 6, 9);

        for (int i = 0; i < 2; i++)
        {
            Join(table, 1, 500);
            Assert.Null(table.Deal(1, s_now));
            Act(table, BlackjackAction.Stand);
        }

        Assert.Equal(0, table.GetBalance(1));
        Assert.NotNull(table.Join(1, "Player", 10, s_now));
        Assert.Equal(1_000, table.GetBalance(2));
        Assert.Null(table.Rebuy(1));
        Assert.Equal(1_000, table.GetBalance(1));
        Assert.NotNull(table.Rebuy(1));
    }

    [Fact]
    public void ShoeReservesEnoughCardsForAllSeatsSplitting()
    {
        // More than 78 cards but too few raw card points to guarantee a four-seat round.
        var lowCards = new BlackjackShoe(Enumerable.Repeat(new BlackjackCard(2, 0), 100));
        Assert.True(lowCards.PrepareRound(4));
        Assert.Equal(312, lowCards.Remaining);
        Assert.Equal(2, lowCards.Number);

        var highCards = new BlackjackShoe(Enumerable.Repeat(new BlackjackCard(10, 0), 100));
        Assert.False(highCards.PrepareRound(4));
        Assert.Equal(100, highCards.Remaining);
    }

    [Fact]
    public void RendererProducesBoundedPngsForLobbyTurnsSplitsAndSettlement()
    {
        var table = Table(8, 10, 10, 8, 9, 7, 2, 10);
        Join(table, 1);
        Join(table, 2);
        BlackjackRenderer.PanelImage[] lobby = AssertImages(table);
        Assert.Null(table.Deal(1, s_now));
        BlackjackRenderer.PanelImage[] playing = AssertImages(table);
        Assert.NotEqual(lobby.SelectMany(p => p.Png), playing.SelectMany(p => p.Png));
        Act(table, BlackjackAction.Split);
        BlackjackRenderer.PanelImage[] split = AssertImages(table);

        using (SKBitmap image = SKBitmap.Decode(split[1].Png))
        {
            Assert.Equal(420, image.Height);
        }

        table.Expire(table.Deadline);
        table.Expire(table.Deadline);
        BlackjackRenderer.PanelImage[] settled = AssertImages(table);
        Embed[] embeds = BlackjackCommand.BuildEmbeds(table, settled);
        Assert.Equal(3, embeds.Length);
        Assert.Contains("+100", embeds[2].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenHoleCardCannotAffectPixelsTranscriptOrEmbedBeforeSettlement()
    {
        var first = Table(10, 6, 8, 11);
        var second = Table(10, 6, 8, 9);

        foreach (BlackjackTable table in new[] { first, second })
        {
            Join(table, 1);
            table.Deal(1, s_now);
        }

        Assert.Equal(BlackjackRenderer.Render(first).SelectMany(p => p.Png), BlackjackRenderer.Render(second).SelectMany(p => p.Png));
        Assert.Equal(BlackjackCommand.BuildTranscript(first), BlackjackCommand.BuildTranscript(second));
        Embed firstEmbed = BlackjackCommand.BuildEmbed(first, "board.png");
        Embed secondEmbed = BlackjackCommand.BuildEmbed(second, "board.png");
        Assert.Equal(firstEmbed.Description, secondEmbed.Description);
        Assert.Equal(firstEmbed.Fields.Select(f => (f.Name, f.Value)), secondEmbed.Fields.Select(f => (f.Name, f.Value)));
        Assert.DoesNotContain("JC", BlackjackCommand.BuildTranscript(first), StringComparison.Ordinal);
        Assert.DoesNotContain("9C", BlackjackCommand.BuildTranscript(second), StringComparison.Ordinal);

        Act(first, BlackjackAction.Stand);
        Act(second, BlackjackAction.Stand);
        Assert.Contains("JC", BlackjackCommand.BuildTranscript(first), StringComparison.Ordinal);
        Assert.Contains("9C", BlackjackCommand.BuildTranscript(second), StringComparison.Ordinal);
        Assert.NotEqual(BlackjackRenderer.Render(first).SelectMany(p => p.Png), BlackjackRenderer.Render(second).SelectMany(p => p.Png));
    }

    [Fact]
    public void RenderedTurnChangesAndControlsMatchTheLivePhase()
    {
        var table = Table(10, 10, 10, 8, 8, 7);
        Join(table, 1);
        Join(table, 2);
        Assert.Equal(2, Buttons(table).Length);
        Assert.NotNull(BetMenu(table));
        table.Deal(1, s_now);
        ButtonComponent[] before = Buttons(table);
        Assert.Contains(before, b => b.CustomId == table.CustomId(BlackjackAction.Hit));
        BlackjackRenderer.PanelImage[] firstTurn = AssertImages(table);
        Act(table, BlackjackAction.Stand);
        Assert.NotEqual(firstTurn.SelectMany(p => p.Png), AssertImages(table).SelectMany(p => p.Png));
        Assert.DoesNotContain(Buttons(table), b => b.CustomId == before[0].CustomId);
        Act(table, BlackjackAction.Stand);
        Assert.Empty(Buttons(table));
        Assert.Contains("next round", BetMenu(table).Placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void FourSeatsWithMaximumSplitsFitImageAndDiscordLimits()
    {
        var table = Table(2, 3, 4, 5, 10, 3, 4, 5, 6, 7);

        for (ulong i = 1; i <= 4; i++)
        {
            Assert.Null(table.Join(i, new string('W', 150) + "\nplayer", 100, s_now));
        }

        table.Deal(1, s_now);

        foreach (BlackjackPlayer player in table.Game.Players)
        {
            for (int i = 0; i < 3; i++)
            {
                var hand = new BlackjackHand { Bet = 100, FromSplit = true };
                hand.Cards.AddRange([new(10, 1), new(1, 3)]);
                player.Hands.Add(hand);
            }
        }

        BlackjackRenderer.PanelImage[] panels = AssertImages(table);
        Assert.Equal(9, panels.Length);

        foreach (BlackjackPlayer player in table.Seats)
        {
            int seat = table.Seats.ToList().IndexOf(player);
            Assert.Equal(new[] { 0, 2 }, panels.Where(p => p.SeatIndex == seat).Select(p => p.FirstHandIndex));
            Assert.All(panels.Where(p => p.SeatIndex == seat), p => Assert.Equal(2, p.HandCount));
        }

        Assert.InRange(BlackjackCommand.BuildTranscript(table).Length, 1, 1_024);
        Assert.Equal(9, BlackjackCommand.BuildEmbeds(table, panels).Length);
    }

    [Fact]
    public void AnEmptyOrRefundedTableCanBeDisplayedWithoutAnActiveDeadline()
    {
        var table = Table();
        Assert.Single(AssertImages(table));
        Assert.Contains("Choose a bet", BlackjackCommand.BuildEmbed(table, "board.png").Description, StringComparison.Ordinal);
        Join(table, 1);
        table.Leave(1, s_now);
        Assert.Single(AssertImages(table));
        Assert.Empty(Buttons(table));
        Assert.NotNull(BetMenu(table));
        Assert.Empty(BlackjackCommand.BuildEmbed(table, "board.png").Fields);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OnlyOccupiedSeatsGetPanelsAndAttachmentsHaveMatchingUniqueEmbeds(int players)
    {
        var table = Table();

        for (ulong player = 1; player <= (ulong)players; player++)
        {
            Join(table, player);
        }

        BlackjackRenderer.PanelImage[] panels = AssertImages(table);
        Assert.Equal(players + 1, panels.Length);
        string[] filenames = panels.Select(p => BlackjackCommand.PanelFilename(table, p)).ToArray();
        table.Deal(1, s_now);
        BlackjackRenderer.PanelImage[] dealt = AssertImages(table);
        Assert.Equal(players + 1, dealt.Length);
        Assert.DoesNotContain(BlackjackCommand.PanelFilename(table, dealt[0]), filenames);
    }

    [Fact]
    public void PanelDescriptionsDescribeOnlyTheirOwnCards()
    {
        var table = Table(8, 10, 10, 8, 9, 7, 2, 10);
        Join(table, 1);
        Join(table, 2);
        table.Deal(1, s_now);
        Act(table, BlackjackAction.Split);

        string dealer = BlackjackCommand.BuildTranscript(table, -1, 0, 0);
        Assert.Contains("hidden card", dealer, StringComparison.Ordinal);
        Assert.DoesNotContain("Seat", dealer, StringComparison.Ordinal);
        string firstHand = BlackjackCommand.BuildTranscript(table, 0, 0, 1);
        Assert.Contains("Seat 1", firstHand, StringComparison.Ordinal);
        Assert.Contains("2C", firstHand, StringComparison.Ordinal);
        Assert.DoesNotContain("Seat 2", firstHand, StringComparison.Ordinal);
        Assert.DoesNotContain("Dealer", firstHand, StringComparison.Ordinal);
        Assert.DoesNotContain("2C", BlackjackCommand.BuildTranscript(table, 0, 1, 1), StringComparison.Ordinal);
    }

    private static BlackjackRenderer.PanelImage[] AssertImages(BlackjackTable table)
    {
        BlackjackRenderer.PanelImage[] panels = BlackjackRenderer.Render(table);
        Assert.InRange(panels.Length, 1, 9);
        Assert.Equal(-1, panels[0].SeatIndex);
        Assert.Equal(panels.Length, panels.Select(p => BlackjackCommand.PanelFilename(table, p)).Distinct().Count());
        Embed[] embeds = BlackjackCommand.BuildEmbeds(table, panels);
        Assert.Equal(panels.Length, embeds.Length);

        for (int i = 0; i < panels.Length; i++)
        {
            BlackjackRenderer.PanelImage panel = panels[i];
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, panel.Png[..8]);
            Assert.InRange(panel.Png.Length, 10_000, 1_000_000);
            Assert.Equal($"attachment://{BlackjackCommand.PanelFilename(table, panel)}", embeds[i].Image!.Value.Url);
            Assert.Null(embeds[i].Url);
            Assert.InRange(BlackjackCommand.BuildTranscript(table, panel.SeatIndex, panel.FirstHandIndex, panel.HandCount).Length, 1, 1_024);
            using SKBitmap image = SKBitmap.Decode(panel.Png);
            Assert.NotNull(image);
            Assert.Equal(800, image.Width);
            Assert.InRange(image.Height, 240, BlackjackRenderer.MaxPanelHeight);

            // Check readable type at a representative inline preview size, not just PNG resolution.
            double scale = Math.Min(400d / image.Width, 300d / image.Height);
            Assert.True(BlackjackRenderer.CardRankSize * scale >= 16);
            Assert.True(BlackjackRenderer.HandTextSize * scale >= 14);
        }

        return panels;
    }

    private static ButtonComponent[] Buttons(BlackjackTable table) =>
        BlackjackCommand.BuildComponents(table).Components.OfType<ActionRowComponent>()
            .SelectMany(row => row.Components).OfType<ButtonComponent>().ToArray();

    private static SelectMenuComponent BetMenu(BlackjackTable table) =>
        Assert.Single(BlackjackCommand.BuildComponents(table).Components.OfType<ActionRowComponent>()
            .SelectMany(row => row.Components).OfType<SelectMenuComponent>());

    [Fact]
    public void DropdownCanOpenAWindowJoinAndChangeBetsWithoutGivingUpASeat()
    {
        var table = Table();
        string initialId = BetMenu(table).CustomId;
        Assert.Null(BlackjackCommand.SelectBet(table, 1, "First", initialId, ["100"], s_now));
        Assert.NotNull(BlackjackCommand.SelectBet(table, 2, "Second", initialId, ["50"], s_now));
        string bettingId = BetMenu(table).CustomId;
        Assert.Null(BlackjackCommand.SelectBet(table, 2, "Second", bettingId, ["50"], s_now));
        BlackjackPlayer host = table.Seats[0];
        string roundId = table.Id;
        DateTime deadline = table.Deadline;

        foreach ((string wager, decimal available) in new[] { ("500", 500m), ("500", 500m), ("25", 975m), ("100", 900m) })
        {
            Assert.Null(BlackjackCommand.SelectBet(table, 1, "First", bettingId, [wager], s_now.AddSeconds(10)));
            Assert.Equal(available, table.GetBalance(1));
            Assert.Equal(available, host.Balance);
            Assert.Equal(1_000 - available, host.Hands[0].Bet);
            Assert.Equal(1_000, host.StartingBalance);
            Assert.Same(host, table.Seats[0]);
            Assert.Equal(1ul, table.HostId);
            Assert.Equal(new ulong[] { 1, 2 }, table.Seats.Select(p => p.Id));
            Assert.Equal(roundId, table.Id);
            Assert.Equal(deadline, table.Deadline);
            Assert.Equal(bettingId, BetMenu(table).CustomId);
            Assert.Equal(950, table.GetBalance(2));
        }

        Assert.Null(table.Leave(1, s_now.AddSeconds(11)));
        Assert.Equal(1_000, table.GetBalance(1));
    }

    [Fact]
    public void FullTablesStillAllowExistingPlayersToChangeTheirBet()
    {
        var table = Table();

        for (ulong player = 1; player <= 4; player++)
        {
            Join(table, player);
        }

        SelectMenuComponent menu = BetMenu(table);
        Assert.False(menu.IsDisabled);
        Assert.Null(BlackjackCommand.SelectBet(table, 4, "Fourth", menu.CustomId, ["250"], s_now));
        Assert.Equal(750, table.GetBalance(4));
        Assert.NotNull(BlackjackCommand.SelectBet(table, 5, "Fifth", menu.CustomId, ["250"], s_now));
        Assert.Equal(1_000, table.GetBalance(5));
        Assert.Equal(4, table.Seats.Count);
    }

    [Fact]
    public void ChangingBetsCountsTheExistingStakeButCannotOverdraw()
    {
        var table = Table(10, 10, 6, 9, 10, 10, 6, 9);

        foreach (int bet in new[] { 500, 100 })
        {
            Join(table, 1, bet);
            table.Deal(1, s_now);
            Act(table, BlackjackAction.Stand);
        }

        Assert.Equal(400, table.GetBalance(1));
        Join(table, 1);
        string id = BetMenu(table).CustomId;
        int revision = table.Revision;
        Assert.NotNull(BlackjackCommand.SelectBet(table, 1, "Player", id, ["500"], s_now));
        Assert.Equal(300, table.GetBalance(1));
        Assert.Equal(100, table.Seats[0].Hands[0].Bet);
        Assert.Equal(revision, table.Revision);
        Assert.Null(BlackjackCommand.SelectBet(table, 1, "Player", id, ["400"], s_now));
        Assert.Equal(0, table.GetBalance(1));
        Assert.Null(table.Leave(1, s_now));
        Assert.Equal(400, table.GetBalance(1));
    }

    [Theory]
    [InlineData()]
    [InlineData("100", "200")]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("100.0")]
    [InlineData("1e2")]
    [InlineData("123")]
    [InlineData("invalid")]
    public void InvalidDropdownValuesDoNotChangeTheBet(params string[] values)
    {
        var table = Table();
        Join(table, 1);
        int revision = table.Revision;

        Assert.NotNull(BlackjackCommand.SelectBet(table, 1, "Player", BetMenu(table).CustomId, values, s_now));
        Assert.Equal(900, table.GetBalance(1));
        Assert.Equal(100, table.Seats[0].Hands[0].Bet);
        Assert.Equal(revision, table.Revision);
    }

    [Fact]
    public void ExpiredOrDealtMenusCannotChangeAStakeOrAccidentallyStartAnotherRound()
    {
        var table = Table(10, 10, 8, 7);
        Join(table, 1);
        string id = BetMenu(table).CustomId;
        Assert.NotNull(BlackjackCommand.SelectBet(table, 1, "Player", id, ["200"], table.Deadline));
        Assert.Equal(900, table.GetBalance(1));
        table.Deal(1, s_now);
        Assert.NotNull(BlackjackCommand.SelectBet(table, 1, "Player", id, ["200"], s_now));
        Assert.NotNull(BlackjackCommand.SelectBet(table, 1, "Player", table.LobbyCustomId("NextBet"), ["200"], s_now));
        Act(table, BlackjackAction.Stand);
        Assert.NotNull(BlackjackCommand.SelectBet(table, 1, "Player", id, ["200"], s_now));
        Assert.Equal(1_100, table.GetBalance(1));
        Assert.True(table.Game.IsComplete);
        Assert.Null(BlackjackCommand.SelectBet(table, 1, "Player", BetMenu(table).CustomId, ["200"], s_now));
        Assert.True(table.IsLobby);
        Assert.Equal(900, table.GetBalance(1));
    }

    [Fact]
    public void DropdownUsesItsOwnRowAndHasNoSharedSelectedWager()
    {
        var table = Table();
        Join(table, 1);
        SelectMenuComponent menu = BetMenu(table);
        Assert.Equal(1, menu.MinValues);
        Assert.Equal(1, menu.MaxValues);
        Assert.InRange(menu.Options.Count, 2, 25);
        Assert.Contains(menu.Options, o => o.Value == "10");
        Assert.Contains(menu.Options, o => o.Value == "500");
        Assert.DoesNotContain(menu.Options, o => o.IsDefault == true);
        Assert.Equal(menu.Options.Count, menu.Options.Select(o => o.Value).Distinct().Count());
        ActionRowComponent[] rows = BlackjackCommand.BuildComponents(table).Components.OfType<ActionRowComponent>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.IsType<SelectMenuComponent>(Assert.Single(rows[0].Components));
        Assert.All(rows[1].Components, component => Assert.IsType<ButtonComponent>(component));
        Assert.Null(BlackjackCommand.SelectBet(table, 1, "Player", menu.CustomId, ["250"], s_now));
        Assert.DoesNotContain(BetMenu(table).Options, o => o.IsDefault == true);
    }
}
