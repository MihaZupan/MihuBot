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

        return new BlackjackTable(new BlackjackShoe(front.Concat(cards), decks: 6));
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
    public void BrowserRoomsShareAccountMoneyButKeepIndependentShoesAndHands()
    {
        using var game = new Game(10, 10, 8, 7).Start();
        (string second, string? error) = game.Service.CreateRoom(User(1));
        Assert.Null(error);
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);

        Assert.Equal(900, game.Read().Balance);
        Assert.Equal(308, game.Read().RemainingCards);
        Assert.Equal(900, game.Service.Read(second, User(1)).Balance);
        Assert.Equal(312, game.Service.Read(second, User(1)).RemainingCards);
        Assert.Empty(game.Service.Read(second, User(1)).Seats);
    }

    [Fact]
    public void SixSeatsBetChangesRefundsAndHostTransferAreSharedAcrossViewers()
    {
        using var game = new Game().Start();

        for (ulong id = 1; id <= 6; id++)
        {
            game.Move(id, BrowserBlackjackCommand.Join);
        }

        Assert.Contains("All six seats", game.Service.Execute(game.Room, User(7), game.Read(7).Version, BrowserBlackjackCommand.Join), StringComparison.Ordinal);
        DateTime deadline = game.Read().Deadline;
        game.Move(1, BrowserBlackjackCommand.Join, 250);
        Assert.Equal(750, game.Read().Balance);
        Assert.Equal(deadline, game.Read().Deadline);
        Assert.Equal(1ul, game.Read(2).HostId);
        Assert.Equal(6, game.Read(2).Seats.Count);
        Assert.Contains("Only the host", game.Service.Execute(game.Room, User(2), game.Read(2).Version, BrowserBlackjackCommand.Deal), StringComparison.Ordinal);
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
    public void PlayersActIndependentlyAndConcurrentDuplicateMovesApplyOnce()
    {
        using var game = new Game(2, 9, 10, 3, 7, 8, 4, 5).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        long version = game.Read().Version;
        Assert.Contains(BrowserBlackjackCommand.Hit, game.Read(2).Actions);
        Assert.True(game.Read(2).YourTurn);
        Assert.Null(game.Service.Execute(game.Room, User(2), game.Read(2).Version, BrowserBlackjackCommand.Hit));
        Assert.Equal(version, game.Read().Version);
        Assert.NotNull(game.Service.Execute(game.Room, User(3), game.Read(3).Version, BrowserBlackjackCommand.Hit));
        Assert.NotNull(game.Service.Execute(game.Room, User(1), version, BrowserBlackjackCommand.Split));

        var errors = new string?[2];
        Parallel.For(0, 2, i =>
        {
            errors[i] = game.Service.Execute(game.Room, User(1), version, BrowserBlackjackCommand.Hit);
        });

        Assert.Single(errors, e => e is null);
        Assert.Single(errors, e => e is not null);
        Assert.Equal(version + 2, game.Read().Version);
        Assert.Equal(3, game.Read().Seats[0].Hands[0].Cards.Count);
        game.Move(1, BrowserBlackjackCommand.Stand);
        Assert.False(game.Read().YourTurn);
        Assert.True(game.Read(2).YourTurn);
        Assert.Contains(BrowserBlackjackCommand.Hit, game.Read(2).Actions);
        Assert.DoesNotContain(BrowserBlackjackCommand.Hit, game.Read(1).Actions);
    }

    [Fact]
    public void TimersRunWithoutViewersAndLateActionsCannotBeatExpiration()
    {
        using var game = new Game(2, 9, 10, 3, 7, 8, 4).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        long lobbyVersion = game.Read().Version;
        game.Clock.Now += TimeSpan.FromSeconds(30);
        Assert.NotNull(game.Service.Execute(game.Room, User(1), lobbyVersion, BrowserBlackjackCommand.Leave));
        Assert.False(game.Read().Lobby);
        Assert.True(game.Read().YourTurn);
        Assert.True(game.Read(2).YourTurn);
        game.Clock.Now += TimeSpan.FromSeconds(20);
        game.Move(1, BrowserBlackjackCommand.Hit);
        game.Clock.Now += TimeSpan.FromSeconds(10);
        game.Service.Sweep();
        Assert.True(game.Read().YourTurn);
        Assert.False(game.Read(2).YourTurn);
        Assert.Contains("Seat 2 timed out", game.Read().Notice, StringComparison.Ordinal);
        Assert.Equal(game.Clock.Now.UtcDateTime.AddSeconds(20), game.Read().Deadline);
        Assert.Null(game.Read().Seats[1].Deadline);
        game.Clock.Now += TimeSpan.FromSeconds(20);
        game.Service.Sweep();
        Assert.True(game.Read().Complete);
        Assert.False(game.Read().YourTurn);
    }

    [Fact]
    public void ExpiringAnotherPlayerDoesNotInvalidateAnInFlightMove()
    {
        using var game = new Game(2, 9, 10, 3, 7, 8, 4, 2).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        game.Clock.Now += TimeSpan.FromSeconds(20);
        game.Move(1, BrowserBlackjackCommand.Hit);
        long version = game.Read().Version;
        game.Clock.Now += TimeSpan.FromSeconds(10);

        Assert.Null(game.Service.Execute(game.Room, User(1), version, BrowserBlackjackCommand.Hit));
        Assert.True(game.Read().YourTurn);
        Assert.False(game.Read(2).YourTurn);
        Assert.Equal(4, game.Read().Seats[0].Hands[0].Cards.Count);
        Assert.Equal(game.Clock.Now.UtcDateTime.AddSeconds(30), game.Read().Deadline);
        Assert.False(game.Read().Complete);
    }

    [Fact]
    public void InsuranceDecisionsAreIndependentAndTimeoutFinishesTheBarrier()
    {
        using var game = new Game(10, 9, 1, 8, 7, 10).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        Assert.True(game.Read().Insurance);
        Assert.Contains(BrowserBlackjackCommand.Insure, game.Read(2).Actions);
        Assert.Equal(new[] { BrowserBlackjackCommand.Close, BrowserBlackjackCommand.Insure, BrowserBlackjackCommand.DeclineInsurance }, game.Read().Actions);
        game.Move(1, BrowserBlackjackCommand.Insure);
        Assert.False(game.Read().YourTurn);
        Assert.True(game.Read(2).YourTurn);
        Assert.DoesNotContain(BrowserBlackjackCommand.Hit, game.Read().Actions);
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
    public void SixPlayersCanJoinHitAndStandConcurrentlyWithoutStalePeerErrors()
    {
        using var game = new Game(2, 2, 2, 2, 2, 2, 10, 3, 3, 3, 3, 3, 3, 7, 4, 4, 4, 4, 4, 4).Start();
        var errors = new string?[6];
        Parallel.For(0, 6, i =>
            errors[i] = game.Service.Execute(game.Room, User((ulong)i + 1), 0, BrowserBlackjackCommand.Join, 100));
        Assert.All(errors, error => Assert.Null(error));
        Assert.Equal(6, game.Read().Seats.Count);
        game.Move(game.Read().HostId, BrowserBlackjackCommand.Deal);
        long[] versions = Enumerable.Range(1, 6).Select(id => game.Read((ulong)id).Version).ToArray();

        Parallel.For(0, 6, i =>
            errors[i] = game.Service.Execute(game.Room, User((ulong)i + 1), versions[i], BrowserBlackjackCommand.Hit));
        Assert.All(errors, error => Assert.Null(error));
        var playing = game.Read();
        Assert.False(playing.Complete);
        Assert.Equal(2, playing.Dealer.Count);
        Assert.Equal(0, playing.Dealer[1].Rank);
        Assert.Equal(292, playing.RemainingCards);
        Assert.All(playing.Seats, seat =>
        {
            Assert.True(seat.Pending);
            Assert.True(Assert.Single(seat.Hands).Active);
            Assert.Equal(3, seat.Hands[0].Cards.Count);
            Assert.Equal(9, seat.Hands[0].Total);
            Assert.Equal(900, seat.Balance);
        });

        versions = Enumerable.Range(1, 6).Select(id => game.Read((ulong)id).Version).ToArray();
        Parallel.For(0, 6, i =>
            errors[i] = game.Service.Execute(game.Room, User((ulong)i + 1), versions[i], BrowserBlackjackCommand.Stand));
        Assert.All(errors, error => Assert.Null(error));
        var settled = game.Read();
        Assert.True(settled.Complete);
        Assert.Equal(17, settled.DealerTotal);
        Assert.Equal(292, settled.RemainingCards);
        Assert.All(settled.Seats, seat =>
        {
            Assert.False(seat.Pending);
            Assert.Null(seat.Deadline);
            Assert.Equal(900, seat.Balance);
        });
        Assert.NotNull(game.Service.Execute(game.Room, User(1), versions[0], BrowserBlackjackCommand.Stand));
        Assert.Equal(900, game.Read().Balance);
    }

    [Fact]
    public void InsuranceBarrierAcceptsParallelAnswersAndStartsFreshTimersForEveryone()
    {
        using var game = new Game(2, 2, 2, 2, 2, 2, 1, 3, 3, 3, 3, 3, 3, 9).Start();

        for (ulong id = 1; id <= 6; id++)
        {
            game.Move(id, BrowserBlackjackCommand.Join);
        }

        game.Move(1, BrowserBlackjackCommand.Deal);
        long insuranceVersion = game.Read().Version;
        var errors = new string?[5];
        Parallel.For(0, 5, i =>
            errors[i] = game.Service.Execute(game.Room, User((ulong)i + 1), insuranceVersion, BrowserBlackjackCommand.DeclineInsurance));
        Assert.All(errors, error => Assert.Null(error));
        Assert.True(game.Read().Insurance);
        Assert.False(game.Read().YourTurn);
        Assert.True(game.Read(6).YourTurn);
        Assert.Equal(0, game.Read().Dealer[1].Rank);
        long waitingVersion = game.Read().Version;
        Assert.NotNull(game.Service.Execute(game.Room, User(1), waitingVersion, BrowserBlackjackCommand.Hit));
        Assert.NotNull(game.Service.Execute(game.Room, User(1), waitingVersion, BrowserBlackjackCommand.Insure));
        game.Clock.Now += TimeSpan.FromSeconds(20);
        game.Move(6, BrowserBlackjackCommand.Insure);
        Assert.False(game.Read().Insurance);
        Assert.NotNull(game.Service.Execute(game.Room, User(1), waitingVersion, BrowserBlackjackCommand.Hit));

        for (ulong id = 1; id <= 6; id++)
        {
            Assert.True(game.Read(id).YourTurn);
            Assert.Contains(BrowserBlackjackCommand.Hit, game.Read(id).Actions);
            Assert.Equal(game.Clock.Now.UtcDateTime.AddSeconds(30), game.Read(id).Deadline);
        }

        game.Clock.Now += TimeSpan.FromSeconds(10);
        game.Service.Sweep();
        Assert.False(game.Read().Complete);
        game.Clock.Now += TimeSpan.FromSeconds(20);
        game.Service.Sweep();
        Assert.True(game.Read().Complete);
        Assert.Equal(20, game.Read().DealerTotal);
        Assert.Equal(900, game.Read(1).Balance);
        Assert.Equal(850, game.Read(6).Balance);
        Assert.Equal(0, game.Read(6).Seats[5].InsuranceReturned);
    }

    [Fact]
    public void SimultaneousInsuranceTimeoutsDoNotAlsoTimeoutTheNewPlayingPhase()
    {
        using var game = new Game(2, 3, 1, 3, 4, 9).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        game.Clock.Now += TimeSpan.FromSeconds(30);
        game.Service.Sweep();
        Assert.False(game.Read().Insurance);
        Assert.False(game.Read().Complete);
        Assert.True(game.Read().YourTurn);
        Assert.True(game.Read(2).YourTurn);
        Assert.Equal(game.Clock.Now.UtcDateTime.AddSeconds(30), game.Read().Deadline);
        Assert.Equal(game.Read().Deadline, game.Read(2).Deadline);
        Assert.Contains("Seat 1 timed out", game.Read().Notice, StringComparison.Ordinal);
        Assert.Contains("Seat 2 timed out", game.Read().Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void NaturalsWaitForOtherPlayersWithoutAnActionOrATimer()
    {
        using var game = new Game(1, 10, 6, 13, 8, 10, 10).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        Assert.False(game.Read().YourTurn);
        Assert.True(game.Read(2).YourTurn);
        Assert.Null(game.Read().Seats[0].Deadline);
        Assert.Equal(0, game.Read().Dealer[1].Rank);
        Assert.Null(game.Service.Read(game.Room, User(1), true).Advice.Move);
        game.Move(2, BrowserBlackjackCommand.Stand);
        Assert.True(game.Read().Complete);
        Assert.Equal(1_150, game.Read().Balance);
        Assert.Equal(1_100, game.Read(2).Balance);
    }

    [Fact]
    public void StrategyAdviceIsForEachViewersOwnPendingHand()
    {
        using var game = new Game(2, 10, 6, 3, 6, 10).Start();
        game.Move(1, BrowserBlackjackCommand.Join);
        game.Move(2, BrowserBlackjackCommand.Join);
        game.Move(1, BrowserBlackjackCommand.Deal);
        var first = game.Service.Read(game.Room, User(1), true);
        var second = game.Service.Read(game.Room, User(2), true);
        Assert.True(first.YourTurn);
        Assert.True(second.YourTurn);
        Assert.Equal(BrowserBlackjackCommand.Hit, first.Advice.Move);
        Assert.Equal(BrowserBlackjackCommand.Stand, second.Advice.Move);
        Assert.Equal(first.Advice.RunningCount, second.Advice.RunningCount);
        Assert.Null(game.Service.Read(game.Room, User(3), true).Advice.Move);
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
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SelectedDeckCountControlsShoeAndDefaultIsFourDecks(int decks)
    {
        using var service = new BrowserBlackjackService(TimeProvider.System);
        string room = service.CreateRoom(User(1), decks).RoomId;
        Assert.Equal(decks, service.Read(room, User(1)).DeckCount);
        Assert.Null(service.Execute(room, User(1), 0, BrowserBlackjackCommand.Join));
        Assert.Null(service.Execute(room, User(1), 1, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, User(1));
        int dealt = state.Dealer.Count + state.Seats.SelectMany(p => p.Hands).Sum(h => h.Cards.Count);
        Assert.Equal(decks * 52, state.RemainingCards + dealt);
        Assert.Equal(decks == 2 ? 3 : 4, state.MaxHandsPerPlayer);
        var shoe = new BlackjackShoe();
        shoe.PrepareRound();
        Assert.Equal(4, shoe.DeckCount);
        Assert.Equal(208, shoe.Remaining);
        string defaultRoom = service.CreateRoom(User(2)).RoomId;
        Assert.Equal(4, service.Read(defaultRoom, User(2)).DeckCount);
    }

    [Theory]
    [InlineData(2)]
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
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void UnsupportedDeckCountsDoNotCreateRooms(int decks)
    {
        using var service = new BrowserBlackjackService(TimeProvider.System);
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

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void BettingAdviceIsPrivateOptInAndDoesNotPrepareTheShoe(int decks)
    {
        using var service = new BrowserBlackjackService(TimeProvider.System);
        string room = service.CreateRoom(User(1), decks).RoomId;
        Assert.Null(service.Read(room, User(1)).Advice);
        Assert.Null(service.Read(room, new ClaimsPrincipal(new ClaimsIdentity()), true).Advice.Bet);
        var state = service.Read(room, User(1), true);
        Assert.Equal(10m, state.Advice.Bet.Amount);
        Assert.True(state.Advice.Bet.ShuffleExpected);
        Assert.Equal(0, state.Advice.Bet.TrueCount);
        Assert.Equal(0, state.ShoeNumber);
        Assert.Equal(0, state.RemainingCards);
        Assert.Equal(1_000, state.Balance);
        Assert.Empty(state.Seats);

        Assert.Null(service.Execute(room, User(1), state.Version, BrowserBlackjackCommand.Join, 100));
        Assert.Null(service.Execute(room, User(1), service.Read(room, User(1)).Version, BrowserBlackjackCommand.Deal));
        var dealt = service.Read(room, User(1), true);
        Assert.True(dealt.Shuffled);

        if (dealt.Active)
        {
            Assert.Null(dealt.Advice.Bet);
        }
    }

    [Fact]
    public void BettingAdviceUsesExposedCountRefundableBetAndOtherTablesReservations()
    {
        using var game = new Game(Enumerable.Repeat(new[] { 2, 6, 2, 6, 5 }, 3).SelectMany(r => r).ToArray()).Start();

        for (int round = 0; round < 3; round++)
        {
            game.Move(1, BrowserBlackjackCommand.Join, 10);
            game.Move(1, BrowserBlackjackCommand.Deal);
            Assert.Null(game.Service.Read(game.Room, User(1), true).Advice.Bet);
            game.Move(1, BrowserBlackjackCommand.Stand);
        }

        var state = game.Service.Read(game.Room, User(1), true);
        Assert.Equal(970, state.Balance);
        Assert.Equal(15, state.Advice.RunningCount);
        Assert.Equal(15 / (297 / 52d), state.Advice.Bet.TrueCount);
        Assert.Equal(20m, state.Advice.Bet.Amount);
        Assert.False(state.Advice.Bet.ShuffleExpected);

        foreach (int bet in new[] { 500, 370 })
        {
            string other = game.Service.CreateRoom(User(1)).RoomId;
            Assert.Null(game.Service.Execute(other, User(1), 0, BrowserBlackjackCommand.Join, bet));
        }

        game.Move(1, BrowserBlackjackCommand.Join, 80);
        state = game.Service.Read(game.Room, User(1), true);
        Assert.Equal(20, state.Balance);
        Assert.Equal(20m, state.Advice.Bet.Amount);
        Assert.Equal(80, state.Seats[0].Hands[0].Bet);
        game.Move(1, BrowserBlackjackCommand.Join, 100);
        Assert.Equal(20m, game.Service.Read(game.Room, User(1), true).Advice.Bet.Amount);

        string reservedRoom = game.Service.GetOwnedRooms(User(1)).First(r => r != game.Room &&
            game.Service.Read(r, User(1)).Seats[0].Hands[0].Bet == 370);
        game.Move(1, BrowserBlackjackCommand.Join, 60);
        Assert.Null(game.Service.Execute(reservedRoom, User(1), game.Service.Read(reservedRoom, User(1)).Version,
            BrowserBlackjackCommand.Join, 410));
        state = game.Service.Read(game.Room, User(1), true);
        Assert.Equal(10m, state.Advice.Bet.Amount);
        game.Move(1, BrowserBlackjackCommand.Leave);
        Assert.Null(game.Service.Execute(reservedRoom, User(1), game.Service.Read(reservedRoom, User(1)).Version,
            BrowserBlackjackCommand.Join, 450));
        Assert.Null(game.Service.Read(game.Room, User(1), true).Advice.Bet.Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BettingAdvicePredictsCutCardAndSeatDependentSafetyShuffles(bool cutCard)
    {
        BlackjackTable table = Table(2, 6, 2, 6, 5);
        using var service = new BrowserBlackjackService(new Clock(), () => table);
        string room = service.CreateRoom(User(1)).RoomId;
        Assert.Null(service.Execute(room, User(1), 0, BrowserBlackjackCommand.Join, 10));
        Assert.Null(service.Execute(room, User(1), service.Read(room, User(1)).Version, BrowserBlackjackCommand.Deal));
        Assert.Null(service.Execute(room, User(1), service.Read(room, User(1)).Version, BrowserBlackjackCommand.Stand));

        while (table.Shoe.Remaining > (cutCard ? table.Shoe.CutCardRemaining : 104))
        {
            table.Shoe.Draw();
        }

        Assert.Equal(cutCard, service.Read(room, User(1), true).Advice.Bet.ShuffleExpected);

        for (ulong id = 1; id <= 5; id++)
        {
            Assert.Null(service.Execute(room, User(id), service.Read(room, User(id)).Version, BrowserBlackjackCommand.Join, 10));
        }

        Assert.Equal(cutCard, service.Read(room, User(1), true).Advice.Bet.ShuffleExpected);
        var joining = service.Read(room, User(6), true);
        Assert.True(joining.Advice.Bet.ShuffleExpected);
        Assert.Equal(0, joining.Advice.Bet.TrueCount);
        Assert.True(joining.Advice.TrueCount > 0);
        Assert.Equal(10m, joining.Advice.Bet.Amount);
        Assert.Null(service.Execute(room, User(6), joining.Version, BrowserBlackjackCommand.Join, 10));
        Assert.True(service.Read(room, User(6), true).Advice.Bet.ShuffleExpected);
        Assert.Null(service.Read(room, User(7), true).Advice.Bet);
        Assert.Null(service.Execute(room, User(1), service.Read(room, User(1)).Version, BrowserBlackjackCommand.Deal));
        Assert.True(service.Read(room, User(1)).Shuffled);
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

        while (table.Shoe.Remaining > table.Shoe.CutCardRemaining)
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
        Assert.NotNull(game.Service.Execute(game.Room, User(2), game.Read(2).Version, BrowserBlackjackCommand.Close));
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
        game.Move(1, BrowserBlackjackCommand.Join, 200);
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
