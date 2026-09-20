using System.Security.Claims;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BlackjackPersistenceTests
{
    [Fact]
    public void HouseProfitAccumulatesAcrossRoundsAndBrowserAndDiscordTables()
    {
        using var files = new SavedScores();

        using (var service = files.Open(() => Table(1, 10, 13, 7, 10, 10, 6, 9)))
        {
            Assert.Equal(0m, service.HouseProfit);
            string browser = Create(service);
            Move(service, browser, 1, BrowserBlackjackCommand.Join, 11);
            Move(service, browser, 1, BrowserBlackjackCommand.Deal);
            Assert.Equal(-16.5m, service.HouseProfit);
            Move(service, browser, 1, BrowserBlackjackCommand.Join, 100);
            Move(service, browser, 1, BrowserBlackjackCommand.Deal);
            Assert.Equal(-16.5m, service.HouseProfit);
            Move(service, browser, 1, BrowserBlackjackCommand.Stand);
            Assert.Equal(83.5m, service.HouseProfit);

            var (discord, error) = service.GetOrCreateDiscordLobby(42, 2);
            Assert.Null(error);
            Move(service, discord, 2, BrowserBlackjackCommand.Join, 11);
            Move(service, discord, 2, BrowserBlackjackCommand.Deal);
            Assert.Equal(67m, service.HouseProfit);
            Move(service, browser, 1, BrowserBlackjackCommand.Close);
            Move(service, discord, 2, BrowserBlackjackCommand.Close);
            service.Sweep();
            Assert.Equal(67m, service.HouseProfit);
        }

        using var restarted = files.Open();
        Assert.Equal(67m, restarted.HouseProfit);
    }

    [Theory]
    [InlineData(new[] { 10, 10, 8, 8 }, BrowserBlackjackCommand.Stand, 0)]
    [InlineData(new[] { 10, 10, 6, 9 }, BrowserBlackjackCommand.Surrender, 5.5)]
    [InlineData(new[] { 5, 10, 6, 8, 10 }, BrowserBlackjackCommand.Double, -22)]
    [InlineData(new[] { 9, 1, 8, 13 }, BrowserBlackjackCommand.Insure, 0)]
    [InlineData(new[] { 1, 1, 13, 10 }, BrowserBlackjackCommand.Insure, -11)]
    public void HouseProfitIncludesAllSettledPayouts(int[] cards, BrowserBlackjackCommand action, double expected)
    {
        using var files = new SavedScores();

        using (var service = files.Open(() => Table(cards)))
        {
            string room = Create(service);
            Move(service, room, 1, BrowserBlackjackCommand.Join, 11);
            Move(service, room, 1, BrowserBlackjackCommand.Deal);
            Assert.Equal(0m, service.HouseProfit);
            Move(service, room, 1, action);
            Assert.True(service.Read(room, User(1)).Complete);
            Assert.Equal((decimal)expected, service.HouseProfit);
        }

        using var restarted = files.Open();
        Assert.Equal((decimal)expected, restarted.HouseProfit);
    }

    [Fact]
    public void SplitDoubleAndLosingInsuranceCountOnlyWhenTheRoundSettles()
    {
        using var files = new SavedScores();
        var tables = new Queue<BlackjackTable>([
            Table(8, 10, 8, 7, 3, 10, 10),
            Table(10, 1, 13, 8)]);

        using (var service = files.Open(tables.Dequeue))
        {
            string split = Create(service);
            Move(service, split, 1, BrowserBlackjackCommand.Join);
            Move(service, split, 1, BrowserBlackjackCommand.Deal);
            Move(service, split, 1, BrowserBlackjackCommand.Split);
            Move(service, split, 1, BrowserBlackjackCommand.Double);
            Assert.Equal(0m, service.HouseProfit);
            Move(service, split, 1, BrowserBlackjackCommand.Stand);
            Assert.Equal(-300m, service.HouseProfit);

            string insurance = Create(service, 2);
            Move(service, insurance, 2, BrowserBlackjackCommand.Join);
            Move(service, insurance, 2, BrowserBlackjackCommand.Deal);
            Move(service, insurance, 2, BrowserBlackjackCommand.Insure);
            Assert.Equal(-300m, service.HouseProfit);
            Move(service, insurance, 2, BrowserBlackjackCommand.Stand);
            Assert.Equal(-350m, service.HouseProfit);
        }

        using var restarted = files.Open();
        Assert.Equal(-350m, restarted.HouseProfit);
    }

    [Theory]
    [InlineData(long.MaxValue, -1)]
    [InlineData(long.MinValue, 1)]
    public void HouseProfitOverflowCannotPartiallyChangeBalances(long profit, long change)
    {
        using var files = new SavedScores();
        string json = $$"""{"0":{{profit}},"1":2000}""";
        File.WriteAllText(files.Path, json);
        var store = new BlackjackBalanceStore(files.Path);
        Assert.Throws<OverflowException>(() => store.ApplyResults(new Dictionary<ulong, long> { [1] = change }));
        Assert.Equal(1_000m, store.GetBalance(1));
        Assert.Equal(profit / 2m, store.HouseProfit);
        Assert.Equal(json, File.ReadAllText(files.Path));
    }

    [Fact]
    public void SettledScoresAndHalfChipsSurviveRestartByFullDiscordId()
    {
        using var files = new SavedScores();
        const ulong userId = ulong.MaxValue - 42;
        string oldRoom;

        using (var service = files.Open(() => Table(1, 10, 13, 7)))
        {
            oldRoom = Create(service, userId);
            Move(service, oldRoom, userId, BrowserBlackjackCommand.Join, 11);
            Move(service, oldRoom, userId, BrowserBlackjackCommand.Deal);
            Assert.True(service.Read(oldRoom, User(userId)).Complete);
            Assert.Equal(1_016.5m, service.GetBalance(User(userId)));
            Assert.Equal(16.5m, service.Read(oldRoom, User(userId)).Seats[0].Change);
            Assert.Equal(-16.5m, service.HouseProfit);
        }

        File.WriteAllText(files.Path + ".tmp", "interrupted temporary write");

        using var restarted = files.Open();
        Assert.Equal(1_016.5m, restarted.GetBalance(User(userId)));
        Assert.Equal(-16.5m, restarted.HouseProfit);
        Assert.Equal(1_000m, restarted.GetBalance(User(2)));
        Assert.Null(restarted.GetBalance(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(restarted.Read(oldRoom, User(userId)));
        string next = Create(restarted, userId);
        Assert.Equal(1_016.5m, restarted.Read(next, User(userId)).Balance);
        Assert.Contains(userId.ToString(), File.ReadAllText(files.Path), StringComparison.Ordinal);
        var saved = JObject.Parse(File.ReadAllText(files.Path));
        Assert.Equal(2, saved.Properties().Count());
        Assert.Equal(-33, (long)saved["0"]!);
        JToken balance = saved[userId.ToString()]!;
        Assert.Equal(JTokenType.Integer, balance.Type);
        Assert.Equal(2_033, (long)balance);
    }

    [Fact]
    public void ASharedRoundSavesEveryPlayersScoreInOneSnapshot()
    {
        using var files = new SavedScores();
        int writes = 0;

        using (var service = files.Open(
            () => Table(2, 2, 2, 2, 2, 2, 10, 3, 3, 3, 3, 3, 3, 7),
            (source, destination) =>
            {
                writes++;
                File.Move(source, destination, overwrite: true);
            }))
        {
            string room = Create(service);

            for (ulong id = 1; id <= 6; id++)
            {
                Move(service, room, id, BrowserBlackjackCommand.Join);
            }

            Move(service, room, 1, BrowserBlackjackCommand.Deal);
            Assert.Equal(0, writes);

            for (ulong id = 1; id <= 6; id++)
            {
                Move(service, room, id, BrowserBlackjackCommand.Stand);
            }

            Assert.Equal(1, writes);
        }

        using var restarted = files.Open();
        Assert.Equal(600m, restarted.HouseProfit);

        for (ulong id = 1; id <= 6; id++)
        {
            Assert.Equal(900m, restarted.GetBalance(User(id)));
        }
    }

    [Fact]
    public void RestartReturnsAllUnfinishedSplitAndDoubleWagers()
    {
        using var files = new SavedScores();
        files.Seed((1, 250));
        string saved = File.ReadAllText(files.Path);

        using (var service = files.Open(() => Table(8, 10, 8, 7, 3, 10, 2, 10)))
        {
            string room = Create(service);
            Move(service, room, 1, BrowserBlackjackCommand.Join);
            Move(service, room, 1, BrowserBlackjackCommand.Deal);
            Move(service, room, 1, BrowserBlackjackCommand.Split);
            Move(service, room, 1, BrowserBlackjackCommand.Double);
            Assert.False(service.Read(room, User(1)).Complete);
            Assert.Equal(950m, service.GetBalance(User(1)));
            Assert.Equal(saved, File.ReadAllText(files.Path));
        }

        using var restarted = files.Open();
        Assert.Equal(1_250m, restarted.GetBalance(User(1)));
        Assert.Equal(-250m, restarted.HouseProfit);
    }

    [Fact]
    public void RestartReturnsLobbyAndUnfinishedInsuranceWagers()
    {
        using var files = new SavedScores();

        using (var service = files.Open(() => Table(10, 9, 1, 8, 7, 10)))
        {
            string room = Create(service);
            Move(service, room, 1, BrowserBlackjackCommand.Join);
            Move(service, room, 2, BrowserBlackjackCommand.Join);
            Move(service, room, 1, BrowserBlackjackCommand.Deal);
            Move(service, room, 1, BrowserBlackjackCommand.Insure);
            string lobby = Create(service, 3);
            Move(service, lobby, 3, BrowserBlackjackCommand.Join, 250);
            Assert.Equal(850m, service.GetBalance(User(1)));
            Assert.Equal(900m, service.GetBalance(User(2)));
            Assert.Equal(750m, service.GetBalance(User(3)));
        }

        using var restarted = files.Open();
        Assert.Equal(1_000m, restarted.GetBalance(User(1)));
        Assert.Equal(1_000m, restarted.GetBalance(User(2)));
        Assert.Equal(1_000m, restarted.GetBalance(User(3)));
        Assert.Equal(0m, restarted.HouseProfit);
    }

    [Fact]
    public void ParallelDoublesAtDifferentTablesCannotSpendTheSameChips()
    {
        using var files = new SavedScores();

        using (var service = files.Open(() => Table(10, 10, 5, 9, 10)))
        {
            string[] rooms = [Create(service), Create(service)];

            foreach (string room in rooms)
            {
                Move(service, room, 1, BrowserBlackjackCommand.Join, 300);
                Move(service, room, 1, BrowserBlackjackCommand.Deal);
            }

            Assert.Equal(400m, service.GetBalance(User(1)));
            var snapshots = rooms.Select(room => service.Read(room, User(1))).ToArray();
            Assert.All(snapshots, state => Assert.Contains(BrowserBlackjackCommand.Double, state.Actions));
            var errors = new string?[2];
            Parallel.For(0, 2, i =>
                errors[i] = service.Execute(rooms[i], User(1), snapshots[i].Version, BrowserBlackjackCommand.Double));
            Assert.Single(errors, error => error is null);
            Assert.Single(errors, error => error is not null);
            Assert.Equal(100m, service.GetBalance(User(1)));
            string unfinished = Assert.Single(rooms, room => !service.Read(room, User(1)).Complete);
            Assert.DoesNotContain(BrowserBlackjackCommand.Double, service.Read(unfinished, User(1)).Actions);
            Move(service, unfinished, 1, BrowserBlackjackCommand.Stand);
            Assert.Equal(100m, service.GetBalance(User(1)));
        }

        using var restarted = files.Open();
        Assert.Equal(100m, restarted.GetBalance(User(1)));
        Assert.Equal(900m, restarted.HouseProfit);
    }

    [Fact]
    public void LobbyChangesAndRefundsUseTheLatestSharedBalance()
    {
        using var files = new SavedScores();
        using var service = files.Open(() => Table(10, 10, 8, 7));
        string first = Create(service);
        string second = Create(service);
        Move(service, first, 1, BrowserBlackjackCommand.Join);
        Move(service, first, 1, BrowserBlackjackCommand.Deal);
        Move(service, second, 1, BrowserBlackjackCommand.Join, 400);
        Assert.Equal(500m, service.GetBalance(User(1)));
        Move(service, first, 1, BrowserBlackjackCommand.Stand);
        Assert.Equal(700m, service.GetBalance(User(1)));
        Assert.Equal(100, service.Read(first, User(1)).Seats[0].Change);
        Move(service, second, 1, BrowserBlackjackCommand.Join, 500);
        Assert.Equal(600m, service.GetBalance(User(1)));
        Assert.Equal(600, service.Read(first, User(1)).Seats[0].Balance);
        Move(service, second, 1, BrowserBlackjackCommand.Leave);
        Assert.Equal(1_100m, service.GetBalance(User(1)));
    }

    [Fact]
    public void GlobalReservationsPreventRebuyingAtAnotherTable()
    {
        using var files = new SavedScores();
        using var service = files.Open();
        string first = Create(service);
        string second = Create(service);
        string third = Create(service);
        Move(service, first, 1, BrowserBlackjackCommand.Join, 500);
        Move(service, second, 1, BrowserBlackjackCommand.Join, 500);
        Assert.Equal(0m, service.GetBalance(User(1)));
        var state = service.Read(third, User(1));
        Assert.DoesNotContain(BrowserBlackjackCommand.Rebuy, state.Actions);
        Assert.Contains("across all tables", service.Execute(third, User(1), state.Version, BrowserBlackjackCommand.Rebuy), StringComparison.Ordinal);
        Assert.NotNull(service.Execute(third, User(1), state.Version, BrowserBlackjackCommand.Join));
        Assert.Equal(0m, service.GetBalance(User(1)));
        Move(service, first, 1, BrowserBlackjackCommand.Leave);
        Assert.Equal(500m, service.GetBalance(User(1)));
        Move(service, second, 1, BrowserBlackjackCommand.Leave);
        Assert.Equal(1_000m, service.GetBalance(User(1)));
    }

    [Fact]
    public void ClosingALiveTableSettlesLossesButClosingALobbyRefundsBets()
    {
        using var files = new SavedScores();

        using (var service = files.Open(() => Table(10, 10, 6, 9)))
        {
            string room = Create(service);
            Move(service, room, 1, BrowserBlackjackCommand.Join);
            Move(service, room, 1, BrowserBlackjackCommand.Deal);
            Move(service, room, 1, BrowserBlackjackCommand.Close);
            Assert.Null(service.Read(room, User(1)));
            Assert.Equal(900m, service.GetBalance(User(1)));
            string lobby = Create(service);
            Move(service, lobby, 1, BrowserBlackjackCommand.Join, 500);
            Move(service, lobby, 1, BrowserBlackjackCommand.Close);
            Assert.Equal(900m, service.GetBalance(User(1)));
        }

        using var restarted = files.Open();
        Assert.Equal(900m, restarted.GetBalance(User(1)));
        Assert.Equal(100m, restarted.HouseProfit);
    }

    [Fact]
    public void TimeoutResultsAndRebuysAreSavedAndIdleExpiryDoesNotEraseScores()
    {
        using var files = new SavedScores();

        using (var service = files.Open(() => Table(10, 10, 6, 9, 10, 10, 6, 9)))
        {
            string room = Create(service);

            for (int i = 0; i < 2; i++)
            {
                Move(service, room, 1, BrowserBlackjackCommand.Join, 500);
                Move(service, room, 1, BrowserBlackjackCommand.Deal);
                files.Clock.Now += TimeSpan.FromSeconds(30);
                service.Sweep();
            }

            Assert.Equal(0m, service.GetBalance(User(1)));
        }

        using (var restarted = files.Open())
        {
            Assert.Equal(0m, restarted.GetBalance(User(1)));
            string room = Create(restarted);
            Move(restarted, room, 1, BrowserBlackjackCommand.Rebuy);
            files.Clock.Now += BrowserBlackjackService.IdleLifetime;
            restarted.Sweep();
            Assert.Null(restarted.Read(room, User(1)));
            Assert.Equal(1_000m, restarted.GetBalance(User(1)));
        }

        using var again = files.Open();
        Assert.Equal(1_000m, again.GetBalance(User(1)));
        Assert.Equal(1_000m, again.HouseProfit);
    }

    [Fact]
    public void FailedSettlementPropagatesWithoutRetryingOrApplyingResultsAgain()
    {
        using var files = new SavedScores();
        int writes = 0;
        using var service = files.Open(() => Table(10, 10, 8, 7), (_, _) =>
        {
            writes++;
            throw new IOException("Simulated unavailable storage");
        });
        string room = Create(service);
        Move(service, room, 1, BrowserBlackjackCommand.Join);
        Move(service, room, 1, BrowserBlackjackCommand.Deal);
        Assert.Throws<IOException>(() => service.Execute(room, User(1), service.Read(room, User(1)).Version, BrowserBlackjackCommand.Stand));
        var state = service.Read(room, User(1));
        Assert.True(state.Complete);
        Assert.Null(state.Notice);
        Assert.Contains(BrowserBlackjackCommand.Join, state.Actions);
        Assert.Equal(1_100, state.Balance);
        Assert.False(File.Exists(files.Path));

        for (int i = 0; i < 3; i++)
        {
            files.Clock.Now += TimeSpan.FromSeconds(5);
            service.Sweep();
            Assert.Equal(1_100, service.Read(room, User(1)).Balance);
            Assert.Equal(-100m, service.HouseProfit);
        }

        Assert.Equal(1, writes);
    }

    [Fact]
    public void FailedClosePropagatesWithoutRetrying()
    {
        using var files = new SavedScores();
        int writes = 0;
        using var service = files.Open(() => Table(10, 10, 6, 9), (_, _) =>
        {
            writes++;
            throw new IOException("Simulated failure");
        });
        string room = Create(service);
        Move(service, room, 1, BrowserBlackjackCommand.Join);
        Move(service, room, 1, BrowserBlackjackCommand.Deal);
        Assert.Throws<IOException>(() => service.Execute(room, User(1), service.Read(room, User(1)).Version, BrowserBlackjackCommand.Close));
        Assert.Equal(900m, service.GetBalance(User(1)));
        files.Clock.Now += TimeSpan.FromSeconds(5);
        service.Sweep();
        Move(service, room, 1, BrowserBlackjackCommand.Close);
        Assert.Null(service.Read(room, User(1)));
        Assert.Equal(900m, service.GetBalance(User(1)));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void FailedRebuyKeepsBalanceInMemoryUntilTheNextSuccessfulSave()
    {
        using var files = new SavedScores();
        files.Seed((1, -1_000));
        string before = File.ReadAllText(files.Path);
        bool fail = true;
        using (var service = files.Open(() => Table(10, 10, 8, 7), replaceFile: (source, destination) =>
        {
            if (fail)
            {
                throw new IOException("Simulated failure");
            }

            File.Move(source, destination, overwrite: true);
        }))
        {
            string room = Create(service);
            var state = service.Read(room, User(1));
            Assert.Throws<IOException>(() => service.Execute(room, User(1), state.Version, BrowserBlackjackCommand.Rebuy));
            Assert.Equal(1_000m, service.GetBalance(User(1)));
            Assert.DoesNotContain(BrowserBlackjackCommand.Rebuy, service.Read(room, User(1)).Actions);
            Assert.Equal(before, File.ReadAllText(files.Path));
            fail = false;
            Move(service, room, 1, BrowserBlackjackCommand.Join);
            Move(service, room, 1, BrowserBlackjackCommand.Deal);
            Move(service, room, 1, BrowserBlackjackCommand.Stand);
        }

        using var restarted = files.Open();
        Assert.Equal(1_100m, restarted.GetBalance(User(1)));
    }

    [Fact]
    public void InvalidResultBatchesCannotPartiallyChangeBalances()
    {
        using var files = new SavedScores();
        files.Seed((1, 250), (2, 100));
        string before = File.ReadAllText(files.Path);
        var store = new BlackjackBalanceStore(files.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ApplyResults(new Dictionary<ulong, long> { [1] = 100, [2] = -2_201 }));
        Assert.Throws<OverflowException>(() =>
            store.ApplyResults(new Dictionary<ulong, long> { [1] = 100, [2] = long.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Rebuy(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetBalance(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ApplyResults(new Dictionary<ulong, long> { [1] = 100, [0] = 100 }));
        Assert.Equal(1_250, store.GetBalance(1));
        Assert.Equal(1_100, store.GetBalance(2));
        Assert.Equal(-350m, store.HouseProfit);
        Assert.Equal(before, File.ReadAllText(files.Path));
    }

    [Fact]
    public void FailedBatchPropagatesAfterApplyingAllChangesInMemory()
    {
        using var files = new SavedScores();
        files.Seed((1, 250.5m));
        string before = File.ReadAllText(files.Path);
        var store = new BlackjackBalanceStore(files.Path, (_, _) => throw new IOException("Simulated failure"));
        var changes = new Dictionary<ulong, long> { [1] = 1, [2] = -1 };
        Assert.Throws<IOException>(() => store.ApplyResults(changes));
        Assert.Equal(1_251m, store.GetBalance(1));
        Assert.Equal(999.5m, store.GetBalance(2));
        Assert.Equal(-250.5m, store.HouseProfit);
        Assert.Equal(before, File.ReadAllText(files.Path));
    }

    [Fact]
    public void SavedBalancesAreADictionaryOfHalfChipCounts()
    {
        using var files = new SavedScores();
        File.WriteAllText(files.Path, """{"1":2469}""");
        var store = new BlackjackBalanceStore(files.Path);
        Assert.Equal(1_234.5m, store.GetBalance(1));
        Assert.Equal(0m, store.HouseProfit);
        store.ApplyResults(new Dictionary<ulong, long> { [1] = 1 });
        var saved = JObject.Parse(File.ReadAllText(files.Path));
        Assert.Equal(2, saved.Properties().Count());
        Assert.Equal(-1, (long)saved["0"]!);
        Assert.Equal(JTokenType.Integer, saved["1"]!.Type);
        Assert.Equal(2_470, (long)saved["1"]!);
        Assert.Equal(1_235m, new BlackjackBalanceStore(files.Path).GetBalance(1));
        Assert.Equal(-0.5m, new BlackjackBalanceStore(files.Path).HouseProfit);
    }

    [Fact]
    public void EmptySavedDictionaryUsesStartingBalance()
    {
        using var files = new SavedScores();
        File.WriteAllText(files.Path, "{}");
        var store = new BlackjackBalanceStore(files.Path);
        Assert.Equal(1_000m, store.GetBalance(1));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"1":-1}""")]
    public void InvalidSavedDataFailsClosedAndCanBeCorrected(string data)
    {
        using var files = new SavedScores();
        File.WriteAllText(files.Path, data);
        Assert.Throws<InvalidDataException>(() => new BlackjackBalanceStore(files.Path));
        Assert.Equal(data, File.ReadAllText(files.Path));
        File.WriteAllText(files.Path, """{"1":2469}""");
        var recovered = new BlackjackBalanceStore(files.Path);
        Assert.Equal(1_234.5m, recovered.GetBalance(1));
    }

    [Theory]
    [InlineData("""{"1":""")]
    [InlineData("""{"1":null}""")]
    [InlineData("""{"1":9223372036854775808}""")]
    [InlineData("""{"0":null}""")]
    [InlineData("""{"0":9223372036854775808}""")]
    [InlineData("""{"Version":2,"Balances":{"1":2469}}""")]
    public void InvalidJsonIsNotSilentlyReset(string json)
    {
        using var files = new SavedScores();
        File.WriteAllText(files.Path, json);
        Assert.ThrowsAny<JsonException>(() => new BlackjackBalanceStore(files.Path));
        Assert.Equal(json, File.ReadAllText(files.Path));
        File.WriteAllText(files.Path, """{"1":2200}""");
        var store = new BlackjackBalanceStore(files.Path);
        Assert.Equal(1_100, store.GetBalance(1));
    }

    [Fact]
    public void SavingBalancesDoesNotCreateALockFile()
    {
        using var files = new SavedScores();
        var store = new BlackjackBalanceStore(files.Path);
        store.ApplyResults(new Dictionary<ulong, long> { [1] = 200 });
        Assert.False(File.Exists(files.Path + ".lock"));
        Assert.Equal(1_100, new BlackjackBalanceStore(files.Path).GetBalance(1));
    }

    private static ClaimsPrincipal User(ulong id) => BrowserBlackjackTests.User(id);
    private static BlackjackTable Table(params int[] ranks) => BrowserBlackjackTests.Table(ranks);

    private static string Create(BrowserBlackjackService service, ulong owner = 1)
    {
        (string room, string? error) = service.CreateRoom(User(owner));
        Assert.Null(error);
        return room;
    }

    private static void Move(BrowserBlackjackService service, string room, ulong user, BrowserBlackjackCommand command, decimal bet = 100) =>
        Assert.Null(service.Execute(room, User(user), service.Read(room, User(user)).Version, command, bet));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SavedScores : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("MihuBot-Blackjack-");
        public string Path => System.IO.Path.Combine(_directory.FullName, "balances.json");
        public Clock Clock { get; } = new();

        public BrowserBlackjackService Open(Func<BlackjackTable>? table = null, Action<string, string>? replaceFile = null) =>
            new(Clock, table ?? (() => new BlackjackTable()), new BlackjackBalanceStore(Path, replaceFile));

        public void Seed(params (ulong Id, decimal Change)[] changes)
        {
            var store = new BlackjackBalanceStore(Path);
            store.ApplyResults(changes.ToDictionary(p => p.Id, p => checked((long)(p.Change * 2))));
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
