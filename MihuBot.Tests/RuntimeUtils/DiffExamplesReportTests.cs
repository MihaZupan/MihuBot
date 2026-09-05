using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MihuBot.Components;
using MihuBot.Components.Pages;
using MihuBot.Helpers;
using MihuBot.RuntimeUtils;
using MihuBot.RuntimeUtils.Jobs;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class DiffExamplesReportTests
{
    private static DiffExampleEntry Entry(string category = "same-size") => new()
    {
        Assembly = "System.Text.RegularExpressions",
        Method = "Example",
        Category = category,
        Description = "Changed instructions",
        ExtraInfo = "```regex\n[a-z]+\n```",
        Diff = "--- base\n+++ diff\n@@ -1 +1 @@\n-old\n+new",
        BaseBytes = 10,
        DiffBytes = 10,
    };

    private static DiffExamplesReport Report(params DiffExampleEntry[] entries) => new()
    {
        Summary = "Comparison summary",
        Notes = ["Examples selected across assemblies."],
        Entries = entries,
    };

    private static async Task<DiffExamplesReport> ReadAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await DiffExamplesReport.ReadAsync(stream, CancellationToken.None);
    }

    [Fact]
    public async Task ReadsPascalCaseContractAndPreservesAllCategories()
    {
        DiffExamplesReport source = Report(Entry("regression"), Entry("improvement"), Entry(), Entry("source"));
        DiffExamplesReport loaded = await ReadAsync(JsonSerializer.Serialize(source));

        Assert.Equal(source.Entries.Select(e => e.Category), loaded.Entries.Select(e => e.Category));
        Assert.Equal(source.Entries[0].ExtraInfo, loaded.Entries[0].ExtraInfo);
        Assert.Equal(source.Entries[0].Diff, loaded.Entries[0].Diff);
        Assert.Equal("10 -> 10 bytes (0)", loaded.Entries[0].ByteSummary);
    }

    [Fact]
    public async Task AcceptsExplicitEmptyReport()
    {
        DiffExamplesReport loaded = await ReadAsync(JsonSerializer.Serialize(Report()));
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public async Task PreservesLargeMultilineAnalysisSummary()
    {
        DiffExamplesReport source = Report();
        source.Summary = "Example counts\n\n" + new string(' ', DiffExamplesReport.MaxEntryCharacters) + "Full analysis\n";
        DiffExamplesReport loaded = await ReadAsync(JsonSerializer.Serialize(source));
        Assert.Equal(source.Summary, loaded.Summary);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Summary\":null,\"Notes\":[],\"Entries\":[]}")]
    [InlineData("{\"Summary\":\"\",\"Notes\":[null],\"Entries\":[]}")]
    [InlineData("{\"Summary\":\"\",\"Notes\":[],\"Entries\":[null]}")]
    public async Task RejectsInvalidReport(string json) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(json));

    [Fact]
    public async Task RejectsMalformedJson() =>
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync("{invalid"));

    [Fact]
    public async Task RejectsOversizedStreamBeforeReading()
    {
        using var stream = new OversizedStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DiffExamplesReport.ReadAsync(stream, CancellationToken.None));
        Assert.Equal(0, stream.Position);
    }

    private sealed class OversizedStream : MemoryStream
    {
        public override long Length => DiffExamplesReport.MaxArtifactBytes + 1L;
    }

    [Fact]
    public async Task RejectsExcessiveJsonValuesBeforeModelAllocation() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync("[" + string.Join(',', Enumerable.Repeat("null", 100_001)) + "]"));

    [Fact]
    public void RejectsExcessiveEntryCount()
    {
        DiffExamplesReport report = Report(Enumerable.Repeat(Entry(), DiffExamplesReport.MaxEntries + 1).ToArray());
        Assert.Throws<InvalidDataException>(report.Validate);
    }

    [Fact]
    public void RejectsExcessiveEntryAndTotalDiffContent()
    {
        DiffExampleEntry entry = Entry();
        entry.Diff = new string('x', DiffExamplesReport.MaxEntryCharacters + 1);
        Assert.Throws<InvalidDataException>(Report(entry).Validate);

        entry.Diff = new string('x', DiffExamplesReport.MaxEntryCharacters);
        DiffExamplesReport report = Report(Enumerable.Repeat(entry, 129).ToArray());
        Assert.Throws<InvalidDataException>(report.Validate);
    }

    [Fact]
    public void RejectsInvalidCategoryAndByteCounts()
    {
        Assert.Throws<InvalidDataException>(Report(Entry("unknown")).Validate);
        DiffExampleEntry entry = Entry();
        entry.BaseBytes = -1;
        Assert.Throws<InvalidDataException>(Report(entry).Validate);
    }

    [Fact]
    public void FiltersSearchContextCategoryAndAssemblyTogether()
    {
        DiffExampleEntry entry = Entry("source");
        Assert.True(entry.Matches("EXAMPLE", "", ""));
        Assert.True(entry.Matches("[a-z]", "source", entry.Assembly));
        Assert.False(entry.Matches("[a-z]", "improvement", entry.Assembly));
        Assert.False(entry.Matches("", "", "Other.Assembly"));
        Assert.False(entry.Matches("not present", "", ""));
    }

    [Fact]
    public void FiltersDiffContentsBeyondTheFirstDisplayedPage()
    {
        DiffExampleEntry entry = Entry();
        entry.ExtraInfo = null;
        entry.Diff = string.Concat(Enumerable.Repeat(" nop\n", 2100)) + "-mov eax, edi\n+vaddps ymm0, ymm1, ymm2";

        Assert.True(entry.Matches("VADDPS", "same-size", entry.Assembly));
        Assert.True(entry.Matches("mov eax", "", ""));
        Assert.True(entry.Matches("nop", "", ""));
        Assert.False(entry.Matches("vaddps", "regression", entry.Assembly));
        Assert.False(entry.Matches("vaddps", "same-size", "Other.Assembly"));
        Assert.False(entry.Matches("not present", "", ""));
    }

    [Theory]
    [InlineData(100, 150, 0.5)]
    [InlineData(100, 60, -0.4)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 0, -1)]
    [InlineData(0, 10, double.PositiveInfinity)]
    [InlineData(0, 0, 0)]
    public void RelativeSizeDeltaHandlesSizeChangesAndZeroBaseline(long baseline, long changed, double expected)
    {
        DiffExampleEntry entry = Entry();
        entry.BaseBytes = baseline;
        entry.DiffBytes = changed;
        Assert.Equal(expected, entry.RelativeSizeDelta);
        Assert.DoesNotContain("RelativeSizeDelta", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersByPercentageMagnitudeRatherThanCategoryOrByteCount()
    {
        DiffExampleEntry Create(string name, long baseline, long changed)
        {
            DiffExampleEntry entry = Entry();
            entry.Method = name;
            entry.BaseBytes = baseline;
            entry.DiffBytes = changed;
            return entry;
        }

        DiffExampleEntry source = Entry("source");
        source.Method = "source";
        source.BaseBytes = source.DiffBytes = null;
        DiffExampleEntry[] entries =
        [
            Create("same-size", 100, 100),
            Create("10% regression", 10000, 11000),
            Create("40% improvement", 100, 60),
            Create("50% regression", 100, 150),
            Create("60% improvement", 100, 40),
            source,
        ];

        Assert.Equal(
            ["60% improvement", "50% regression", "40% improvement", "10% regression", "same-size", "source"],
            entries.OrderByDescending(e => Math.Abs(e.RelativeSizeDelta)).Select(e => e.Method));
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("https://example.com")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a?x=y")]
    [InlineData("")]
    public void RejectsNonPublicPathIdentifiers(string externalId) =>
        Assert.False(DiffExamplesReport.IsPublicId(externalId));

    [Fact]
    public void SnapshotSurvivesSerializationWithoutTrustingArtifactUrls()
    {
        var record = new CompletedJobRecord
        {
            ExternalId = Snowflake.GetString(ulong.MaxValue),
            Artifacts = [new(DiffExamplesReport.JitArtifactName, "https://untrusted.invalid", 100)],
        };
        CompletedJobRecord restored = record.ToDbEntry().ToRecord();
        Assert.True(DiffExamplesReport.HasReport(restored.Artifacts));
        Assert.True(DiffExamplesReport.IsPublicId(restored.ExternalId));
        Assert.False(DiffExamplesReport.HasReport([new("LongDiffsRegressions.md", "", 0)]));
        Assert.False(DiffExamplesReport.HasReport([new("../JitDiffExamples.json", "", 0)]));
    }

    [Fact]
    public void AcceptsGeneratedBase64UrlPublicIds()
    {
        Assert.True(DiffExamplesReport.IsPublicId(Snowflake.NextString()));
        Assert.True(DiffExamplesReport.IsPublicId(Snowflake.GetString(0xfbff_ffff_ffff_ffff)));
        Assert.False(DiffExamplesReport.IsPublicId(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task RendersUntrustedContentAsTextAndOnlyFirstPageOfLines()
    {
        DiffExampleEntry entry = Entry();
        entry.ExtraInfo = "<script>alert('context')</script> **not markdown**";
        entry.Diff = "+<img src=x onerror=alert(1)>\n-old\n" +
            string.Join('\n', Enumerable.Range(0, 2500).Select(i => $" line-{i}"));
        entry.Truncated = true;

        string html = await RenderAsync<DiffExampleDetails>(new() { ["Entry"] = entry });

        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
        Assert.Contains("**not markdown**", html, StringComparison.Ordinal);
        Assert.Contains("diff-added", html, StringComparison.Ordinal);
        Assert.Contains("diff-removed", html, StringComparison.Ordinal);
        Assert.Contains("shortened by the runner", html, StringComparison.Ordinal);
        Assert.Contains("line-1997", html, StringComparison.Ordinal);
        Assert.DoesNotContain("line-1998", html, StringComparison.Ordinal);
        Assert.DoesNotContain("line-2499", html, StringComparison.Ordinal);
        Assert.Contains("Jump to page", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    public async Task RendersDiffsUpTo2000LinesWithoutPagingControls(int lineCount)
    {
        DiffExampleEntry entry = Entry();
        entry.Diff = string.Join('\n', Enumerable.Range(0, lineCount).Select(i => $" line-{i}"));

        string html = await RenderAsync<DiffExampleDetails>(new() { ["Entry"] = entry });

        Assert.Contains($"line-{lineCount - 1}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Previous lines", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Next lines", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Jump to page", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://mihubot.xyz/results.zip")]
    [InlineData("https://mihubot.xyz/results.zip?value=\"\\\n</script>")]
    public async Task FullRegexResultsExampleRendersCompleteCompilableCode(string url)
    {
        string html = await RenderAsync<RegexFullResultsExample>(new()
        {
            ["ArtifactUrl"] = url,
            ["ExternalId"] = "example-job",
        });
        string code = WebUtility.HtmlDecode(Regex.Match(html, "<code[^>]*>(.*?)</code>", RegexOptions.Singleline).Groups[1].Value);

        Assert.Contains("Download Results.zip", html, StringComparison.Ordinal);
        Assert.Contains("class=\"language-csharp\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script>", html, StringComparison.Ordinal);
        Assert.Contains("RegexResults-example-job.json", code, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize(url), code, StringComparison.Ordinal);
        Assert.Contains("IncludeFields = true", code, StringComparison.Ordinal);
        Assert.Contains("public string? FullDiff", code, StringComparison.Ordinal);
        Assert.Contains("SearchValuesOfChar", code, StringComparison.Ordinal);
        Assert.Contains("SearchValuesOfString", code, StringComparison.Ordinal);

        string[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create("RegexResultsExample",
            [CSharpSyntaxTree.ParseText(code)],
            references.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, StaticJsRuntime>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return rendered.ToHtmlString();
        });
    }

    private sealed class StaticJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering should not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static rendering should not invoke JavaScript.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MethodRowsKeepDescriptionsAndDiffsInsideExpandedView(bool expanded)
    {
        DiffExampleEntry entry = Entry();
        entry.Method = "Example<T>:Method";
        entry.Description = "Detailed description only shown when expanded";
        string html = await RenderAsync<DiffExampleRow>(new() { ["Entry"] = entry, ["Expanded"] = expanded });

        Assert.Contains("class=\"method-toggle\"", html, StringComparison.Ordinal);
        Assert.Contains("Example&lt;T&gt;:Method", html, StringComparison.Ordinal);
        Assert.Contains(entry.Assembly, html, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(html, "Example&lt;T&gt;:Method").Count);
        Assert.Equal(2, Regex.Matches(html, Regex.Escape(entry.Assembly)).Count);
        Assert.DoesNotContain("btn-outline-secondary", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<br", html, StringComparison.Ordinal);
        Assert.Equal(expanded, html.Contains(entry.Description, StringComparison.Ordinal));
        Assert.Equal(expanded, html.Contains("Unified diff", StringComparison.Ordinal));
        if (!expanded)
        {
            Assert.DoesNotContain("<p", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DiffLinesPreserveIndentationAndBlankLines()
    {
        DiffExampleEntry entry = Entry();
        entry.Diff = "-    before\r\n\r\n+    after";
        string html = await RenderAsync<DiffExampleDetails>(new() { ["Entry"] = entry });
        string[] lines = Regex.Matches(html, "<span[^>]*>(.*?)</span>", RegexOptions.Singleline)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .ToArray();
        Assert.Equal(["-    before\n", "\n", "+    after\n"], lines);
    }

    [Theory]
    [InlineData("improvement")]
    [InlineData("regression")]
    [InlineData("same-size")]
    [InlineData("source")]
    public async Task MethodRowsExposeTheirCategoryForColorHighlighting(string category)
    {
        string html = await RenderAsync<DiffExampleRow>(new() { ["Entry"] = Entry(category) });
        Assert.Contains($"data-category=\"{category}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeJobArtifactsExerciseDiffBrowserAndFullRegexResults()
    {
        Dictionary<string, byte[]> artifacts = FakeInMemoryJob.CreateDiffArtifacts();
        var entries = new List<DiffExampleEntry>();
        foreach (string name in new[] { DiffExamplesReport.JitArtifactName, DiffExamplesReport.RegexArtifactName })
        {
            using var stream = new MemoryStream(artifacts[name]);
            DiffExamplesReport report = await DiffExamplesReport.ReadAsync(stream, CancellationToken.None);
            entries.AddRange(report.Entries);
        }

        Assert.Equal(["improvement", "regression", "same-size", "source"], entries.Select(e => e.Category).Distinct().Order());
        Assert.True(entries.Select(e => e.Assembly).Distinct().Count() > 1);
        Assert.Contains(entries, e => e.Diff.Split('\n').Length > 2000);

        using var zipStream = new MemoryStream(artifacts["Results.zip"]);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        using var jsonStream = archive.GetEntry("Results.json")!.Open();
        using var json = await JsonDocument.ParseAsync(jsonStream);
        var result = json.RootElement[0];
        Assert.Equal("[a-z]+", result.GetProperty("Regex").GetProperty("Pattern").GetString());
        Assert.Equal(entries.Single(e => e.Category == "source").Diff, result.GetProperty("FullDiff").GetString());
        Assert.NotEqual(result.GetProperty("MainSource").GetString(), result.GetProperty("PrSource").GetString());
        Assert.Equal("Letters", result.GetProperty("SearchValuesOfChar")[0].GetProperty("Item1").GetString());
    }
}
