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
    private readonly MetadataReference[] _references;
    private readonly Type _generatorType;

    public string? GeneratorCommit { get; }
    public string? LoadError { get; }

    public RegexSourceGenerator()
    {
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

            Assembly generatorAssembly = Assembly.LoadFile(Path.GetFullPath("System.Text.RegularExpressions.Generator.dll"));

            _generatorType = generatorAssembly.GetTypes().Single(t => t.Name == "RegexGenerator");

            GeneratorCommit = Helpers.Helpers.GetCommitId(generatorAssembly);
        }
        catch (Exception ex)
        {
            _references = null!;
            _generatorType = null!;

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

    private async Task<GeneratorDriverRunResult> RunGeneratorCore(string code, LanguageVersion langVersion = LanguageVersion.Preview, CancellationToken cancellationToken = default)
    {
        Project proj = new AdhocWorkspace()
            .AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create()))
            .AddProject("RegexGeneratorTest", "RegexGeneratorTest.dll", LanguageNames.CSharp)
            .WithMetadataReferences(_references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, checkOverflow: false)
            .WithNullableContextOptions(NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(langVersion))
            .AddDocument("RegexGenerator.g.cs", SourceText.From(code, Encoding.UTF8)).Project;

        proj.Solution.Workspace.TryApplyChanges(proj.Solution);

        Compilation? comp = await proj.GetCompilationAsync(CancellationToken.None).ConfigureAwait(false);
        Debug.Assert(comp is not null);

        var generator = (IIncrementalGenerator)Activator.CreateInstance(_generatorType)!;

        CSharpGeneratorDriver cgd = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: CSharpParseOptions.Default.WithLanguageVersion(langVersion));
        GeneratorDriver gd = cgd.RunGenerators(comp, cancellationToken);
        return gd.GetRunResult();
    }

    public async Task<string> GenerateSourceText(string code, LanguageVersion langVersion = LanguageVersion.Preview, CancellationToken cancellationToken = default)
    {
        GeneratorDriverRunResult generatorResults = await RunGeneratorCore(code, langVersion, cancellationToken);
        string generatedSource = string.Concat(generatorResults.GeneratedTrees.Select(t => t.ToString()));

        if (generatorResults.Diagnostics.Length != 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, generatorResults.Diagnostics) + Environment.NewLine + generatedSource);
        }

        return generatedSource;
    }

    public async Task<string> GenerateSourceAsync(string pattern, RegexOptions options, CancellationToken cancellationToken)
    {
        string optionsSource = "";
        if (options != RegexOptions.None)
        {
            optionsSource = $", {string.Join(" | ", options.ToString().Split(',').Select(o => $"RegexOptions.{o.Trim()}"))}";
        }

        return await GenerateSourceText(
            $$"""
            using System.Text.RegularExpressions;
            partial class C
            {
                [GeneratedRegex({{SymbolDisplay.FormatLiteral(pattern, quote: true)}}{{optionsSource}})]
                public static partial Regex Valid();
            }
            """,
            cancellationToken: cancellationToken);
    }
}
