using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Components;

namespace MihuBot.Tests;

public sealed class PageRenderModesTests
{
    [Theory]
    [InlineData(typeof(InteractivePage), true)]
    [InlineData(typeof(NonPrerenderedPage), false)]
    public void HeadOutletMatchesInteractivePageMode(Type pageType, bool prerender)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var mode = Assert.IsType<InteractiveServerRenderMode>(
            PageRenderModes.GetHeadRenderMode(CreateContext(pageType), services));
        Assert.Equal(prerender, mode.Prerender);
    }

    [Theory]
    [InlineData(typeof(StaticPage))]
    [InlineData(typeof(UnavailablePage))]
    public void HeadOutletStaysStaticForStaticOrUnavailablePages(Type pageType)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Null(PageRenderModes.GetHeadRenderMode(CreateContext(pageType), services));
    }

    [Fact]
    public void HeadOutletStaysStaticWithoutPageEndpoint()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Null(PageRenderModes.GetHeadRenderMode(new DefaultHttpContext(), services));
    }

    private static DefaultHttpContext CreateContext(Type pageType)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ComponentTypeMetadata(pageType)), "Test page"));
        return context;
    }

    [ServerRenderMode(prerender: true)]
    private sealed class InteractivePage : ComponentBase;

    [ServerRenderMode(prerender: false)]
    private sealed class NonPrerenderedPage : ComponentBase;

    private sealed class StaticPage : ComponentBase;

    [ServerRenderMode(prerender: true)]
    private sealed class UnavailablePage : ComponentBase
    {
        [Inject]
        private MissingService Service { get; set; } = null!;
    }

    private sealed class MissingService;

    private sealed class ServerRenderModeAttribute(bool prerender) : RenderModeAttribute
    {
        public override IComponentRenderMode Mode => new InteractiveServerRenderMode(prerender);
    }
}
