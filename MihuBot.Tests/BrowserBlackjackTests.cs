using System.Security.Claims;
using System.Text.Json;
using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BrowserBlackjackTests
{
    internal static ClaimsPrincipal User(ulong id, string? name = null, string authenticationType = "Discord") =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Name, name ?? $"Player {id}")
        ], authenticationType));

    internal static BlackjackTable Table(params int[] ranks)
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

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Game(params int[] ranks) : IDisposable
    {
        public Clock Clock { get; } = new();
        public BrowserBlackjackService Service { get; private set; } = null!;
        public string Room { get; private set; } = null!;

        public Game Start()
        {
            Service = new(Clock, () => Table(ranks));
            (Room, string? error) = Service.CreateRoom(User(1));
            Assert.Null(error);
            return this;
        }

        public BrowserBlackjackState Read(ulong id = 1) => Service.Read(Room, User(id));

        public void Move(ulong id, BrowserBlackjackCommand command, decimal bet = 100)
        {
            Assert.Null(Service.Execute(Room, User(id), Read(id).Version, command, bet));
        }

        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public void OnlyAuthenticatedDiscordPlayersCanCreateOrMutate()
    {
        using var game = new Game().Start();
        ClaimsPrincipal[] rejected =
        [
            new(new ClaimsIdentity()),
            User(1, authenticationType: "GitHub"),
            User(0),
            new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")])),
        ];

        foreach (ClaimsPrincipal user in rejected)
        {
            Assert.NotNull(game.Service.CreateRoom(user).Error);
            Assert.NotNull(game.Service.Execute(game.Room, user, 0, BrowserBlackjackCommand.Join));
            Assert.Empty(game.Service.Read(game.Room, user).Actions);
            Assert.Empty(game.Service.GetOwnedRooms(user));
        }

        Assert.Empty(game.Read().Seats);
    }

    [Fact]
    public void BrowserRoomsKeepIndependentMoneyShoesAndHands()
    {
        using var game = new Game(10, 10, 8, 7).Start();
        (string second, string? error) = game.Service.CreateRoom(User(1));
        Assert.Null(error);
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);

        Assert.Equal(900, game.Read().Balance);
        Assert.Equal(308, game.Read().RemainingCards);
        Assert.Equal(1_000, game.Service.Read(second, User(1)).Balance);
        Assert.Equal(312, game.Service.Read(second, User(1)).RemainingCards);
        Assert.Empty(game.Service.Read(second, User(1)).Seats);
    }

    [Fact]
    public void FourSeatsBetChangesRefundsAndHostTransferAreSharedAcrossViewers()
    {
        using var game = new Game().Start();

        for (ulong id = 1; id <= 4; id++)
        {
            game.Move(id, BrowserBlackjackCommand.Join);
        }

        Assert.NotNull(game.Service.Execute(game.Room, User(5), game.Read().Version, BrowserBlackjackCommand.Join));
        DateTime deadline = game.Read().Deadline;
        game.Move(1, BrowserBlackjackCommand.Join, 250);
        Assert.Equal(750, game.Read().Balance);
        Assert.Equal(deadline, game.Read().Deadline);
        Assert.Equal(1ul, game.Read(2).HostId);
        Assert.Equal(4, game.Read(2).Seats.Count);
        Assert.NotNull(game.Service.Execute(game.Room, User(2), game.Read().Version, BrowserBlackjackCommand.Deal));
        game.Move(1, BrowserBlackjackCommand.Leave);
        Assert.Equal(1_000, game.Read().Balance);
        Assert.Equal(2ul, game.Read().HostId);
        Assert.Contains(BrowserBlackjackCommand.Deal, game.Read(2).Actions);
        Assert.DoesNotContain(BrowserBlackjackCommand.Deal, game.Read(1).Actions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(501)]
    [InlineData(10.5)]
    public void InvalidBetsAndUnknownActionsDoNotChangeState(decimal bet)
    {
        using var game = new Game().Start();
        Assert.NotNull(game.Service.Execute(game.Room, User(1), 0, BrowserBlackjackCommand.Join, bet));
        Assert.NotNull(game.Service.Execute(game.Room, User(1), 0, (BrowserBlackjackCommand)999));
        Assert.Empty(game.Read().Seats);
        Assert.Equal(1_000, game.Read().Balance);
        Assert.Equal(0, game.Read().Version);
    }

    [Fact]
    public void HoleCardAndDealerTotalStayOutOfEverySnapshotUntilSettlement()
    {
        using var game = new Game(10, 9, 6, 8, 7, 10, 5).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        BrowserBlackjackState before = game.Read();

        foreach (var viewer in new[] { User(1), User(2), User(3), new ClaimsPrincipal(new ClaimsIdentity()) })
        {
            var state = game.Service.Read(game.Room, viewer);
            Assert.Null(state.DealerTotal);
            Assert.Equal(new BrowserBlackjackCard(0, 0), state.Dealer[1]);
            Assert.Equal("[{\"Rank\":6,\"Suit\":0},{\"Rank\":0,\"Suit\":0}]", JsonSerializer.Serialize(state.Dealer));
        }

        game.Move(1, BrowserBlackjackCommand.Stand);
        game.Move(2, BrowserBlackjackCommand.Stand);
        var after = game.Read();
        Assert.True(after.Complete);
        Assert.Equal(21, after.DealerTotal);
        Assert.Equal(new[] { 6, 10, 5 }, after.Dealer.Select(c => c.Rank));
        Assert.Null(before.DealerTotal);
        Assert.Equal(0, before.Dealer[1].Rank);
        Assert.Null(before.Seats[0].Hands[0].Result);
    }

    [Fact]
    public void OnlyCurrentPlayerCanActAndConcurrentDuplicateMovesApplyOnce()
    {
        using var game = new Game(2, 9, 10, 3, 7, 8, 4, 5).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        long version = game.Read().Version;
        Assert.Empty(game.Read(2).Actions);
        Assert.NotNull(game.Service.Execute(game.Room, User(2), version, BrowserBlackjackCommand.Hit));
        Assert.NotNull(game.Service.Execute(game.Room, User(1), version, BrowserBlackjackCommand.Split));

        var errors = new string?[2];
        Parallel.For(0, 2, i =>
        {
            errors[i] = game.Service.Execute(game.Room, User(1), version, BrowserBlackjackCommand.Hit);
        });

        Assert.Single(errors, e => e is null);
        Assert.Single(errors, e => e is not null);
        Assert.Equal(version + 1, game.Read().Version);
        Assert.Equal(3, game.Read().Seats[0].Hands[0].Cards.Count);
        game.Move(1, BrowserBlackjackCommand.Stand);
        Assert.Equal(2ul, game.Read().ActivePlayerId);
        Assert.Contains(BrowserBlackjackCommand.Hit, game.Read(2).Actions);
        Assert.DoesNotContain(BrowserBlackjackCommand.Hit, game.Read(1).Actions);
    }

    [Fact]
    public void TimersRunWithoutViewersAndLateActionsCannotBeatExpiration()
    {
        using var game = new Game(2, 9, 10, 3, 7, 8).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        long lobbyVersion = game.Read().Version;
        game.Clock.Now += TimeSpan.FromSeconds(30);
        Assert.NotNull(game.Service.Execute(game.Room, User(1), lobbyVersion, BrowserBlackjackCommand.Leave));
        Assert.False(game.Read().Lobby);
        Assert.Equal(1ul, game.Read().ActivePlayerId);
        game.Clock.Now += TimeSpan.FromSeconds(30);
        game.Service.Sweep();
        Assert.Equal(2ul, game.Read().ActivePlayerId);
        Assert.Contains("timed out", game.Read().Notice, StringComparison.Ordinal);
        Assert.Equal(game.Clock.Now.UtcDateTime.AddSeconds(30), game.Read().Deadline);
        game.Clock.Now += TimeSpan.FromSeconds(30);
        game.Service.Sweep();
        Assert.True(game.Read().Complete);
        Assert.Equal(0ul, game.Read().ActivePlayerId);
    }

    [Fact]
    public void InsuranceMovesArePrivateToCurrentPlayerAndTimeoutOnlyDeclinesOne()
    {
        using var game = new Game(10, 9, 1, 8, 7, 10).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        Assert.True(game.Read().Insurance);
        Assert.Equal(new[] { BrowserBlackjackCommand.Close, BrowserBlackjackCommand.Insure, BrowserBlackjackCommand.DeclineInsurance }, game.Read().Actions);
        game.Move(1, BrowserBlackjackCommand.Insure);
        Assert.Equal(850, game.Read().Balance);
        Assert.Equal(50, game.Read().Seats[0].InsuranceBet);
        Assert.Equal(0, game.Read().Dealer[1].Rank);
        game.Clock.Now += TimeSpan.FromSeconds(30);
        game.Service.Sweep();
        Assert.True(game.Read().Complete);
        Assert.Equal(1_000, game.Read().Balance);
        Assert.Equal(150, game.Read().Seats[0].InsuranceReturned);
        Assert.Equal(900, game.Read(2).Balance);
    }

    [Fact]
    public void SplitHandsAndDoubleDownUseTheSameBrowserBalance()
    {
        using var game = new Game(8, 10, 8, 7, 3, 10, 2, 10).Start();
        game.Move(1, BrowserBlackjackCommand.Join, 100);
        game.Move(1, BrowserBlackjackCommand.Deal);
        game.Move(1, BrowserBlackjackCommand.Split);
        Assert.Equal(2, game.Read().Seats[0].Hands.Count);
        Assert.Equal(800, game.Read().Balance);
        game.Move(1, BrowserBlackjackCommand.Double);
        Assert.Equal(200, game.Read().Seats[0].Hands[0].Bet);
        Assert.True(game.Read().Seats[0].Hands[1].Active);
        game.Move(1, BrowserBlackjackCommand.Double);
        Assert.True(game.Read().Complete);
        Assert.Equal(1_400, game.Read().Balance);
    }

    [Fact]
    public void StaleControlsCannotAffectAnotherRoundAndPlayersMustOptInAgain()
    {
        using var game = new Game(10, 10, 8, 7).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        var playing = game.Read();
        game.Move(1, BrowserBlackjackCommand.Stand);
        decimal balance = game.Read().Balance;
        game.Move(2, BrowserBlackjackCommand.Join);
        var lobby = game.Read();
        Assert.NotEqual(playing.RoundId, lobby.RoundId);
        Assert.True(lobby.Version > playing.Version);
        Assert.Equal(2ul, Assert.Single(lobby.Seats).Id);
        Assert.Equal(balance, lobby.Balance);
        Assert.NotNull(game.Service.Execute(game.Room, User(1), playing.Version, BrowserBlackjackCommand.Hit));
        Assert.Equal(lobby.Version, game.Read().Version);
    }

    [Fact]
    public void RebuyRequiresLowBalanceAndNoActiveWagerAndIncrementsVersion()
    {
        using var game = new Game(10, 10, 6, 9, 10, 10, 6, 9).Start();
        Assert.NotNull(game.Service.Execute(game.Room, User(1), 0, BrowserBlackjackCommand.Rebuy));

        for (int round = 0; round < 2; round++)
        {
            game.Move(1, BrowserBlackjackCommand.Join, 500);
            Assert.NotNull(game.Service.Execute(game.Room, User(1), game.Read().Version, BrowserBlackjackCommand.Rebuy));
            game.Move(1, BrowserBlackjackCommand.Deal);
            game.Move(1, BrowserBlackjackCommand.Stand);
        }

        var state = game.Read();
        Assert.Equal(0, state.Balance);
        Assert.Contains(BrowserBlackjackCommand.Rebuy, state.Actions);
        game.Move(1, BrowserBlackjackCommand.Rebuy);
        Assert.Equal(1_000, game.Read().Balance);
        Assert.Equal(1_000, game.Read().Seats[0].Balance);
        Assert.Equal(-500, game.Read().Seats[0].Change);
        Assert.Equal(state.Version + 1, game.Read().Version);
    }

    [Fact]
    public void RoomLimitsOwnershipAndIdleCleanupAreEnforced()
    {
        using var game = new Game().Start();

        for (int i = 1; i < BrowserBlackjackService.MaxRoomsPerOwner; i++)
        {
            Assert.Null(game.Service.CreateRoom(User(1)).Error);
        }

        Assert.NotNull(game.Service.CreateRoom(User(1)).Error);
        Assert.Equal(BrowserBlackjackService.MaxRoomsPerOwner, game.Service.GetOwnedRooms(User(1)).Count);
        Assert.Empty(game.Service.GetOwnedRooms(User(2)));
        game.Clock.Now += BrowserBlackjackService.IdleLifetime;
        game.Service.Sweep();
        Assert.Null(game.Read());
        Assert.NotNull(game.Service.Execute(game.Room, User(1), 0, BrowserBlackjackCommand.Join));
        Assert.Null(game.Service.Read("not-a-room", User(1)));
        Assert.Null(game.Service.CreateRoom(User(1)).Error);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SelectedDeckCountControlsShoeAndDefaultRemainsSixDecks(int decks)
    {
        using var service = new BrowserBlackjackService();
        string room = service.CreateRoom(User(1), decks).RoomId;
        Assert.Equal(decks, service.Read(room, User(1)).DeckCount);
        Assert.Null(service.Execute(room, User(1), 0, BrowserBlackjackCommand.Join));
        Assert.Null(service.Execute(room, User(1), 1, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, User(1));
        int dealt = state.Dealer.Count + state.Seats.SelectMany(p => p.Hands).Sum(h => h.Cards.Count);
        Assert.Equal(decks * 52, state.RemainingCards + dealt);
        var shoe = new BlackjackShoe();
        shoe.PrepareRound();
        Assert.Equal(6, shoe.DeckCount);
        Assert.Equal(312, shoe.Remaining);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SelectedShoesHaveCompleteDecksAndUseTheirOwnCutCard(int decks)
    {
        var shoe = new BlackjackShoe(decks);
        Assert.True(shoe.PrepareRound());

        while (shoe.Remaining > decks * 52 / 4)
        {
            shoe.Draw();
        }

        Assert.True(shoe.PrepareRound());
        Assert.Equal(2, shoe.Number);
        var cards = Enumerable.Range(0, decks * 52).Select(_ => shoe.Draw()).ToArray();
        Assert.Equal(52, cards.Distinct().Count());
        Assert.All(cards.GroupBy(c => c), group => Assert.Equal(decks, group.Count()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void UnsupportedDeckCountsDoNotCreateRooms(int decks)
    {
        using var service = new BrowserBlackjackService();
        Assert.NotNull(service.CreateRoom(User(1), decks).Error);
        Assert.Empty(service.GetOwnedRooms(User(1)));
    }

    [Fact]
    public void AdviceIsOptInAndOnlyCountsExposedCardsOnceIncludingAfterReveal()
    {
        using var game = new Game(2, 10, 3, 13).Start();
        Assert.Null(game.Read().Advice);
        var empty = game.Service.Read(game.Room, new ClaimsPrincipal(new ClaimsIdentity()), includeAdvice: true).Advice;
        Assert.Equal(0, empty.RunningCount);
        Assert.Null(empty.Move);
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        long version = game.Read().Version;
        var before = game.Service.Read(game.Room, User(1), includeAdvice: true).Advice;
        Assert.Equal(1, before.RunningCount);
        Assert.Equal(309 / 52d, before.UnseenDecks);
        Assert.Equal(52 / 309d, before.TrueCount, 10);
        Assert.Equal(BrowserBlackjackCommand.Hit, before.Move);
        Assert.Equal(before, game.Service.Read(game.Room, User(1), includeAdvice: true).Advice);
        Assert.Null(game.Service.Read(game.Room, User(2), includeAdvice: true).Advice.Move);
        Assert.Equal(version, game.Read().Version);
        game.Move(1, BrowserBlackjackCommand.Stand);
        var after = game.Service.Read(game.Room, User(1), includeAdvice: true).Advice;
        Assert.Equal(0, after.RunningCount);
        Assert.Equal(308 / 52d, after.UnseenDecks);
        Assert.Null(after.Move);
        game.Move(1, BrowserBlackjackCommand.Join);
        Assert.Equal(after.RunningCount, game.Service.Read(game.Room, User(1), includeAdvice: true).Advice.RunningCount);
        Assert.Equal(after.UnseenDecks, game.Service.Read(game.Room, User(1), includeAdvice: true).Advice.UnseenDecks);
    }

    [Fact]
    public void ChangingOnlyTheUnrevealedHoleCardCannotChangeCountsOrAdvice()
    {
        using var blackjack = new Game(10, 1, 9, 10).Start();
        using var noBlackjack = new Game(10, 1, 9, 2).Start();

        foreach (Game game in new[] { blackjack, noBlackjack })
        {
            game.Move(1, BrowserBlackjackCommand.Join);
            game.Move(1, BrowserBlackjackCommand.Deal);
        }

        Assert.True(blackjack.Read().Insurance);
        Assert.True(noBlackjack.Read().Insurance);
        Assert.Equal(blackjack.Service.Read(blackjack.Room, User(1), true).Advice,
            noBlackjack.Service.Read(noBlackjack.Room, User(1), true).Advice);
    }

    [Fact]
    public void ShuffleResetsTheExposedCardLedger()
    {
        BlackjackTable table = Table(10, 6, 8, 10, 10);
        using var service = new BrowserBlackjackService(new Clock(), () => table);
        string room = service.CreateRoom(User(1)).RoomId;
        Assert.Null(service.Execute(room, User(1), 0, BrowserBlackjackCommand.Join));
        Assert.Null(service.Execute(room, User(1), 1, BrowserBlackjackCommand.Deal));
        Assert.Null(service.Execute(room, User(1), 2, BrowserBlackjackCommand.Stand));
        Assert.Equal(-2, service.Read(room, User(1), true).Advice.RunningCount);

        while (table.Shoe.Remaining > BlackjackShoe.CutCardRemaining)
        {
            table.Shoe.Draw();
        }

        Assert.Null(service.Execute(room, User(1), 3, BrowserBlackjackCommand.Join));
        Assert.Null(service.Execute(room, User(1), 4, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, User(1), true);
        Assert.Equal(2, state.ShoeNumber);
        var cards = state.Dealer.Concat(state.Seats.SelectMany(p => p.Hands).SelectMany(h => h.Cards))
            .Where(c => c.Rank != 0).ToArray();
        Assert.Equal(cards.Sum(c => c.Rank is >= 2 and <= 6 ? 1 : c.Rank == 1 || c.Rank >= 10 ? -1 : 0), state.Advice.RunningCount);
        Assert.Equal((312 - cards.Length) / 52d, state.Advice.UnseenDecks);
    }

    [Fact]
    public void SplitDoesNotDoubleCountCardsAndTimeoutRevealUpdatesTheLedger()
    {
        using var game = new Game(8, 6, 8, 10, 3, 5, 10).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        Assert.Equal(1, game.Service.Read(game.Room, User(1), true).Advice.RunningCount);
        game.Move(1, BrowserBlackjackCommand.Split);
        Assert.Equal(2, game.Service.Read(game.Room, User(1), true).Advice.RunningCount);
        Assert.Equal(308 / 52d, game.Service.Read(game.Room, User(1), true).Advice.UnseenDecks);
        game.Move(1, BrowserBlackjackCommand.Stand);
        Assert.Equal(3, game.Service.Read(game.Room, User(1), true).Advice.RunningCount);
        game.Clock.Now += TimeSpan.FromSeconds(30);
        game.Service.Sweep();
        Assert.True(game.Read().Complete);
        Assert.Equal(1, game.Service.Read(game.Room, User(1), true).Advice.RunningCount);
        Assert.Equal(305 / 52d, game.Service.Read(game.Room, User(1), true).Advice.UnseenDecks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HostCanCloseEmptyLobbyActiveAndCompletedTables(int phase)
    {
        using var game = new Game(10, 10, 8, 7).Start();

        if (phase > 0)
        {
            game.Move(1, BrowserBlackjackCommand.Join);
        }

        if (phase > 1)
        {
            game.Move(1, BrowserBlackjackCommand.Deal);
        }

        if (phase > 2)
        {
            game.Move(1, BrowserBlackjackCommand.Stand);
        }

        var state = game.Read();
        Assert.Contains(BrowserBlackjackCommand.Close, state.Actions);
        Assert.DoesNotContain(BrowserBlackjackCommand.Close, game.Read(2).Actions);
        Assert.NotNull(game.Service.Execute(game.Room, User(2), state.Version, BrowserBlackjackCommand.Close));
        game.Move(1, BrowserBlackjackCommand.Close);
        Assert.Null(game.Read());
        Assert.Null(game.Read(2));
        Assert.Empty(game.Service.GetOwnedRooms(User(1)));
        Assert.NotNull(game.Service.Execute(game.Room, User(1), state.Version, BrowserBlackjackCommand.Join));
        Assert.NotNull(game.Service.Execute(game.Room, User(1), state.Version, BrowserBlackjackCommand.Close));
        game.Service.Sweep();
        Assert.Null(game.Read());
    }

    [Fact]
    public void ClosePermissionTransfersWithTheHostAndStaleRequestsAreRejected()
    {
        using var game = new Game().Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        long version = game.Read().Version;
        game.Move(2, BrowserBlackjackCommand.Join);
        Assert.NotNull(game.Service.Execute(game.Room, User(1), version, BrowserBlackjackCommand.Close));
        Assert.NotNull(game.Read());
        game.Move(1, BrowserBlackjackCommand.Leave);
        Assert.DoesNotContain(BrowserBlackjackCommand.Close, game.Read(1).Actions);
        Assert.Contains(BrowserBlackjackCommand.Close, game.Read(2).Actions);
        Assert.NotNull(game.Service.Execute(game.Room, User(1), game.Read().Version, BrowserBlackjackCommand.Close));
        game.Move(2, BrowserBlackjackCommand.Close);
        Assert.Null(game.Read());
    }

    [Fact]
    public void ClosingReclaimsQuotaWithoutAffectingOtherTables()
    {
        using var game = new Game().Start();
        string other = game.Service.CreateRoom(User(1)).RoomId;
        Assert.Null(game.Service.CreateRoom(User(1)).Error);
        Assert.NotNull(game.Service.CreateRoom(User(1)).Error);
        game.Move(1, BrowserBlackjackCommand.Close);
        Assert.Null(game.Service.CreateRoom(User(1)).Error);
        Assert.NotNull(game.Service.Read(other, User(1)));
    }

    [Fact]
    public void EmptyTableReturnsClosePermissionToItsCreator()
    {
        using var game = new Game().Start();
        game.Move(2, BrowserBlackjackCommand.Join);
        Assert.Contains(BrowserBlackjackCommand.Close, game.Read(2).Actions);
        Assert.DoesNotContain(BrowserBlackjackCommand.Close, game.Read(1).Actions);
        game.Move(2, BrowserBlackjackCommand.Leave);
        Assert.Equal(1ul, game.Read().HostId);
        Assert.Contains(BrowserBlackjackCommand.Close, game.Read(1).Actions);
        Assert.DoesNotContain(BrowserBlackjackCommand.Close, game.Read(2).Actions);
    }

    [Fact]
    public void ReadingATableKeepsItAliveButDoesNotExtendTurnDeadline()
    {
        using var game = new Game(10, 10, 8, 7).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        DateTime deadline = game.Read().Deadline;
        game.Clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(deadline, game.Read(2).Deadline);
        game.Move(1, BrowserBlackjackCommand.Leave);
        game.Clock.Now += BrowserBlackjackService.IdleLifetime - TimeSpan.FromSeconds(1);
        Assert.NotNull(game.Read(2));
        game.Clock.Now += TimeSpan.FromSeconds(2);
        game.Service.Sweep();
        Assert.NotNull(game.Read());
    }
}
