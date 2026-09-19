using System.Net;
using System.Reflection;
using Discord;
using Discord.Net;
using MihuBot.Discord.Commands;

namespace MihuBot.Tests;

public sealed class BlackjackMessageTests
{
    [Fact]
    public async Task InitialBoardIsSentOnceAndLaterRoundsAndRefreshesEditIt()
    {
        int sends = 0;
        int edits = 0;
        var updates = new List<MessageProperties>();
        IUserMessage message = Fake<IUserMessage>((method, args) =>
        {
            Assert.Equal(nameof(IUserMessage.ModifyAsync), method.Name);
            edits++;
            var properties = new MessageProperties();
            ((Action<MessageProperties>)args[0]!)(properties);
            updates.Add(properties);
            return Task.CompletedTask;
        });
        IMessageChannel channel = Fake<IMessageChannel>((method, args) =>
        {
            Assert.Equal(nameof(IMessageChannel.SendFilesAsync), method.Name);
            sends++;
            int mentionsIndex = Array.FindIndex(method.GetParameters(), p => p.Name == "allowedMentions");
            var mentions = Assert.IsType<AllowedMentions>(args[mentionsIndex]);
            Assert.Equal(AllowedMentions.None.AllowedTypes, mentions.AllowedTypes);
            return Task.FromResult(message);
        });

        using var initial = new FileAttachment(new MemoryStream([1, 2, 3]), "round-1.png");
        IUserMessage current = await BlackjackCommand.UpdateBoardMessageAsync(null!, channel, [initial],
            [new EmbedBuilder().WithImageUrl("attachment://round-1.png").Build()], new ComponentBuilder().Build(),
            _ => Assert.Fail("No board was deleted."));

        foreach (string filename in new[] { "round-1-updated.png", "round-2.png", "round-2-refreshed.png" })
        {
            using var attachment = new FileAttachment(new MemoryStream([4, 5, 6]), filename);
            Embed[] embeds = [new EmbedBuilder().WithImageUrl($"attachment://{filename}").Build()];
            MessageComponent components = new ComponentBuilder().WithButton("Deal", filename).Build();
            current = await BlackjackCommand.UpdateBoardMessageAsync(current, channel, [attachment], embeds, components,
                _ => Assert.Fail("No board was deleted."));

            Assert.Same(message, current);
            MessageProperties update = updates[^1];
            Assert.Equal(embeds, update.Embeds.Value);
            Assert.Same(components, update.Components.Value);
            Assert.Equal(filename, Assert.Single(update.Attachments.Value).FileName);
            Assert.Equal(AllowedMentions.None.AllowedTypes, update.AllowedMentions.Value.AllowedTypes);
        }

        Assert.Equal(1, sends);
        Assert.Equal(3, edits);
    }

    [Fact]
    public async Task DeletedBoardIsReplacedAndUploadStreamsAreRewound()
    {
        int sends = 0;
        var deletedIds = new List<ulong>();
        using var first = new FileAttachment(new MemoryStream([1, 2, 3]), "dealer.png");
        using var second = new FileAttachment(new MemoryStream([4, 5]), "player.png");
        FileAttachment[] attachments = [first, second];
        IUserMessage replacement = Fake<IUserMessage>((_, _) => throw new InvalidOperationException());
        IUserMessage deleted = Fake<IUserMessage>((method, _) =>
        {
            if (method.Name == "get_Id")
            {
                return 123ul;
            }

            Assert.Equal(nameof(IUserMessage.ModifyAsync), method.Name);

            foreach (FileAttachment attachment in attachments)
            {
                attachment.Stream.Position = attachment.Stream.Length;
            }

            return Task.FromException(new HttpException(HttpStatusCode.NotFound, null, DiscordErrorCode.UnknownMessage));
        });
        IMessageChannel channel = Fake<IMessageChannel>((method, args) =>
        {
            Assert.Equal(nameof(IMessageChannel.SendFilesAsync), method.Name);
            sends++;
            var uploads = Assert.IsAssignableFrom<IEnumerable<FileAttachment>>(args[0]);
            Assert.All(uploads, file => Assert.Equal(0, file.Stream.Position));
            return Task.FromResult(replacement);
        });

        IUserMessage result = await BlackjackCommand.UpdateBoardMessageAsync(deleted, channel, attachments, [],
            new ComponentBuilder().Build(), deletedIds.Add);

        Assert.Same(replacement, result);
        Assert.Equal(new ulong[] { 123 }, deletedIds);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherEditFailuresDoNotCreateAnotherBoard(HttpStatusCode status)
    {
        var failure = new HttpException(status, null);
        IUserMessage message = Fake<IUserMessage>((method, _) =>
        {
            Assert.Equal(nameof(IUserMessage.ModifyAsync), method.Name);
            return Task.FromException(failure);
        });
        IMessageChannel channel = Fake<IMessageChannel>((_, _) => throw new InvalidOperationException("Must not send another message."));

        HttpException actual = await Assert.ThrowsAsync<HttpException>(() =>
            BlackjackCommand.UpdateBoardMessageAsync(message, channel, [], [], new ComponentBuilder().Build(),
                _ => Assert.Fail("Only confirmed deleted messages may be replaced.")));

        Assert.Same(failure, actual);
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        T instance = DispatchProxy.Create<T, DiscordProxy>();
        ((DiscordProxy)(object)instance).Handler = handler;
        return instance;
    }

    public class DiscordProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => throw new NotSupportedException();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return Handler(targetMethod, args ?? []);
        }
    }
}
