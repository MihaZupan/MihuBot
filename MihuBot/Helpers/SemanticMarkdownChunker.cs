using System.Buffers;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Syntax;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Text;
using MihuBot.DB.GitHub;

#nullable enable

namespace MihuBot.Helpers;

public static class SemanticMarkdownChunker
{
    public const int MaxSectionTokens = 4_000;

    public static IEnumerable<string> GetSections(Tokenizer tokenizer, int smallSectionTokenThreshold, IssueInfo issue, CommentInfo? comment, string markdown, string titleInfo)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            yield break;
        }

        if (IsLikelySpam(issue, comment, ref markdown))
        {
            yield break;
        }

        MarkdownDocument document = MarkdownHelper.ParseAdvanced(markdown);

        List<string> sectionTexts;

        try
        {
            sectionTexts = [.. GetMarkdownSections(tokenizer, smallSectionTokenThreshold, document, markdown)];

            // Ensure code blocks are included as sections.
            foreach (FencedCodeBlock codeBlock in document.Descendants<FencedCodeBlock>())
            {
                StringLineGroup lines = codeBlock.Lines;

                if (lines.Count == 0)
                {
                    continue;
                }

                sectionTexts.Add(lines.Lines[0].ToString());

                if (lines.Count > 1)
                {
                    sectionTexts.AddRange(SplitTextBlock(tokenizer, smallSectionTokenThreshold, lines.ToString()));
                }
            }
        }
        catch
        {
            // Can happen if some object offsets aren't properly set.
            sectionTexts = [.. SplitTextBlock(tokenizer, smallSectionTokenThreshold, markdown)];
        }

        foreach (string sectionText in sectionTexts)
        {
            string trimmed = sectionText.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || IsSectionWithoutContext(trimmed))
            {
                continue;
            }

            int tokens = tokenizer.CountTokens(trimmed);

            if (tokens <= 1)
            {
                // Skip empty sections
                continue;
            }

            if (tokens < 10 || trimmed.Contains(titleInfo, StringComparison.Ordinal))
            {
                // Avoid giving small comments too much context to stop "+1" from being considered as relevant just due to the title.
                yield return trimmed;
            }
            else
            {
                string author = comment is null
                    ? $"{(issue.PullRequest is null ? "Issue" : "Pull request")} author: {issue.User.Login}"
                    : $"Comment author: {comment.User.Login}";

                yield return $"{titleInfo}\n{author}\n\n{trimmed}";
            }
        }
    }

    private static IEnumerable<string> GetMarkdownSections(Tokenizer tokenizer, int smallSectionTokenThreshold, MarkdownDocument document, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            yield break;
        }

        int documentTokens = tokenizer.CountTokens(markdown);

        if (documentTokens <= MaxSectionTokens)
        {
            yield return markdown;

            if (documentTokens < smallSectionTokenThreshold)
            {
                yield break;
            }
        }
        else
        {
            foreach (string text in SplitTextBlock(tokenizer, MaxSectionTokens, markdown))
            {
                yield return text;
            }
        }

        if (document.Count > 1)
        {
            // Workaround for a bug in Markdig where the last block is not properly closed
            Block? lastChild = document.LastChild;
            if (lastChild is LinkReferenceDefinitionGroup) lastChild = document[^2];

            if (lastChild is FencedCodeBlock)
            {
                lastChild.Span.End = markdown.Length - 1;
            }
        }

        foreach (Block[] semanticSection in SplitThematicSections(document))
        {
            foreach (Block[] headingSection in SplitByHeadings(semanticSection))
            {
                foreach (string sectionText in GetSubSections(headingSection))
                {
                    yield return sectionText;
                }
            }

            IEnumerable<string> GetSubSections(Block[] section, int depth = 0)
            {
                string sectionText = SliceBlocks(section).ToString();
                int tokens = tokenizer.CountTokens(sectionText);

                if (tokens <= smallSectionTokenThreshold)
                {
                    yield return sectionText;
                }
                else
                {
                    (Block Block, int Tokens)[] blocks = [.. section.Select(b => (b, tokenizer.CountTokens(Slice(b))))];

                    for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
                    {
                        (Block block, int blockTokens) = blocks[blockIndex];

                        int combineThreshold = smallSectionTokenThreshold / 2;

                        if (tokens <= smallSectionTokenThreshold)
                        {
                            // Combine small blocks together
                            List<Block> combinedBlocks = [block];

                            while (blockIndex + 1 < blocks.Length && tokens + blocks[blockIndex + 1].Tokens <= combineThreshold)
                            {
                                combinedBlocks.Add(blocks[blockIndex + 1].Block);
                                tokens += blocks[blockIndex + 1].Tokens;
                                blockIndex++;
                            }

                            yield return SliceBlocks(combinedBlocks).ToString();
                        }
                        else if (block is Table)
                        {
                            // Skip for now
                        }
                        else if (block is ContainerBlock container && depth < 5)
                        {
                            foreach (string text in GetSubSections([.. container], depth + 1))
                            {
                                yield return text;
                            }
                        }
                        else
                        {
                            foreach (string text in SplitTextBlock(tokenizer, smallSectionTokenThreshold, Slice(block).ToString()))
                            {
                                yield return text;
                            }
                        }
                    }
                }
            }
        }

        ReadOnlySpan<char> Slice(Block block)
        {
            return markdown.AsSpan(block.Span.Start, block.Span.Length);
        }

        ReadOnlySpan<char> SliceBlocks(IList<Block> blocks)
        {
            if (blocks.Count == 0)
            {
                return [];
            }

            var span = new SourceSpan(blocks[0].Span.Start, blocks[^1].Span.End);

            if ((uint)span.Start > (uint)markdown.Length || (uint)(span.Start + span.Length) > (uint)markdown.Length || span.Length < 0)
            {
                // This might happen if some block's span wasn't updated properly in Markdig.

                span.Start = blocks.Min(b => b.Span.Start);
                span.End = blocks.Max(b => b.Span.End);
            }

            return markdown.AsSpan(span.Start, span.Length);
        }
    }

    public static bool IsUnlikelyToBeUseful(IssueInfo issue, CommentInfo comment, bool removeSectionsWithoutContext = true)
    {
        string body = comment.Body;

        return string.IsNullOrWhiteSpace(body)
            || IsLikelySpam(issue, comment, ref body)
            || (removeSectionsWithoutContext && IsSectionWithoutContext(body));
    }

    private static bool IsLikelySpam(IssueInfo issue, CommentInfo? comment, ref string markdown)
    {
        const string EmailReplyFooter = "You are receiving this because you are subscribed to this thread";

        UserInfo author = comment?.User ?? issue.User;

        markdown = markdown.ReplaceLineEndings("\n");
        markdown = markdown.Trim();

        if (!author.IsLikelyARealUser())
        {
            return true;
        }

        if (author.Login == "ghost" && markdown.Contains("<summary>Issue Details</summary>", StringComparison.Ordinal))
        {
            return true;
        }

        if (markdown.AsSpan().ContainsAny(s_spamPhrases))
        {
            return true;
        }

        if (comment is not null && markdown.IndexOf(EmailReplyFooter, StringComparison.OrdinalIgnoreCase) is int footerOffset && footerOffset >= 0)
        {
            markdown = markdown.Substring(0, footerOffset);

            int prefixOffset = markdown.IndexOf("Subject: ", StringComparison.Ordinal);
            if (prefixOffset >= 0)
            {
                prefixOffset = markdown.IndexOf('\n', prefixOffset);
                if (prefixOffset >= 0)
                {
                    markdown = markdown.Substring(prefixOffset + 1);
                }
            }
        }

        return false;
    }

    private static bool IsSectionWithoutContext(string section)
    {
        int newLines = section.AsSpan().Count('\n');

        if (newLines <= 1)
        {
            if (section.StartsWith('#') ||
                section.StartsWith("<!--", StringComparison.Ordinal) ||
                section.StartsWith("/backport to", StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith("/ba-g", StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith("<details>", StringComparison.Ordinal) ||
                section.StartsWith("</details>", StringComparison.Ordinal) ||
                section.StartsWith("Opened on behalf of", StringComparison.OrdinalIgnoreCase) ||
                section.Contains(" Update dependencies from ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (section.Length < 100 &&
            (section.Contains("/azp ", StringComparison.OrdinalIgnoreCase) ||
            section.Contains("_No response_", StringComparison.OrdinalIgnoreCase) ||
            section.Contains("I have searched the existing issues", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (section.Length < 30 &&
            (section.Contains("### Regression", StringComparison.Ordinal) ||
            section.Contains("### Known Workarounds", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        section = section.Trim(" \t\r\n.:,!?*_#`'").TrimEnd('s').ToString();

        return s_sectionsWithoutContext.Contains(section);
    }

    private static IEnumerable<Block[]> SplitThematicSections(MarkdownDocument document)
    {
        var currentSection = new List<Block>();

        foreach (Block block in document)
        {
            if (block is ThematicBreakBlock)
            {
                if (currentSection.Count > 0)
                {
                    yield return currentSection.ToArray();
                    currentSection.Clear();
                }
            }
            else if (block is LinkReferenceDefinitionGroup)
            {
                // Skip
            }
            else
            {
                currentSection.Add(block);
            }
        }

        if (currentSection.Count > 0)
        {
            yield return currentSection.ToArray();
        }
    }

    private static IEnumerable<Block[]> SplitByHeadings(Block[] blocks)
    {
        var currentSection = new List<Block>();
        int? topHeading = null;

        foreach (Block block in blocks)
        {
            if (block is HeadingBlock heading)
            {
                if (!topHeading.HasValue || heading.Level != topHeading.Value)
                {
                    topHeading = Math.Max(topHeading ?? 0, heading.Level);
                }
                else
                {
                    if (currentSection.Count > 0)
                    {
                        yield return currentSection.ToArray();
                        currentSection.Clear();
                    }
                }
            }

            currentSection.Add(block);
        }

        if (currentSection.Count > 0)
        {
            yield return currentSection.ToArray();
        }
    }

    private static List<string> SplitTextBlock(Tokenizer tokenizer, int maxTokensPerParagraph, string text)
    {
#pragma warning disable SKEXP0050 // Type is for evaluation purposes only
        return TextChunker.SplitPlainTextParagraphs(
            lines: text.Split('\n'),
            maxTokensPerParagraph: maxTokensPerParagraph,
            overlapTokens: 20,
            chunkHeader: null,
            tokenCounter: text => tokenizer.CountTokens(text));
#pragma warning restore SKEXP0050
    }

    public static string TrimTextToTokens(Tokenizer tokenizer, string text, int maxTokens)
    {
        int tokens = tokenizer.CountTokens(text);
        if (tokens <= maxTokens)
        {
            return text;
        }

        int index = tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out _);
        return text.Substring(0, index);
    }

    private static readonly SearchValues<string> s_spamPhrases = SearchValues.Create(
    [
        "This issue has been marked `needs-author-action`",
        "I couldn't figure out the best area label to add to this ",
        "This issue has been automatically marked `no-recent-activity`",
        "This issue will now be closed since it had been marked `no-recent-activity`",
        "Tagging subscribers to this area:",
        "CI/CD Pipelines for this repository:",
        "<summary>Issue Details</summary>",
        "<!-- runfo report start -->",
        "Note regarding the `new-api-needs-documentation` label",
        "<!--Known issue error report start -->",
        "<!-- Known issue validation start -->",
        "Run these commands to merge this pull request from the command line.",
        "[automated] Merge branch ",
    ], StringComparison.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> s_sectionsWithoutContext = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
    [
        "",
        "+1",
        "After",
        "Before",
        "Error message",
        "Failed test",
        "Log",
        "OS & Arch:**\nWindows x64",
        "Stack trace",
        "Steps to Reproduce",
        "Output",
        "After PR checks are complete push the branch",
        "After",
        "Before",
        "Code",
        "Current codegen",
        "Ditto",
        "Done",
        "Dotnet SDK installed for dotnet command",
        "Error message",
        "Error",
        "Example",
        "Failing configuration",
        "Failed test",
        "Failure Message",
        "Failure",
        "Fixed",
        "For example",
        "generate",
        "Hi",
        "Hi all",
        "Hello",
        "Lgtm",
        "Message",
        "New codegen",
        "No failure",
        "Note",
        "Output",
        "Program.c",
        "Repro step",
        "Repro",
        "Result",
        "Running commands from the runtime folder",
        "Source file was",
        "Stack Trace",
        "Steps to reproduce",
        "Thank",
        "Thank you",
        "Ok, thanks",
        "Thought",
        "TODO",
        "</div></details>",
        "</p>\n</details>",
        "</p>\n</details>",
        "<hr>",
        "<hr />",
        "<summary>Detail diffs</summary>",
        "```log\n\n```",
        "```suggestion\n```",
        "```\ngit push\n```",
        "addressed in recent commit",
        "e.g",
        "n/a",
        "Unknown/Other",
        "Reproduction",
        "Steps to reproduce",
        "Sounds good",
        "Stacktrace",
        "Addressed",
        "Updated",
        "Same here",
        "to",
        "with",
        "Result",
        "Will do",
        "I'll take a look",
        "Sure",
        "Correct",
        "What do you think",
        "Ok",
        "Yes",
        "csproj",
        "Labels",
    ]);
}
