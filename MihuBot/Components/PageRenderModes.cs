using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using MihuBot.Configuration;

namespace MihuBot.Components;

public static class PageRenderModes
{
    public static IComponentRenderMode GetHeadRenderMode(HttpContext context, IServiceProvider services)
    {
        Type pageType = context?.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type;
        if (pageType is null || OptionalDependencies.GetMissingInjectedService(services, pageType) is not null)
        {
            return null;
        }

        // PageTitle and HeadOutlet must stay on the same side of the page's render-mode boundary.
        return pageType.GetCustomAttribute<RenderModeAttribute>()?.Mode;
    }
}
