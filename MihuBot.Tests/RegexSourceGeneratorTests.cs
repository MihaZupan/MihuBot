using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Components.Pages;
using MihuBot.RuntimeUtils;

namespace MihuBot.Tests;

public sealed class RegexSourceGeneratorTests
{
    [Theory]
    [InlineData("a+b", RegexOptions.None)]
    [InlineData("[a-z]+", RegexOptions.IgnoreCase)]
    public async Task BundledGeneratorProducesSource(string pattern, RegexOptions options)
    {
        await using var provider = CreateServices();
        List<string> logs = [];
        var service = new RegexSourceGenerator(logs.Add, provider.GetRequiredService<HybridCache>());

        Assert.True(service.LoadError is null, string.Join(Environment.NewLine, logs.Append(service.LoadError ?? "")));
        var generator = Assert.Single(service.Generators, g => g.Name == $"{Environment.Version.Major}.0");
        var (source, error) = await service.GenerateSourceAsync(generator, pattern, options, CancellationToken.None);

        Assert.Null(error);
        Assert.Contains("TryMatchAtCurrentPosition", source, StringComparison.Ordinal);
        string coreMethods = RegexSourceGen.GetCoreMethods(source);
        Assert.NotEqual(source, coreMethods);
        Assert.Contains("private bool TryMatchAtCurrentPosition", coreMethods, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public async Task SimplePatternsRemainVisibleInCoreMethodsView(string pattern)
    {
        await using var provider = CreateServices();
        List<string> logs = [];
        var service = new RegexSourceGenerator(logs.Add, provider.GetRequiredService<HybridCache>());

        Assert.True(service.LoadError is null, string.Join(Environment.NewLine, logs.Append(service.LoadError ?? "")));
        var generator = Assert.Single(service.Generators, g => g.Name == $"{Environment.Version.Major}.0");
        var (source, error) = await service.GenerateSourceAsync(generator, pattern, RegexOptions.None, CancellationToken.None);

        Assert.Null(error);
        Assert.False(string.IsNullOrWhiteSpace(RegexSourceGen.GetCoreMethods(source)));
    }

    [Fact]
    public void CoreMethodsViewPreservesErrors()
    {
        const string error = "// Failed to load any regex source generators";
        Assert.Equal(error, RegexSourceGen.GetCoreMethods(error));
    }

    [Fact]
    public async Task InvalidPatternReportsDiagnostic()
    {
        await using var provider = CreateServices();
        List<string> logs = [];
        var service = new RegexSourceGenerator(logs.Add, provider.GetRequiredService<HybridCache>());

        Assert.True(service.LoadError is null, string.Join(Environment.NewLine, logs.Append(service.LoadError ?? "")));
        var generator = Assert.Single(service.Generators, g => g.Name == $"{Environment.Version.Major}.0");
        var (_, error) = await service.GenerateSourceAsync(generator, "[", RegexOptions.None, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("SYSLIB1042", error, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        return services.BuildServiceProvider();
    }
}
