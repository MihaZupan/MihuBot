using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Helpers;

public static class StringHelpers
{
    public static string TruncateWithDotDotDot(this string text, int maxLength)
    {
        if (text.Length <= maxLength || text.Length <= 4)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, maxLength - 4), " ...");
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

    private static readonly SearchValues<char> s_quoteCharacters = SearchValues.Create("\"'‘’“”");

    public static string[]? TrySplitQuotedArgumentString(ReadOnlySpan<char> arguments, out string? error)
    {
        List<string> parts = new List<string>();

        arguments = arguments.Trim();

        while (!arguments.IsEmpty)
        {
            int nextQuote = arguments.IndexOfAny(s_quoteCharacters);

            var before = nextQuote < 0 ? arguments : arguments.Slice(0, nextQuote);
            parts.AddRange(before.Trim().ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (nextQuote < 0)
            {
                break;
            }

            char quoteType = arguments[nextQuote];

            arguments = arguments.Slice(nextQuote + 1);

            int end = (quoteType is '‘' or '’') ? arguments.IndexOfAny('‘', '’')
                : (quoteType is '“' or '“') ? arguments.IndexOfAny('“', '“')
                : arguments.IndexOf(quoteType);

            if (end < 0)
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

        if (index < 0)
            return source.Trim();

        return source.AsSpan(0, index).Trim().ToString();
    }

    public static string SplitLastTrimmed(this string source, char separator)
    {
        int index = source.AsSpan().LastIndexOf(separator);

        if (index < 0)
            return source.Trim();

        return source.AsSpan(index + 1).Trim().ToString();
    }

    [InlineArray(64)]
    private struct SHA512Buffer { private byte _b; }

    public static string GetUtf8Sha3_512HashBase64Url(this string text)
    {
        SHA512Buffer hash = default;
        ComputeHash(text, hash);
        return Base64Url.EncodeToString(hash);
    }

    public static Guid GetTruncatedContentHash(this string content)
    {
        SHA512Buffer hash = default;
        ComputeHash(content, hash);
        return new Guid(hash[..16], bigEndian: false);
    }

    private static void ComputeHash(string content, Span<byte> hash)
    {
        byte[] utf8 = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(content.Length));
        int byteCount = Encoding.UTF8.GetBytes(content, utf8);
        SHA3_512.HashData(utf8.AsSpan(0, byteCount), hash);
        ArrayPool<byte>.Shared.Return(utf8);
    }

    [return: NotNullIfNotNull(nameof(text))]
    public static string? RemoveNullChars(this string? text) => text?.Replace("\0", "", StringComparison.Ordinal);

    public static long ParseInt64(ReadOnlySpan<char> text)
    {
        return long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public static bool TryGetArgument(ReadOnlySpan<char> arguments, string argument, [NotNullWhen(true)] out string? value)
    {
        value = null;
        argument = $"-{argument} ";

        int offset = arguments.IndexOf(argument, StringComparison.OrdinalIgnoreCase);
        if (offset < 0) return false;

        arguments = arguments.Slice(offset + argument.Length);

        int length = arguments.IndexOf(' ');
        if (length >= 0)
        {
            arguments = arguments.Slice(0, length);
        }

        value = arguments.Trim().ToString();

        if (value.Length == 0)
        {
            value = null;
            return false;
        }

        return true;
    }

    public static bool TryParseSize(ReadOnlySpan<char> text, int defaultScale, out long size)
    {
        text = text.Trim();

        if (text.EndsWith('g') || text.EndsWith('G'))
        {
            defaultScale = 1024 * 1024 * 1024;
            text = text.Slice(0, text.Length - 1);
        }
        else if (text.EndsWith('m') || text.EndsWith('M'))
        {
            defaultScale = 1024 * 1024;
            text = text.Slice(0, text.Length - 1);
        }
        else if (text.EndsWith('k') || text.EndsWith('K'))
        {
            defaultScale = 1024;
            text = text.Slice(0, text.Length - 1);
        }

        text = text.Trim();

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0)
        {
            size = value * defaultScale;
            return true;
        }

        size = 0;
        return false;
    }
}
