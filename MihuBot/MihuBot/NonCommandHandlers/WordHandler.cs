using SharpCollections.Generic;

namespace MihuBot.NonCommandHandlers;

public sealed class WordHandler : NonCommandHandler
{
    private readonly CompactPrefixTree<Func<MessageContext, Task>> _wordHandlers;

    public WordHandler()
    {
        _wordHandlers = new CompactPrefixTree<Func<MessageContext, Task>>(ignoreCase: true);

        foreach (string word in new[] { "cock", "penis", "dick", "dicks", "wiener", "glizzy" })
        {
            _wordHandlers.Add(word, async ctx => await ctx.Channel.TriggerTypingAsync());
        }

        var jacobCooldown = new CooldownTracker(TimeSpan.FromHours(1), cooldownTolerance: 0, adminOverride: false);

        foreach (string word in new[] { "jacob", "jacob's" })
        {
            _wordHandlers.Add(word, async ctx =>
            {
                if (jacobCooldown.TryEnter(ctx.AuthorId))
                {
                    await ctx.Message.AddReactionAsync(Emotes.RegionalIndicator_K);
                }
            });
        }
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        var handlers = GetWordHandlersForText(ctx.Content);

        return handlers is null
            ? Task.CompletedTask
            : HandleAsyncCore();

        async Task HandleAsyncCore()
        {
            foreach (var handler in handlers)
            {
                await handler(ctx);
            }
        }
    }

    private List<Func<MessageContext, Task>> GetWordHandlersForText(string text)
    {
        List<Func<MessageContext, Task>> list = null;

        int space = -1;
        do
        {
            int next = text.IndexOf(' ', space + 1);
            if (next == -1)
                next = text.Length;

            ReadOnlySpan<char> word = text.AsSpan(space + 1, next - space - 1);

            if (_wordHandlers.TryMatchLongest(word, out var match))
            {
                string trimmed = word.ToString()
                    .Trim('\'', '"', ',', '.', '!', '#', '?', '\r', '\n', '\t');

                if (trimmed.Length == match.Key.Length)
                    (list ??= new List<Func<MessageContext, Task>>()).Add(match.Value);
            }

            space = next;
        }
        while (space + 1 < text.Length);

        return list?.Unique().ToList();
    }
}
