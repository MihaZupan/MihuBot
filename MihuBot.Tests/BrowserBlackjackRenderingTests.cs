using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MihuBot.Components.Blackjack;
using MihuBot.Configuration;
using MihuBot.Games.Blackjack;

namespace MihuBot.Tests;

public sealed class BrowserBlackjackRenderingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(-1, "-1")]
    [InlineData(33, "17")]
    [InlineData(-33, "-17")]
    [InlineData(1998, "999")]
    [InlineData(1999, "1k")]
    [InlineData(2000, "1k")]
    [InlineData(2400, "1.2k")]
    [InlineData(-2400, "-1.2k")]
    [InlineData(19900, "10k")]
    [InlineData(24000, "12k")]
    [InlineData(24690, "12k")]
    [InlineData(240000, "120k")]
    [InlineData(246900, "123k")]
    [InlineData(1998998, "999k")]
    [InlineData(1999000, "1M")]
    [InlineData(-1999000, "-1M")]
    [InlineData(2000000, "1M")]
    [InlineData(2400000, "1.2M")]
    [InlineData(-2400000, "-1.2M")]
    [InlineData(24000000, "12M")]
    public void HouseProfitUsesWholeChipsAndCompactUnits(long halfChips, string expected)
    {
        Assert.Equal(expected, MihuBot.Components.Pages.Blackjack.FormatHouseProfit(halfChips / 2m));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PageShowsGlobalHouseProfitToPlayersAndSpectators(bool signedIn, bool atTable)
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () => BrowserBlackjackTests.Table(1, 10, 13, 7));
        var player = BrowserBlackjackTests.User(1);
        string room = service.CreateRoom(player).RoomId;
        Assert.Null(service.Execute(room, player, 0, BrowserBlackjackCommand.Join, 11));
        Assert.Null(service.Execute(room, player, 1, BrowserBlackjackCommand.Deal));
        string otherRoom = service.CreateRoom(player).RoomId;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCascadingAuthenticationState();
        services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(signedIn));
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<AvailableFeatures>();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(service);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new PageRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = renderer.BeginRenderingComponent(typeof(MihuBot.Components.Pages.Blackjack),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["RoomId"] = atTable ? otherRoom : null
                }));
            await component.QuiescenceTask;
            return component.ToHtmlString();
        });

        Assert.Contains("title=\"Lifetime house profit across all tables\"", html, StringComparison.Ordinal);
        Assert.Contains("House profit:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Lifetime house profit:", html, StringComparison.Ordinal);
        Assert.Contains(">-17 chips</strong>", html, StringComparison.Ordinal);
    }

    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            componentActivator.CreateInstance(componentType);
    }

    private sealed class TestAuthenticationStateProvider(bool signedIn) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(signedIn ? BrowserBlackjackTests.User(1)
                : new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/blackjack");
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task BoardEncodesPlayerNamesAndHidesDealerCardAndTotal()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () => BrowserBlackjackTests.Table(10, 6, 8, 10, 5));
        var user = BrowserBlackjackTests.User(1, "<script>alert('name')</script>");
        string room = service.CreateRoom(user).RoomId;
        Assert.Null(service.Execute(room, user, 0, BrowserBlackjackCommand.Join, 100));
        Assert.Null(service.Execute(room, user, 1, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, user);
        string html = await Render<BlackjackBoard>(new()
        {
            ["State"] = state,
            ["ViewerId"] = 1ul,
            ["Now"] = state.Deadline.AddSeconds(-30)
        });

        Assert.Contains("Your move", html, StringComparison.Ordinal);
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
    public async Task BettingOddsShowApproximatePublishedRatesAndTheirLimitations()
    {
        var advice = BlackjackStrategy.RecommendBet(0, 1000, shuffleExpected: false);
        string html = await Render<BlackjackBetOdds>(new() { ["Advice"] = advice });
        Assert.Contains("Estimated odds", html, StringComparison.Ordinal);
        Assert.Contains("42.7%", html, StringComparison.Ordinal);
        Assert.Contains("8.1%", html, StringComparison.Ordinal);
        Assert.Contains("49.2%", html, StringComparison.Ordinal);
        Assert.Contains("-0.3%", html, StringComparison.Ordinal);
        Assert.Contains("Six-deck estimates; actual odds may differ.", html, StringComparison.Ordinal);
        Assert.Contains("not exact odds for our rules or strategy", html, StringComparison.Ordinal);
        Assert.Contains("truecount5.htm", html, StringComparison.Ordinal);
        Assert.Contains("truecount2.htm", html, StringComparison.Ordinal);
        Assert.Contains("Reference count: truncated toward zero", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sampling", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveHandAdviceDoesNotRenderBettingOdds()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () => BrowserBlackjackTests.Table(10, 6, 8, 10));
        var user = BrowserBlackjackTests.User(1);
        string room = service.CreateRoom(user).RoomId;
        Assert.Null(service.Execute(room, user, 0, BrowserBlackjackCommand.Join, 100));
        Assert.Null(service.Execute(room, user, 1, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, user, includeAdvice: true);
        Assert.True(state.YourTurn);
        Assert.NotNull(state.Advice.Move);
        Assert.Null(state.Advice.Bet);
        string html = await Render<BlackjackBetOdds>(new() { ["Advice"] = state.Advice.Bet });
        Assert.DoesNotContain("betting-odds", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ref TC", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyTableRendersSixSeatsAndNoDealerTotal()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System);
        var user = BrowserBlackjackTests.User(1);
        string room = service.CreateRoom(user).RoomId;
        string html = await Render<BlackjackBoard>(new() { ["State"] = service.Read(room, user), ["ViewerId"] = 1ul });
        Assert.Contains("A seat is waiting for you", html, StringComparison.Ordinal);

        for (int i = 1; i <= 6; i++)
        {
            Assert.Contains($"Empty seat {i}", html, StringComparison.Ordinal);
        }

        Assert.Contains("Fresh 4-deck shoe", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"total", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoardShowsAllSixPendingSeatsAndTheirOwnTimers()
    {
        using var service = new BrowserBlackjackService(TimeProvider.System, () =>
            BrowserBlackjackTests.Table(2, 2, 2, 2, 2, 2, 10, 3, 3, 3, 3, 3, 3, 7));
        var user = BrowserBlackjackTests.User(1);
        string room = service.CreateRoom(user).RoomId;

        for (ulong id = 1; id <= 6; id++)
        {
            var player = BrowserBlackjackTests.User(id);
            Assert.Null(service.Execute(room, player, service.Read(room, player).Version, BrowserBlackjackCommand.Join));
        }

        Assert.Null(service.Execute(room, user, service.Read(room, user).Version, BrowserBlackjackCommand.Deal));
        var state = service.Read(room, user);
        string html = await Render<BlackjackBoard>(new()
        {
            ["State"] = state,
            ["ViewerId"] = 1ul,
            ["Now"] = state.Deadline.AddSeconds(-30)
        });
        Assert.Contains("6 / 6", html, StringComparison.Ordinal);
        Assert.Contains("Your move", html, StringComparison.Ordinal);

        for (int i = 1; i <= 6; i++)
        {
            Assert.Contains($"Player {i}: 30 seconds remaining", html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Empty seat", html, StringComparison.Ordinal);
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
