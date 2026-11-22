using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Discord.Commands;

namespace MihuBot.Tests.Discord;

public sealed class FlipCommandTests
{
    [Fact]
    public async Task ClassifiesAllOptionsAndKeepsOnlyHealthyChoices()
    {
        string[] options = ["go to bed", "keep gaming", "have dinner", "skip dinner"];
        using var chat = new TestChatClient((messages, chatOptions, token) =>
        {
            ChatMessage[] sent = messages.ToArray();
            Assert.Equal(ChatRole.System, sent[0].Role);
            Assert.Equal(ChatRole.User, sent[1].Role);
            Assert.Equal(options, JsonSerializer.Deserialize<string[]>(sent[1].Text!));
            Assert.IsType<ChatResponseFormatJson>(chatOptions?.ResponseFormat);
            Assert.True(token.CanBeCanceled);

            return Respond("""{"Options":[{"Flagged":true,"Healthy":true},{"Flagged":false,"Healthy":false},{"Flagged":true,"Healthy":true},{"Flagged":true,"Healthy":false}]}""");
        });

        string[] eligible = await FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None);

        Assert.Equal(["go to bed", "have dinner"], eligible);
    }

    [Theory]
    [InlineData("dangerous drug use")]
    [InlineData("self-harm")]
    [InlineData("impaired driving")]
    public async Task DangerousChoicesPreferSafeAlternatives(string dangerousOption)
    {
        string[] options = [dangerousOption, "watch a movie"];
        using var chat = new TestChatClient((messages, _, _) =>
        {
            string prompt = messages.First().Text!;
            Assert.Contains(dangerousOption, prompt, StringComparison.Ordinal);
            Assert.Contains("safe alternatives have Flagged true and Healthy true", prompt, StringComparison.Ordinal);

            return Respond("""{"Options":[{"Flagged":true,"Healthy":false},{"Flagged":true,"Healthy":true}]}""");
        });

        Assert.Equal(["watch a movie"], await FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"Options":[{"Flagged":false,"Healthy":false},{"Flagged":false,"Healthy":false}]}""")]
    [InlineData("""{"Options":[{"Flagged":true,"Healthy":false},{"Flagged":true,"Healthy":false}]}""")]
    [InlineData("""{"Options":[{"Flagged":false,"Healthy":true},{"Flagged":false,"Healthy":false}]}""")]
    [InlineData("""{"Options":[{"Flagged":true,"Healthy":true}]}""")]
    [InlineData("""{"Options":null}""")]
    [InlineData("""{"Options":[null,null]}""")]
    public async Task IrrelevantOrInvalidClassificationsPreserveAllOptions(string response)
    {
        string[] options = ["sleep", "\u200Bplay"];
        using var chat = new TestChatClient((_, _, _) => Respond(response));

        Assert.Same(options, await FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
    }

    [Fact]
    public void CommandCanBeConstructedWithoutAI()
    {
        using var provider = new ServiceCollection().AddSingleton<FlipCommand>().BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<FlipCommand>());
    }

    [Fact]
    public async Task MalformedResponsesPropagateToTheCaller()
    {
        string[] options = ["sleep", "play"];
        using var chat = new TestChatClient((_, _, _) => Respond("not JSON"));

        await Assert.ThrowsAsync<JsonException>(() => FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
    }

    [Fact]
    public async Task NullResponsesPropagateToTheCaller()
    {
        string[] options = ["sleep", "play"];
        using var chat = new TestChatClient((_, _, _) => Respond("null"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
    }

    [Fact]
    public async Task SynchronousChatFailuresPropagateToTheCaller()
    {
        string[] options = ["sleep", "play"];
        using var chat = new TestChatClient((_, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<InvalidOperationException>(() => FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledRequestsPropagateToTheCaller(bool canceled)
    {
        string[] options = ["eat", "skip dinner"];
        using var chat = new TestChatClient((_, _, _) => canceled
            ? Task.FromCanceled<ChatResponse>(new CancellationToken(true))
            : Task.FromException<ChatResponse>(new HttpRequestException()));

        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None));
        }
    }

    [Fact]
    public async Task TimeoutCancelsTheChatRequestAndPropagatesToTheCaller()
    {
        string[] options = ["sleep", "play"];
        using var chat = new TestChatClient(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return await Respond("""{"Options":[]}""");
        });

        Task<string[]> request = FlipCommand.GetEligibleOptionsAsync(options, chat, CancellationToken.None, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static Task<ChatResponse> Respond(string json) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            respond(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
