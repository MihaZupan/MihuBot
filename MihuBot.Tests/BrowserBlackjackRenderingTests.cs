using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Components.Blackjack;
using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BrowserBlackjackRenderingTests
{
    [Fact]
    public async Task BoardEncodesPlayerNamesAndHidesDealerCardAndTotal()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () => BrowserBlackjackTests.Table(10, 6, 8, 10, 5));
        var user = BrowserBlackjackTests.User(1, "<script>alert('name')</script>");
        string room = service.CreateRoom(user).RoomId;
        Assert.Null(service.Execute(room, user, 0, BrowserBlackjackCommand.Join, 100));
        Assert.Null(service.Execute(room, user, 1, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, user);
        string html = await Render<BlackjackBoard>(new() { ["State"] = state, ["ViewerId"] = 1ul });

        Assert.Contains("Your turn", html, StringComparison.Ordinal);
        Assert.Contains("Face-down card", html, StringComparison.Ordinal);
        Assert.Contains("6 of clubs", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Dealer / 16", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("current your-seat", html, StringComparison.Ordinal);

        Assert.Null(service.Execute(room, user, state.Version, BrowserBlackjackCommand.Stand));
        html = await Render<BlackjackBoard>(new() { ["State"] = service.Read(room, user), ["ViewerId"] = 1ul });
        Assert.Contains("Round complete", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Face-down card", html, StringComparison.Ordinal);
        Assert.Contains("5 of clubs", html, StringComparison.Ordinal);
        Assert.Contains("Loss", html, StringComparison.Ordinal);
        Assert.Contains("-100 chips", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, "Face-down card")]
    [InlineData(1, 3, "Ace of spades")]
    [InlineData(13, 2, "King of hearts")]
    public async Task CardsHaveAccessibleNamesAndHiddenCardsContainNoRank(int rank, int suit, string description)
    {
        string html = await Render<PlayingCard>(new() { ["Card"] = new BrowserBlackjackCard(rank, suit) });
        Assert.Contains($"aria-label=\"{description}\"", html, StringComparison.Ordinal);
        Assert.Equal(rank != 0, html.Contains("face-up", StringComparison.Ordinal));
        Assert.Equal(rank != 0, html.Contains("class=\"corner\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyTableRendersFourSeatsAndNoDealerTotal()
    {
        using var service = new BrowserBlackjackService();
        var user = BrowserBlackjackTests.User(1);
        string room = service.CreateRoom(user).RoomId;
        string html = await Render<BlackjackBoard>(new() { ["State"] = service.Read(room, user), ["ViewerId"] = 1ul });
        Assert.Contains("A seat is waiting for you", html, StringComparison.Ordinal);

        for (int i = 1; i <= 4; i++)
        {
            Assert.Contains($"Empty seat {i}", html, StringComparison.Ordinal);
        }

        Assert.Contains("Fresh 6-deck shoe", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"total", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoardCanRerenderAcrossBettingDealingAndNewRounds()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () => BrowserBlackjackTests.Table(10, 10, 8, 7));
        var user = BrowserBlackjackTests.User(1);
        string room = service.CreateRoom(user).RoomId;
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        BoardHarness? harness = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<BoardHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["InitialState"] = service.Read(room, user),
                ["Capture"] = (Action<BoardHarness>)(value => harness = value)
            }));

            foreach (var command in new[]
            {
                BrowserBlackjackCommand.Join, BrowserBlackjackCommand.Deal,
                BrowserBlackjackCommand.Stand, BrowserBlackjackCommand.Join
            })
            {
                Assert.Null(service.Execute(room, user, service.Read(room, user).Version, command));
                harness!.Update(service.Read(room, user));
            }
        });
    }

    private sealed class BoardHarness : ComponentBase
    {
        [Parameter]
        public BrowserBlackjackState InitialState { get; set; } = null!;

        [Parameter]
        public Action<BoardHarness> Capture { get; set; } = null!;

        private BrowserBlackjackState _state = null!;

        protected override void OnInitialized()
        {
            _state = InitialState;
            Capture(this);
        }

        public void Update(BrowserBlackjackState state)
        {
            _state = state;
            StateHasChanged();
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<BlackjackBoard>(0);
            builder.AddAttribute(1, "State", _state);
            builder.CloseComponent();
        }
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }
}
