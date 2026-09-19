using MihuBot.Discord.Commands;
using MihuBot.Games.Blackjack;
using MihuBot.Helpers;

namespace MihuBot.Tests;

public sealed class BlackjackCommandTests
{
    [Fact]
    public void CommandPostsABrowserLobbyWithoutTakingASeatOrWager()
    {
        using var service = new BrowserBlackjackService();
        var command = new BlackjackCommand(service);
        Assert.Equal("blackjack", command.Command);
        Assert.Contains("bj", command.Aliases);
        string message = command.GetLobbyMessage(100, 1);
        string room = RoomId(message);
        var state = service.Read(room, BrowserBlackjackTests.User(1));
        Assert.NotNull(state);
        Assert.Empty(state.Seats);
        Assert.False(state.Active);
        Assert.Equal(1_000, state.Balance);
        Assert.Equal(6, state.DeckCount);
        Assert.Equal(1ul, state.HostId);
        Assert.Equal(room, Assert.Single(service.GetOwnedRooms(BrowserBlackjackTests.User(1))));
        Assert.Contains("Sign in with Discord", message, StringComparison.Ordinal);
        Assert.InRange(message.Length, 1, 2_000);
    }

    [Fact]
    public void ChannelMembersGetTheSameLobbyAndDifferentChannelsGetDifferentLobbies()
    {
        using var service = new BrowserBlackjackService();
        var command = new BlackjackCommand(service);
        string first = command.GetLobbyMessage(100, 1);
        Assert.Equal(first, command.GetLobbyMessage(100, 2));
        Assert.Empty(service.GetOwnedRooms(BrowserBlackjackTests.User(2)));
        string other = command.GetLobbyMessage(200, 2);
        Assert.NotEqual(RoomId(first), RoomId(other));
        Assert.Equal(2ul, service.Read(RoomId(other), BrowserBlackjackTests.User(2)).HostId);
    }

    [Fact]
    public void ConcurrentCommandsCreateOnlyOneLobby()
    {
        using var service = new BrowserBlackjackService();
        var command = new BlackjackCommand(service);
        string[] messages = new string[16];
        Parallel.For(0, messages.Length, i => messages[i] = command.GetLobbyMessage(100, (ulong)i + 1));
        Assert.All(messages, message => Assert.Equal(messages[0], message));
        Assert.Equal(1, Enumerable.Range(1, 16).Sum(id => service.GetOwnedRooms(BrowserBlackjackTests.User((ulong)id)).Count));
    }

    [Fact]
    public void RepostingAnActiveLobbyDoesNotChangeTheRoundBalanceOrDeadline()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () => BrowserBlackjackTests.Table(2, 10, 3, 13));
        var command = new BlackjackCommand(service);
        string message = command.GetLobbyMessage(100, 1);
        string room = RoomId(message);
        var user = BrowserBlackjackTests.User(1);
        Assert.Null(service.Execute(room, user, 0, BrowserBlackjackCommand.Join, 100));
        Assert.Null(service.Execute(room, user, 1, BrowserBlackjackCommand.Deal));
        var before = service.Read(room, user);

        Assert.Equal(message, command.GetLobbyMessage(100, 2));
        var after = service.Read(room, user);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.RoundId, after.RoundId);
        Assert.Equal(before.Balance, after.Balance);
        Assert.Equal(before.Deadline, after.Deadline);
        Assert.Equal(before.RemainingCards, after.RemainingCards);
        Assert.Equal(before.ActivePlayerId, after.ActivePlayerId);
    }

    [Fact]
    public void ClosedLobbyIsReplacedAndOldLinksStayClosed()
    {
        using var service = new BrowserBlackjackService();
        var command = new BlackjackCommand(service);
        string first = RoomId(command.GetLobbyMessage(100, 1));
        Assert.Null(service.Execute(first, BrowserBlackjackTests.User(1), 0, BrowserBlackjackCommand.Close));
        string next = RoomId(command.GetLobbyMessage(100, 2));
        Assert.NotEqual(first, next);
        Assert.Null(service.Read(first, BrowserBlackjackTests.User(1)));
        Assert.Equal(2ul, service.Read(next, BrowserBlackjackTests.User(2)).HostId);
        Assert.Empty(service.GetOwnedRooms(BrowserBlackjackTests.User(1)));
    }

    [Fact]
    public void ExpiredLobbyIsReplacedAndPostingALinkRefreshesIdleLifetime()
    {
        var clock = new Clock();
        using var service = new BrowserBlackjackService(clock, () => new BlackjackTable());
        var command = new BlackjackCommand(service);
        string first = command.GetLobbyMessage(100, 1);
        clock.Now += BrowserBlackjackService.IdleLifetime - TimeSpan.FromSeconds(1);
        Assert.Equal(first, command.GetLobbyMessage(100, 2));
        clock.Now += TimeSpan.FromSeconds(2);
        service.Sweep();
        Assert.Equal(first, command.GetLobbyMessage(100, 2));
        clock.Now += BrowserBlackjackService.IdleLifetime;
        string next = command.GetLobbyMessage(100, 2);
        Assert.NotEqual(RoomId(first), RoomId(next));
        Assert.Null(service.Read(RoomId(first), BrowserBlackjackTests.User(1)));
    }

    [Fact]
    public void CreationHonorsRoomQuotasButExistingLobbyLinksRemainAvailable()
    {
        using var service = new BrowserBlackjackService();
        var command = new BlackjackCommand(service);
        string existing = command.GetLobbyMessage(100, 1);
        var user = BrowserBlackjackTests.User(2);

        for (int i = 0; i < BrowserBlackjackService.MaxRoomsPerOwner; i++)
        {
            Assert.Null(service.CreateRoom(user).Error);
        }

        Assert.Equal(existing, command.GetLobbyMessage(100, 2));
        string error = command.GetLobbyMessage(200, 2);
        Assert.Contains("table limit", error, StringComparison.Ordinal);
        Assert.DoesNotContain("/blackjack/", error, StringComparison.Ordinal);
        Assert.Equal(BrowserBlackjackService.MaxRoomsPerOwner, service.GetOwnedRooms(user).Count);
    }

    [Theory]
    [InlineData(0ul, 1ul)]
    [InlineData(100ul, 0ul)]
    public void InvalidDiscordIdentifiersCannotCreateALobby(ulong channel, ulong author)
    {
        using var service = new BrowserBlackjackService();
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetOrCreateDiscordLobby(channel, author));
    }

    private static string RoomId(string message)
    {
        string link = message.Split('\n')[1];
        Assert.StartsWith($"{Constants.PublicBaseUrl}/blackjack/", link, StringComparison.Ordinal);
        string room = new Uri(link).Segments[^1];
        Assert.True(Guid.TryParseExact(room, "N", out _));
        return room;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
