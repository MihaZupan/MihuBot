using System.IO.Compression;
using System.Text.Json;

namespace MihuBot.RuntimeUtils.Jobs;

public sealed class FakeInMemoryJob : JobBase
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { IncludeFields = true };

    public override string JobTitlePrefix => "Fake";

    public FakeInMemoryJob(RuntimeUtilsService parent, string githubCommenterLogin) : base(parent, githubCommenterLogin, "Fake args")
    {
        SuppressTrackingIssue = true;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        foreach (var (name, bytes) in CreateDiffArtifacts())
        {
            using var stream = new MemoryStream(bytes);
            await ArtifactReceivedAsync(name, stream, jobTimeout);
        }
        Log($"Dummy diff examples: {DiffExamplesUrl}");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        int counter = 0;

        RemoteLoginCredentials = "foo@127.0.0.1 bar";

        while (await timer.WaitForNextTickAsync(jobTimeout))
        {
            LastProgressSummary = new string('a', Random.Shared.Next(5, 20));
            LastSystemInfo = new SystemHardwareInfo(Random.Shared.NextDouble() * 16, 16, Random.Shared.NextDouble() * 64, 64);
            Log($"Dummy message {++counter} {new string('a', Random.Shared.Next(50, 500))}");
        }
    }

    public static Dictionary<string, byte[]> CreateDiffArtifacts()
    {
        const string MainSource = "public bool IsMatch(string input) => input.Length > 0 && input[0] >= 'a' && input[0] <= 'z';";
        const string PrSource = "public bool IsMatch(string input) => input.Length > 0 && (uint)(input[0] - 'a') <= 25;";
        const string SourceDiff = "@@ -1 +1 @@\n-" + MainSource + "\n+" + PrSource;
        const string RegexContext = "[GeneratedRegex(\"[a-z]+\")]\npublic static partial Regex Letters();";

        var jitReport = new DiffExamplesReport
        {
            Summary = "Local sample JIT diffs - no runtime build or comparison was performed.",
            Notes = ["Dummy data covers multiple assemblies and categories. LargeExample exercises diff-line paging."],
            Entries =
            [
                new()
                {
                    Assembly = "System.Private.CoreLib",
                    Method = "Example:Reduce(int):int",
                    Category = "improvement",
                    Description = "A smaller instruction sequence.",
                    BaseBytes = 6,
                    DiffBytes = 3,
                    Diff = "@@ -1,3 +1,2 @@\n-mov eax, edi\n-imul eax, 2\n+lea eax, [rdi+rdi]\n ret",
                },
                new()
                {
                    Assembly = "System.Text.RegularExpressions",
                    Method = "KnownRegex_0:TryMatchAtCurrentPosition",
                    Category = "regression",
                    Description = "A larger instruction sequence with regex context.",
                    ExtraInfo = RegexContext,
                    BaseBytes = 3,
                    DiffBytes = 6,
                    Diff = "@@ -1,2 +1,3 @@\n-xor eax, eax\n+mov eax, 1\n+add eax, edi\n ret",
                },
                new()
                {
                    Assembly = "System.Private.CoreLib",
                    Method = "Example:SameSize(int):int",
                    Category = "same-size",
                    Description = "Changed instructions with unchanged code size.",
                    BaseBytes = 3,
                    DiffBytes = 3,
                    Diff = "@@ -1,2 +1,2 @@\n-mov eax, edi\n+xor eax, eax\n ret",
                },
                new()
                {
                    Assembly = "System.Private.CoreLib",
                    Method = "Example:LargeExample():void",
                    Category = "same-size",
                    Description = "Artificially long diff for testing the 2,000-line page boundary.",
                    BaseBytes = 2100,
                    DiffBytes = 2100,
                    Diff = "@@ -1,2100 +1,2100 @@\n" + string.Join('\n',
                        Enumerable.Range(0, 2100).Select(i => $"{(i % 2 == 0 ? '-' : '+')}nop ; sample line {i + 1}")),
                },
            ],
        };
        var regexReport = new DiffExamplesReport
        {
            Summary = "Local sample regex source change.",
            Notes = ["Results.zip contains the same sample data for trying the C# analysis example."],
            Entries =
            [
                new()
                {
                    Assembly = "GeneratedRegex",
                    Method = "\"[a-z]+\"",
                    Category = "source",
                    Description = "42 uses; options: None",
                    ExtraInfo = RegexContext,
                    Diff = SourceDiff,
                },
            ],
        };

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        using (var jsonStream = archive.CreateEntry("Results.json").Open())
        {
            JsonSerializer.Serialize(jsonStream, new[]
            {
                new
                {
                    Regex = new { Pattern = "[a-z]+", Options = 0, Count = 42 },
                    MainSource,
                    PrSource,
                    FullDiff = SourceDiff,
                    ShortDiff = SourceDiff,
                    SearchValuesOfChar = new[] { (Name: "Letters", Values: "abcdefghijklmnopqrstuvwxyz") },
                    SearchValuesOfString = Array.Empty<(string[] Values, StringComparison ComparisonType)>(),
                },
            }, s_jsonOptions);
        }

        return new()
        {
            [DiffExamplesReport.JitArtifactName] = JsonSerializer.SerializeToUtf8Bytes(jitReport),
            [DiffExamplesReport.RegexArtifactName] = JsonSerializer.SerializeToUtf8Bytes(regexReport),
            ["Results.zip"] = zipStream.ToArray(),
        };
    }
}
