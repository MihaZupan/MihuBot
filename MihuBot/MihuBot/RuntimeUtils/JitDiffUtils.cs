using System.Buffers;
using System.IO.Pipelines;
using System.Text.RegularExpressions;

namespace MihuBot.RuntimeUtils;

public static partial class JitDiffUtils
{
    public static string GetCommentMarkdown(string[] diffs, int lengthLimit, bool regressions, out bool lengthLimitExceeded)
    {
        lengthLimitExceeded = false;

        if (diffs.Length == 0)
        {
            return string.Empty;
        }

        int currentLength = 0;
        bool someChangesSkipped = false;

        List<string> changesToShow = new();

        foreach (var change in diffs)
        {
            if (change.Length > lengthLimit)
            {
                someChangesSkipped = true;
                lengthLimitExceeded = true;
                continue;
            }

            if ((currentLength += change.Length) > lengthLimit)
            {
                lengthLimitExceeded = true;
                break;
            }

            changesToShow.Add(change);
        }

        StringBuilder sb = new();

        sb.AppendLine($"## Top method {(regressions ? "regressions" : "improvements")}");
        sb.AppendLine();

        foreach (string md in changesToShow)
        {
            sb.AppendLine(md);
        }

        sb.AppendLine();

        if (someChangesSkipped)
        {
            sb.AppendLine("Note: some changes were skipped as they were too large to fit into a comment.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static (string Description, string Name)[] ParseDiffEntries(string diffSource, bool regressions)
    {
        ReadOnlySpan<char> text = diffSource.ReplaceLineEndings("\n");

        string start = regressions ? "Top method regressions" : "Top method improvements";
        int index = text.IndexOf(start, StringComparison.Ordinal);

        if (index < 0)
        {
            return Array.Empty<(string, string)>();
        }

        text = text.Slice(index);
        text = text.Slice(text.IndexOf('\n') + 1);
        text = text.Slice(0, text.IndexOf("\n\n", StringComparison.Ordinal));

        return text
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JitDiffRegressionNameRegex().Match(line))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToArray();
    }

    public static async Task<string> TryGetMethodDumpAsync(string diffPath, string methodName)
    {
        using var fs = File.OpenRead(diffPath);
        var pipe = PipeReader.Create(fs);

        bool foundPrefix = false;
        bool foundSuffix = false;
        byte[] prefix = Encoding.ASCII.GetBytes($"; Assembly listing for method {methodName}");
        byte[] suffix = Encoding.ASCII.GetBytes("; ============================================================");

        StringBuilder sb = new();

        while (true)
        {
            ReadResult result = await pipe.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition? position = null;

            do
            {
                position = buffer.PositionOf((byte)'\n');

                if (position != null)
                {
                    var line = buffer.Slice(0, position.Value);

                    ProcessLine(
                        line.IsSingleSegment ? line.FirstSpan : line.ToArray(),
                        prefix, suffix, ref foundPrefix, ref foundSuffix);

                    if (foundPrefix)
                    {
                        sb.AppendLine(Encoding.UTF8.GetString(line));

                        if (sb.Length > 1024 * 1024)
                        {
                            return string.Empty;
                        }
                    }

                    if (foundSuffix)
                    {
                        return sb.ToString();
                    }

                    buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                }
            }
            while (position != null);

            pipe.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                return string.Empty;
            }
        }

        static void ProcessLine(ReadOnlySpan<byte> line, byte[] prefix, byte[] suffix, ref bool foundPrefix, ref bool foundSuffix)
        {
            if (foundPrefix)
            {
                if (line.StartsWith(suffix))
                {
                    foundSuffix = true;
                }
            }
            else
            {
                if (line.StartsWith(prefix))
                {
                    foundPrefix = true;
                }
            }
        }
    }

    [GeneratedRegex(@" *(.*?) : .*? - ([^ ]*)")]
    private static partial Regex JitDiffRegressionNameRegex();
}
