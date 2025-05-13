using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MihuBot.Helpers;

public static class MarkdownHelper
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePreciseSourceLocation()
        .Build();

    public static MarkdownDocument ParseAdvanced(string markdown)
    {
        return Markdown.Parse(markdown, s_pipeline);
    }

    public static string ToHtmlAdvanced(this MarkdownDocument document)
    {
        return Markdown.ToHtml(document, s_pipeline);
    }

    // https://github.com/xoofx/markdig/issues/858
    public static void FixUpPartialDocument(MarkdownDocument document)
    {
        Block lastChild = document.LastChild;
        while (lastChild is ContainerBlock containerBlock)
        {
            lastChild = containerBlock.LastChild;
        }

        if (lastChild is not LeafBlock leafBlock)
        {
            return;
        }

        // "Level: 2" means '-' was used.
        if (leafBlock is HeadingBlock { IsSetext: true, Level: 2, HeaderCharCount: 1 } setext)
        {
            var paragraph = new ParagraphBlock();
            paragraph.Inline = new ContainerInline();
            setext.Inline?.EmbraceChildrenBy(paragraph.Inline);

            var parent = setext.Parent!;
            parent[parent.IndexOf(setext)] = paragraph;

            leafBlock = paragraph;
        }

        if (leafBlock.Inline?.LastChild is LiteralInline literal)
        {
            // A LiteralInline with a backtick character is a potential CodeInline that wasn't closed.
            int indexOfBacktick = literal.Content.IndexOf('`');
            if (indexOfBacktick >= 0)
            {
                // But it could also happen if the backticks were escaped.
                if (literal.Content.AsSpan().Count('`') == 1 && !literal.IsFirstCharacterEscaped)
                {
                    // "Text with `a code inline" => "Text with `a code inline`"
                    int originalLength = literal.Content.Length;

                    // Shorten the existing text. -1 to exclude the backtick.
                    literal.Content.End = indexOfBacktick - 1;

                    // Insert a CodeInline with the remainder. +1 and -1 to account for the backtick.
                    string code = literal.Content.Text.Substring(indexOfBacktick + 1, originalLength - literal.Content.Length - 1);
                    literal.InsertAfter(new CodeInline(code));

                    return;
                }
            }

            Inline previousSibling = literal.PreviousSibling;

            // Handle unclosed bold/italic that don't yet have any following content.
            if (previousSibling is null && IsEmphasisStart(literal.Content.AsSpan()))
            {
                literal.Remove();
                return;
            }

            if (previousSibling is EmphasisInline)
            {
                // Handle cases like "**_foo_ and bar" by skipping the _foo_ emphasis.
                previousSibling = previousSibling.PreviousSibling;
            }

            if (previousSibling is LiteralInline previousInline)
            {
                var content = previousInline.Content.AsSpan();

                // Unclosed bold/italic (EmphasisInline)?
                // Note that this doesn't catch cases with mixed opening chars, e.g. "**_text"
                if (IsEmphasisStart(content))
                {
                    literal.Remove();

                    var emphasis = new EmphasisInline();
                    emphasis.DelimiterChar = '*';
                    emphasis.DelimiterCount = previousInline.Content.Length;

                    previousInline.ReplaceBy(emphasis);

                    if (emphasis.DelimiterCount <= 2)
                    {
                        // Just * or **
                        emphasis.AppendChild(literal);
                    }
                    else
                    {
                        // E.g. "***text", which we need to turn into nested <em><strong>text</strong></em>
                        emphasis.DelimiterCount = 1;

                        var nestedStrong = new EmphasisInline();
                        nestedStrong.DelimiterChar = emphasis.DelimiterChar;
                        nestedStrong.DelimiterCount = 2;

                        nestedStrong.AppendChild(literal);
                        emphasis.AppendChild(nestedStrong);
                    }

                    if (emphasis.NextSibling is EmphasisInline nextSibling)
                    {
                        // This is the EmphasisInline we've skipped before. Fix the ordering.
                        // "**_foo_ and bar" is currently "** and bar**_foo_".
                        // Move the skipped emphasis to be the first child of the node we've generated.
                        nextSibling.Remove();
                        emphasis.FirstChild!.InsertBefore(nextSibling);
                    }

                    return;
                }
                else if (content is "[" or "![")
                {
                    // In-progress link, e.g. [text](http://
                    literal.Remove();
                    previousInline.Remove();
                }
            }
        }
        else if (leafBlock.Inline?.LastChild is LinkDelimiterInline linkDelimiterInline)
        {
            // In-progress link, e.g. [text, or [text]
            linkDelimiterInline.Remove();
        }
        else if (leafBlock.Inline?.LastChild is LinkInline linkInline)
        {
            // In-progress link, e.g. [text](http://
            if (!linkInline.IsClosed)
            {
                linkInline.Remove();
            }
        }

        static bool IsEmphasisStart(ReadOnlySpan<char> text)
        {
            return text is "*" or "**" or "***" or "_" or "__" or "___";
        }
    }
}
