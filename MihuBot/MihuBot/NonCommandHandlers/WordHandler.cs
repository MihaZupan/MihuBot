﻿using Discord;
using MihuBot.Helpers;
using SharpCollections.Generic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class WordHandler : NonCommandHandler
    {
        private readonly CompactPrefixTree<Func<MessageContext, Task>> _wordHandlers;

        public WordHandler()
        {
            _wordHandlers = new CompactPrefixTree<Func<MessageContext, Task>>(ignoreCase: true);

            _wordHandlers.Add("banana", BananaReactionHandler);

            foreach (string word in new[] { "cock", "penis" })
            {
                _wordHandlers.Add(word, TypingResponseHandler);
            }

            _wordHandlers.Add("stinky", StinkyHandler);

            static async Task BananaReactionHandler(MessageContext ctx) => await ctx.Message.AddReactionAsync(Emotes.PudeesJammies);
            static async Task TypingResponseHandler(MessageContext ctx) => await ctx.Channel.TriggerTypingAsync();
            static async Task StinkyHandler(MessageContext ctx)
            {
                if (Rng.Chance(25))
                {
                    await ctx.ReplyAsync(MentionUtils.MentionUser(KnownUsers.Jordan));
                }
            }
        }

        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            var handlers = GetWordHandlersForText(ctx.Content);

            if (handlers != null)
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

                if (_wordHandlers.TryMatchExact(text.AsSpan(space + 1, next - space - 1), out var match))
                {
                    (list ??= new List<Func<MessageContext, Task>>()).Add(match.Value);
                }

                space = next;
            }
            while (space + 1 < text.Length);

            return list?.Unique().ToList();
        }
    }
}
