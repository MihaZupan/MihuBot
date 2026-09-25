using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task RestoringSourceAfterEmptyCodeHighlightsTheReplacementElement(string? emptyCode)
    {
        await using var renderer = new HighlightRenderer();
        await renderer.RenderAsync("public class Example {}");
        await renderer.RenderAsync(emptyCode);
        await renderer.RenderAsync("public class Example {}");

        Assert.Equal(2, renderer.JavaScript.Calls.Count);
        Assert.NotEqual(renderer.JavaScript.Calls[0].Element.Id, renderer.JavaScript.Calls[1].Element.Id);
    }

    [Fact]
    public async Task LateHighlightCompletionDoesNotSkipRestoredSource()
    {
        await using var renderer = new HighlightRenderer();
        var completion = new TaskCompletionSource();
        renderer.JavaScript.NextCompletion = completion.Task;
        await renderer.RenderAsync("public class First {}");
        await renderer.RenderAsync("public class Second {}");
        await renderer.Dispatcher.InvokeAsync(completion.SetResult);
        await renderer.RenderAsync("public class Second {}");
        Assert.Equal(2, renderer.JavaScript.Calls.Count);

        await renderer.RenderAsync("public class First {}");

        Assert.Equal(3, renderer.JavaScript.Calls.Count);
        Assert.NotEqual(renderer.JavaScript.Calls[0].Element.Id, renderer.JavaScript.Calls[2].Element.Id);
    }

    [Fact]
    public async Task UnchangedSourceIsNotHighlightedAgainButLanguageChangesAre()
    {
        await using var renderer = new HighlightRenderer();
        await renderer.RenderAsync("public class Example {}");
        await renderer.RenderAsync("public class Example {}");
        Assert.Single(renderer.JavaScript.Calls);

        await renderer.RenderAsync("public class Example {}", "language-plaintext");
        Assert.Equal(2, renderer.JavaScript.Calls.Count);
    }

#pragma warning disable BL0006
    private sealed class HighlightRenderer : Renderer
    {
        private readonly int _componentId;

        public HighlightJavaScript JavaScript { get; }
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public HighlightRenderer() : this(new HighlightJavaScript())
        {
        }

        private HighlightRenderer(HighlightJavaScript javaScript)
            : base(new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(javaScript).BuildServiceProvider(), NullLoggerFactory.Instance)
        {
            JavaScript = javaScript;
            _componentId = AssignRootComponentId(InstantiateComponent(typeof(CodeHighlight)));
        }

        public Task RenderAsync(string? code, string language = "language-csharp") =>
            Dispatcher.InvokeAsync(() => RenderRootComponentAsync(_componentId,
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CodeHighlight.Code)] = code,
                    [nameof(CodeHighlight.Language)] = language,
                })));

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Rendering failed.", exception);
    }
#pragma warning restore BL0006

#pragma warning disable BL0016
    private sealed class HighlightJavaScript : IJSRuntime, IJSObjectReference
    {
        public List<(ElementReference Element, string Code)> Calls { get; } = [];
        public Task? NextCompletion { get; set; }

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "import")
            {
                return (TValue)(object)this;
            }

            Assert.Equal("highlight", identifier);
            Calls.Add(((ElementReference)args![0]!, (string)args[1]!));
            Task? completion = NextCompletion;
            NextCompletion = null;

            if (completion is not null)
            {
                await completion;
            }

            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore BL0016
}
