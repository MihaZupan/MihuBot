namespace MihuBot.Helpers;

public static class StringHelpers
{
    public static string TruncateWithDotDotDot(this string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, maxLength - 4), " ...");
    }

    public static bool StartsWith(this ReadOnlySpan<char> span, char c)
    {
        return 0 < (uint)span.Length && span[0] == c;
    }

    public static bool EndsWith(this ReadOnlySpan<char> span, char c)
    {
        return 0 < (uint)span.Length && span[^1] == c;
    }

    public static bool Contains(this string[] matches, ReadOnlySpan<char> text, StringComparison stringComparison)
    {
        foreach (var match in matches)
        {
            if (text.Equals(match, stringComparison))
            {
                return true;
            }
        }
        return false;
    }

    public static string NormalizeNewLines(this string source)
    {
        return source.ReplaceLineEndings("\n");
    }

    public static string[] SplitLines(this string source, bool removeEmpty = false)
    {
        return source.NormalizeNewLines().Split('\n', removeEmpty ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None);
    }

    private static ReadOnlySpan<char> QuoteCharacters => new char[] { '"', '\'', '‘', '’', '“', '”' };

    public static string[] TrySplitQuotedArgumentString(ReadOnlySpan<char> arguments, out string error)
    {
        List<string> parts = new List<string>();

        arguments = arguments.Trim();

        while (!arguments.IsEmpty)
        {
            int nextQuote = arguments.IndexOfAny(QuoteCharacters);

            var before = nextQuote == -1 ? arguments : arguments.Slice(0, nextQuote);
            parts.AddRange(before.Trim().ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (nextQuote == -1)
            {
                break;
            }

            char quoteType = arguments[nextQuote];

            arguments = arguments.Slice(nextQuote + 1);

            int end = (quoteType == '‘' || quoteType == '’') ? arguments.IndexOfAny('‘', '’')
                : (quoteType == '“' || quoteType == '“') ? arguments.IndexOfAny('“', '“')
                : arguments.IndexOf(quoteType);

            if (end == -1)
            {
                error = $"No matching quote {quoteType} character found";
                return null;
            }

            var part = arguments.Slice(0, end).Trim();

            if (part.IsEmpty)
            {
                error = "Empty quoted string found";
                return null;
            }

            parts.Add(part.ToString());

            arguments = arguments.Slice(end + 1).TrimStart();
        }

        error = null;
        return parts.ToArray();
    }

    public static string SplitFirstTrimmed(this string source, char separator)
    {
        int index = source.AsSpan().IndexOf(separator);

        if (index == -1)
            return source.Trim();

        return source.AsSpan(0, index).Trim().ToString();
    }

    public static string SplitLastTrimmed(this string source, char separator)
    {
        int index = source.AsSpan().LastIndexOf(separator);

        if (index == -1)
            return source.Trim();

        return source.AsSpan(index + 1).Trim().ToString();
    }
}
