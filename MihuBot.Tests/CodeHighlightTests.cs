using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MihuBot.Components;

namespace MihuBot.Tests;

public sealed class CodeHighlightTests
{
    [Fact]
    public async Task PrerenderIncludesThemeLayoutClassAndEncodedSourceWithoutRunningJavaScript()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, NoJavaScriptDuringPrerender>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<CodeHighlight>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CodeHighlight.Code)] = "var html = \"<script>literal</script>\";",
                [nameof(CodeHighlight.Language)] = "language-csharp",
                [nameof(CodeHighlight.CodeBlockId)] = "source",
            }));
            return rendered.ToHtmlString();
        });

        Assert.Contains("href=\"vs2015.css\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"source\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"hljs language-csharp\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;literal&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Syntax highlighting is unavailable", html, StringComparison.Ordinal);
    }

    private sealed class NoJavaScriptDuringPrerender : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("JavaScript must not run during prerendering.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("JavaScript must not run during prerendering.");
    }
}
