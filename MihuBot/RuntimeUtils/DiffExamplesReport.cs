using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MihuBot.RuntimeUtils;

public sealed class DiffExamplesReport
{
    public const string JitArtifactName = "JitDiffExamples.json";
    public const string RegexArtifactName = "RegexSourceDiffExamples.json";
    public const int MaxArtifactBytes = 128 * 1024 * 1024;
    public const int MaxEntries = 2000;
    public const int MaxDiffCharacters = 16 * 1024 * 1024;
    public const int MaxEntryCharacters = 128 * 1024;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { MaxDepth = 16 };

    public string Summary { get; set; }
    public string[] Notes { get; set; }
    public DiffExampleEntry[] Entries { get; set; }

    public static bool IsArtifact(string name) => name is JitArtifactName or RegexArtifactName;

    public static bool HasReport(IEnumerable<CompletedJobRecord.Artifact> artifacts) =>
        artifacts?.Any(a => IsArtifact(a.FileName)) == true;

    public static bool IsPublicId(string externalId) =>
        externalId is { Length: > 0 and <= 11 } && Snowflake.TryGetFromString(externalId, out _);

    public string ToIssueSummaryMarkdown(string reportUrl)
    {
        string details = string.Join("\n\n", [Summary, .. Notes]);
        return $"### Diffs\n\n[Browse JIT diff results and examples]({reportUrl})\n\n{CodeBlock(LimitIssueText(details, 16_000))}";
    }

    public IEnumerable<string> GetIssueExampleComments(string reportUrl, int maxCharacters)
    {
        string footer = $"\n\n[Browse all JIT diff results and examples]({reportUrl})";

        foreach (var (category, title) in new[]
        {
            ("regression", "JIT diff regressions"),
            ("improvement", "JIT diff improvements"),
            ("same-size", "Same-size JIT changes"),
        })
        {
            DiffExampleEntry[] entries = [.. Entries.Where(e => e.Category == category)
                .OrderByDescending(e => Math.Abs(e.RelativeSizeDelta))];

            if (entries.Length == 0)
            {
                continue;
            }

            var comment = new StringBuilder($"### {title}\n\n");
            int included = 0;

            foreach (DiffExampleEntry entry in entries)
            {
                string example =
                    $"""
                    <details>
                    <summary>{System.Net.WebUtility.HtmlEncode(entry.Method)} ({System.Net.WebUtility.HtmlEncode(entry.Description)})</summary>

                    Assembly: <code>{System.Net.WebUtility.HtmlEncode(entry.Assembly)}</code>

                    {CodeBlock(LimitIssueText(entry.Diff, 16_000), "diff")}
                    {(entry.Truncated ? "\nNote: this example has reduced context or was truncated by the runner.\n" : "")}
                    </details>


                    """;

                if (comment.Length + example.Length + footer.Length + 256 > maxCharacters)
                {
                    continue;
                }

                comment.Append(example);
                included++;

                if (included == 20)
                {
                    break;
                }
            }

            comment.Append($"Showing {included} of {entries.Length} reported examples in this category.");
            comment.Append(footer);
            yield return comment.ToString();
        }
    }

    private static string LimitIssueText(string text, int limit) => text.Length <= limit
        ? text
        : $"{text[..limit]}\n... truncated for GitHub; see the full report in the diff browser ...";

    private static string CodeBlock(string text, string language = "")
    {
        int longestRun = 0;
        int currentRun = 0;

        foreach (char character in text)
        {
            currentRun = character == '`' ? currentRun + 1 : 0;
            longestRun = Math.Max(longestRun, currentRun);
        }

        string fence = new('`', Math.Max(3, longestRun + 1));
        return $"{fence}{language}\n{text}\n{fence}";
    }

    public static async Task<DiffExamplesReport> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaxArtifactBytes)
        {
            throw new InvalidDataException("Diff report exceeds the size limit.");
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > MaxArtifactBytes)
            {
                throw new InvalidDataException("Diff report exceeds the size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        ValidateTokenBudget(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        buffer.Position = 0;
        DiffExamplesReport report = await JsonSerializer.DeserializeAsync<DiffExamplesReport>(
            buffer, s_jsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Diff report is empty.");
        report.Validate();
        return report;
    }

    private static void ValidateTokenBudget(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > 100_000)
            {
                throw new InvalidDataException("Diff report contains too many JSON values.");
            }
        }
    }

    public void Validate()
    {
        if (Summary is null || Summary.Length > MaxDiffCharacters ||
            Notes is null || Notes.Length > 128 || Notes.Any(n => n is null || n.Length > 16 * 1024) ||
            Entries is null || Entries.Length > MaxEntries)
        {
            throw new InvalidDataException("Invalid diff report.");
        }

        long total = 0;
        long metadata = Summary.Length + Notes.Sum(n => n.Length);
        foreach (DiffExampleEntry entry in Entries)
        {
            if (entry is null ||
                string.IsNullOrEmpty(entry.Assembly) || entry.Assembly.Length > 16 * 1024 ||
                string.IsNullOrEmpty(entry.Method) || entry.Method.Length > MaxEntryCharacters ||
                entry.Category is not ("regression" or "improvement" or "same-size" or "source") ||
                entry.Description is null || entry.Description.Length > MaxEntryCharacters ||
                entry.ExtraInfo?.Length > MaxEntryCharacters ||
                entry.Diff is null || entry.Diff.Length > MaxEntryCharacters ||
                entry.BaseBytes < 0 || entry.DiffBytes < 0)
            {
                throw new InvalidDataException("Invalid diff report entry.");
            }

            total += entry.Diff.Length;
            metadata += entry.Assembly.Length + entry.Method.Length + entry.Description.Length + (entry.ExtraInfo?.Length ?? 0);
        }

        if (total > MaxDiffCharacters || metadata > MaxDiffCharacters)
        {
            throw new InvalidDataException("Diff report exceeds the content limit.");
        }
    }
}

public sealed class DiffExampleEntry
{
    public string Assembly { get; set; }
    public string Method { get; set; }
    public string Category { get; set; }
    public string Description { get; set; }
    public string ExtraInfo { get; set; }
    public string Diff { get; set; }
    public bool Truncated { get; set; }
    public long? BaseBytes { get; set; }
    public long? DiffBytes { get; set; }

    [JsonIgnore]
    public double RelativeSizeDelta => BaseBytes is { } baseline && DiffBytes is { } changed
        ? baseline > 0 ? (double)(changed - baseline) / baseline : changed > 0 ? double.PositiveInfinity : 0
        : 0;

    public bool Matches(string search, string category, string assembly) =>
        (string.IsNullOrEmpty(category) || Category.Equals(category, StringComparison.Ordinal)) &&
        (string.IsNullOrEmpty(assembly) || Assembly.Equals(assembly, StringComparison.Ordinal)) &&
        (string.IsNullOrWhiteSpace(search) ||
            Method.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            Assembly.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            ExtraInfo?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
            Diff.Contains(search, StringComparison.OrdinalIgnoreCase));

    public string ByteSummary => BaseBytes is { } baseline && DiffBytes is { } changed
        ? string.Create(CultureInfo.InvariantCulture, $"{baseline:N0} -> {changed:N0} bytes ({changed - baseline:+#,0;-#,0;0})")
        : null;

    public static string LineClass(string line) => line switch
    {
        _ when line.StartsWith("@@", StringComparison.Ordinal) => "diff-hunk",
        _ when line.StartsWith('+') => "diff-added",
        _ when line.StartsWith('-') => "diff-removed",
        _ => null,
    };
}
