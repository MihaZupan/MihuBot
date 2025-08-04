using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

#nullable enable

namespace MihuBot.RuntimeUtils;

public sealed class RegexSourceGenerator
{
    public sealed record Generator(string Name, string Commit, string Repo, Type GeneratorType);

    public static readonly RegexOptions[] ValidOptions =
    [
        // All except None, Compiled, NonBacktracking
        RegexOptions.IgnoreCase, RegexOptions.Multiline, RegexOptions.ExplicitCapture, RegexOptions.Singleline,
        RegexOptions.IgnorePatternWhitespace, RegexOptions.RightToLeft, RegexOptions.ECMAScript, RegexOptions.CultureInvariant
    ];

    private readonly CSharpParseOptions _languageOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private readonly MetadataReference[] _references;
    private readonly Logger _logger;
    private readonly HybridCache _cache;

    public ImmutableArray<Generator> Generators { get; }
    public Generator Latest => Generators[0];

    public string? LoadError { get; }

    public RegexSourceGenerator(Logger logger, HybridCache cache)
    {
        _logger = logger;
        _cache = cache;

        try
        {
            _references =
            [
                LoadFromAssembly(typeof(object).Assembly),
                LoadFromAssembly(typeof(Unsafe).Assembly),
                LoadFromAssembly(typeof(Regex).Assembly),
                LoadFromAssembly(AppDomain.CurrentDomain.GetAssemblies()
                    .Single(a => a.FullName is not null && a.FullName.StartsWith("System.Runtime,", StringComparison.Ordinal)))
            ];

            List<(string name, string path)> versions =
            [
                ("10.0", Path.GetFullPath("System.Text.RegularExpressions.Generator.dll"))
            ];

            string generatorsDirectory = Path.Combine(Constants.StateDirectory, "RegexSourceGenerators");
            if (Directory.Exists(generatorsDirectory))
            {
                foreach (string path in Directory.GetFiles(generatorsDirectory))
                {
                    versions.Add((Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path)));
                }
            }

            List<Generator> generators = [];

            foreach ((string name, string path) in versions)
            {
                try
                {
                    Assembly generatorAssembly = Assembly.LoadFile(path);
                    Type generatorType = generatorAssembly.GetTypes().Single(t => t.Name == "RegexGenerator");
                    string commit = Helpers.Helpers.GetCommitId(generatorAssembly);
                    string repo = int.Parse(name.Split('.')[0]) < 10 ? "dotnet/runtime" : "dotnet/dotnet";

                    generators.Add(new Generator(name, commit, repo, generatorType));
                }
                catch (Exception ex) when (generators.Count == 0)
                {
                    _logger.DebugLog($"Failed to load generator '{name}' from '{path}': {ex}");
                }
            }

            Generators = [.. generators.OrderByDescending(g => g.Name, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering))];
        }
        catch (Exception ex)
        {
            _references = null!;
            LoadError = ex.Message;
        }

        static unsafe MetadataReference LoadFromAssembly(Assembly assembly)
        {
            // https://github.com/dotnet/runtime/issues/36590#issuecomment-689883856
            if (!assembly.TryGetRawMetadata(out byte* blob, out int length))
            {
                throw new InvalidOperationException($"Failed to get raw metadata for {assembly.FullName}");
            }

            ModuleMetadata moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
            AssemblyMetadata assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
            PortableExecutableReference metadataReference = assemblyMetadata.GetReference();

            return metadataReference;
        }
    }

    private async Task<GeneratorDriverRunResult> RunGeneratorCore(Generator generator, string code, CancellationToken cancellationToken = default)
    {
        Project proj = new AdhocWorkspace()
            .AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create()))
            .AddProject("RegexGeneratorTest", "RegexGeneratorTest.dll", LanguageNames.CSharp)
            .WithMetadataReferences(_references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, checkOverflow: false)
            .WithNullableContextOptions(NullableContextOptions.Enable))
            .WithParseOptions(_languageOptions)
            .AddDocument("RegexGenerator.g.cs", SourceText.From(code, Encoding.UTF8)).Project;

        proj.Solution.Workspace.TryApplyChanges(proj.Solution);

        Compilation? comp = await proj.GetCompilationAsync(CancellationToken.None).ConfigureAwait(false);
        Debug.Assert(comp is not null);

        ISourceGenerator sourceGenerator = ((IIncrementalGenerator)Activator.CreateInstance(generator.GeneratorType)!).AsSourceGenerator();

        CSharpGeneratorDriver cgd = CSharpGeneratorDriver.Create([sourceGenerator], parseOptions: _languageOptions);
        GeneratorDriver gd = cgd.RunGenerators(comp, cancellationToken);
        return gd.GetRunResult();
    }

    private async Task<string> GenerateSourceText(Generator generator, string code, CancellationToken cancellationToken = default)
    {
        GeneratorDriverRunResult generatorResults = await RunGeneratorCore(generator, code, cancellationToken);
        string generatedSource = string.Concat(generatorResults.GeneratedTrees.Select(t => t.ToString()));

        if (generatorResults.Diagnostics.Length != 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, generatorResults.Diagnostics) + Environment.NewLine + generatedSource);
        }

        return generatedSource;
    }

    public async Task<string> GenerateSourceAsync(Generator generator, string pattern, RegexOptions options, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();

        string source = await _cache.GetOrCreateAsync($"/regexsourcegen/{generator.Name}/{options}/{pattern.GetUtf8Sha384HashBase64Url()}", async cancellationToken =>
        {
            string optionsSource = "";
            if (options != RegexOptions.None)
            {
                optionsSource = $", {string.Join(" | ", options.ToString().Split(',').Select(o => $"RegexOptions.{o.Trim()}"))}";
            }

            return await GenerateSourceText(
                generator,
                $$"""
                using System.Text.RegularExpressions;
                partial class C
                {
                    [GeneratedRegex({{SymbolDisplay.FormatLiteral(pattern, quote: true)}}{{optionsSource}})]
                    public static partial Regex Valid();
                }
                """,
                cancellationToken);
        }, cancellationToken: cancellationToken);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        _logger.DebugLog($"[RegexSourceGenerator] Generated source for v={generator.Name} '{pattern}' ({options}) in {elapsed.TotalMilliseconds:N2} ms");

        return source;
    }
}
