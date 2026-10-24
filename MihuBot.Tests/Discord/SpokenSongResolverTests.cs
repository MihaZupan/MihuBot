using System.Buffers.Binary;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MihuBot.Discord.Audio;
using MihuBot.Helpers.AI;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;

namespace MihuBot.Tests.Discord;

public sealed class SpokenSongResolverTests
{
    [Fact]
    public void WavEncodingPreservesPcmTimingWithoutDuplicatingOverlapOrWhisperPadding()
    {
        SpeechSegment first = Segment(0, 3, 0.25f) with { Samples = [0.25f, 0.5f, 0.75f, 0, 0] };
        SpeechSegment overlap = Segment(2, 2, 0) with { Samples = [0.75f, -0.25f] };
        SpeechSegment afterGap = Segment(6, 2, 0) with { Samples = [-1, 1] };
        SpokenAudioClip clip = Assert.IsType<SpokenAudioClip>(SpokenAudioClip.Create([first, overlap, afterGap], "MihuBot queue song"));
        byte[] wav = clip.ToWav();

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        short[] samples = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2))).ToArray();
        Assert.Equal([8192, 16384, 24576, -8192, 0, 0, -32768, 32767], samples.Select(sample => (int)sample).ToArray());
    }

    [Fact]
    public async Task QueueClipIncludesWakeAndFollowUpButDoesNotSurviveACompletedRequest()
    {
        var context = new SpokenAudioContext((text, _) => Task.FromResult<SpokenAudioCommand?>(
            text.Contains("Marigold", StringComparison.Ordinal) ? new(SpokenAudioAction.Queue, "Marigold") : null));
        Assert.Null(await context.ParseAsync(Segment(0, 16_000, 0.25f), "Hey MihuBot queue song", CancellationToken.None));
        SpokenAudioCommand? command = await context.ParseAsync(Segment(16_000, 16_000, 0.5f), "Marigold", CancellationToken.None);
        SpokenAudioClip clip = Assert.IsType<SpokenAudioClip>(command?.AudioClip);
        byte[] wav = clip.ToWav();

        Assert.Equal("Hey MihuBot queue song\nMarigold", clip.Transcript);
        Assert.Equal(64_044, wav.Length);
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)));
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + 32_000)));
        Assert.DoesNotContain("AudioClip", JsonSerializer.Serialize(command), StringComparison.Ordinal);

        SpokenAudioCommand? next = await context.ParseAsync(Segment(32_000, 16_000, -0.25f),
            "MihuBot play Marigold", CancellationToken.None);
        byte[] nextWav = Assert.IsType<SpokenAudioClip>(next?.AudioClip).ToWav();
        Assert.Equal(32_044, nextWav.Length);
        Assert.Equal(-8192, BinaryPrimitives.ReadInt16LittleEndian(nextWav.AsSpan(44)));
    }

    [Fact]
    public async Task AudioIsIsolatedBetweenSpeakersAndNeverAttachedToNonQueueCommands()
    {
        var first = new SpokenAudioContext((_, _) => Task.FromResult<SpokenAudioCommand?>(new(SpokenAudioAction.Skip)));
        var second = new SpokenAudioContext((_, _) => Task.FromResult<SpokenAudioCommand?>(new(SpokenAudioAction.Queue, "Song")));
        SpokenAudioCommand? skip = await first.ParseAsync(Segment(0, 16_000, 0.75f), "MihuBot skip", CancellationToken.None);
        SpokenAudioCommand? queue = await second.ParseAsync(Segment(0, 16_000, -0.5f), "MihuBot play song", CancellationToken.None);

        Assert.Null(skip?.AudioClip);
        byte[] wav = Assert.IsType<SpokenAudioClip>(queue?.AudioClip).ToWav();
        Assert.All(Enumerable.Range(0, 16_000),
            i => Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2))));
    }

    [Fact]
    public void AHardCutWithoutAnUtteranceEndIsNotTreatedAsACompleteRequestClip()
    {
        SpeechSegment first = Segment(0, 192_000, 0.25f) with { IsFinal = false };
        SpeechSegment ending = Segment(160_000, 64_000, 0.5f) with { IsContinuation = true };

        Assert.Null(SpokenAudioClip.Create([first], "MihuBot queue song"));
        Assert.NotNull(SpokenAudioClip.Create([first, ending], "MihuBot queue song"));
    }

    [Fact]
    public async Task IncompleteOverlongAudioIsNotAttachedForCloudFallback()
    {
        var context = new SpokenAudioContext((text, _) => Task.FromResult<SpokenAudioCommand?>(
            text.Contains("Marigold", StringComparison.Ordinal) ? new(SpokenAudioAction.Queue, "Marigold") : null));
        await context.ParseAsync(Segment(0, 192_000, 0.25f), "MihuBot queue song", CancellationToken.None);
        SpokenAudioCommand? command = await context.ParseAsync(Segment(160_000, 192_000, 0.5f),
            "Marigold", CancellationToken.None);

        Assert.NotNull(command);
        Assert.Null(command.AudioClip);
    }

    [Fact]
    public async Task ConvincingSearchMatchDoesNotUploadAudio()
    {
        IVideo video = Video("Darude - Sandstorm", "Darude");
        using var chat = new TestChatClient(new SpokenSongMatch(0));
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult(new[] { video }), _ => chat,
            (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded for a convincing match."));

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Same(video, result.Video);
        Assert.Empty(result.Error);
        Assert.Single(chat.Requests);
        Assert.All(chat.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents),
            content => Assert.IsType<TextContent>(content));
    }

    [Fact]
    public async Task AmbiguousSearchUsesAudioOnceThenRetriesAndValidatesCorrectedNames()
    {
        IVideo wrong = Video("Unrelated first result", "Another artist");
        IVideo corrected = Video("Darude - Sandstorm", "Darude");
        List<string> searches = [];
        using var chat = new TestChatClient(new SpokenSongMatch(null),
            new SpokenSongIdentification("Darude", "Sandstorm", true), new SpokenSongMatch(0));
        int audioCalls = 0;
        var resolver = new SpokenSongResolver((query, _) =>
        {
            searches.Add(query);
            return Task.FromResult(new[] { searches.Count == 1 ? wrong : corrected });
        }, _ => chat, (transcript, candidates, wav, _) =>
        {
            audioCalls++;
            Assert.Contains("Darood", transcript, StringComparison.Ordinal);
            Assert.Same(wrong, Assert.Single(candidates));
            Assert.Equal(32_044, wav.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            return Task.FromResult("The speaker requested Darude, Sandstorm.");
        });

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Same(corrected, result.Video);
        Assert.Equal(["Darood sandstom", "Darude Sandstorm"], searches);
        Assert.Equal(1, audioCalls);
        Assert.Equal(3, chat.Requests.Count);
        Assert.All(chat.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents),
            content => Assert.IsType<TextContent>(content));
    }

    [Fact]
    public async Task FailedRetryDoesNotTakeTheFirstResultOrUploadAudioAgain()
    {
        IVideo wrong = Video("Unrelated", "Another artist");
        using var chat = new TestChatClient(new SpokenSongMatch(null),
            new SpokenSongIdentification("Darude", "Sandstorm", true), new SpokenSongMatch(null));
        int audioCalls = 0;
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult(new[] { wrong }), _ => chat, (_, _, _, _) =>
        {
            audioCalls++;
            return Task.FromResult("Darude, Sandstorm.");
        });

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Null(result.Video);
        Assert.Contains("repeat", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, audioCalls);
    }

    [Fact]
    public async Task UnclearAudioEvidenceDoesNotTriggerAnotherSearchOrPickACandidate()
    {
        using var chat = new TestChatClient(new SpokenSongMatch(null), new SpokenSongIdentification(null, null, false));
        int searches = 0;
        var resolver = new SpokenSongResolver((_, _) =>
        {
            searches++;
            return Task.FromResult(new[] { Video("Unrelated result", "Another artist") });
        }, _ => chat, (_, _, _, _) => Task.FromResult("The artist and title cannot be identified reliably."));

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Null(result.Video);
        Assert.Contains("repeat", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task MalformedMatchResponseDoesNotTriggerAnAudioUpload()
    {
        using JsonDocument missingIndex = JsonDocument.Parse("{}");
        using var chat = new TestChatClient(missingIndex.RootElement.Clone());
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult(new[] { Video("Song", "Artist") }), _ => chat,
            (_, _, _, _) => throw new InvalidOperationException("Malformed text output must not cause audio upload."));

        await Assert.ThrowsAsync<JsonException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrExpiredClipNeverUploadsAudio(bool expired)
    {
        using var chat = new TestChatClient();
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult<IVideo[]>([]), _ => chat,
            (_, _, _, _) => throw new InvalidOperationException("No usable request audio should be uploaded."));
        SpokenAudioCommand command = QueueCommand(expired: expired);

        if (!expired)
        {
            command = command with { AudioClip = null };
        }

        SpokenSongResolution result = await resolver.ResolveAsync(42, command, CancellationToken.None);

        Assert.Null(result.Video);
        Assert.Contains("repeat", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(chat.Requests);
    }

    [Theory]
    [InlineData((int)SpokenAudioAction.Skip)]
    [InlineData((int)SpokenAudioAction.Pause)]
    [InlineData((int)SpokenAudioAction.None)]
    public async Task NonQueueIntentsCannotInvokeSongSearchOrAudioFallback(int action)
    {
        var resolver = new SpokenSongResolver((_, _) => throw new InvalidOperationException("Search should not run."),
            _ => throw new InvalidOperationException("Text AI should not run."),
            (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(42, QueueCommand() with { Action = (SpokenAudioAction)action }, CancellationToken.None));
    }

    [Fact]
    public async Task SearchFailureIsNotMisclassifiedAsAReasonToUploadAudio()
    {
        var resolver = new SpokenSongResolver((_, _) => throw new IOException("Search unavailable"),
            _ => throw new InvalidOperationException("Text AI should not run."),
            (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task AudioSdkRequestUsesWavTextOutputAndNoStoredResponse()
    {
        byte[] wav = Assert.IsType<SpokenAudioClip>(QueueCommand().AudioClip).ToWav();
        using var http = new HttpClient(new TestHttpHandler(async (request, token) =>
        {
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            JsonElement root = data.RootElement;
            Assert.Equal(OpenAIService.DefaultAudioModel, root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("text", Assert.Single(root.GetProperty("modalities").EnumerateArray()).GetString());
            JsonElement parts = root.GetProperty("messages")[1].GetProperty("content");
            JsonElement audio = parts.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_audio").GetProperty("input_audio");
            Assert.Equal("wav", audio.GetProperty("format").GetString());
            Assert.Equal(wav, Convert.FromBase64String(audio.GetProperty("data").GetString()!));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"chat_test","object":"chat.completion","created":1,"model":"gpt-audio-1.5",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"Darude, Sandstorm."},"finish_reason":"stop"}]}
                    """, Encoding.UTF8, "application/json")
            };
        }));
        var client = new OpenAI.OpenAIClient(new ApiKeyCredential("test-key"), new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.openai.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http),
            ClientLoggingOptions = new() { EnableMessageContentLogging = false }
        }).GetChatClient(OpenAIService.DefaultAudioModel);

        string evidence = await SpokenSongResolver.HearAsync(client, "MihuBot play Darood", [], wav, CancellationToken.None);

        Assert.Equal("Darude, Sandstorm.", evidence);
    }

    [Fact]
    public async Task AudioProviderErrorsDoNotExposeResponseBodiesToOrdinaryLogs()
    {
        using var http = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"sensitive-audio-payload","type":"invalid_request_error","code":"bad_audio"}}""",
                Encoding.UTF8, "application/json")
        })));
        var client = new OpenAI.OpenAIClient(new ApiKeyCredential("test-key"), new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.openai.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http)
        }).GetChatClient(OpenAIService.DefaultAudioModel);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            SpokenSongResolver.HearAsync(client, "MihuBot play song", [],
                Assert.IsType<SpokenAudioClip>(QueueCommand().AudioClip).ToWav(), CancellationToken.None));

        Assert.Contains("400", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-audio-payload", error.ToString(), StringComparison.Ordinal);
    }

    private static SpokenAudioCommand QueueCommand(bool expired = false)
    {
        SpeechSegment segment = Segment(0, 16_000, 0.25f);

        if (expired)
        {
            segment = segment with { Timestamp = Stopwatch.GetTimestamp() - 21 * Stopwatch.Frequency };
        }

        return new(SpokenAudioAction.Queue, "Darood sandstom")
        {
            AudioClip = SpokenAudioClip.Create([segment], "Hey MihuBot play Darood sandstom")
        };
    }

    private static SpeechSegment Segment(long start, int count, float sample)
    {
        long now = Stopwatch.GetTimestamp();
        return new(Enumerable.Repeat(sample, count).ToArray(), start, start + count, now, now, now);
    }

    private static IVideo Video(string title, string channel) =>
        new Video("afqQwmXTcPA", title, new Author("UC0123456789012345678901", channel), DateTimeOffset.UtcNow,
            "", TimeSpan.FromMinutes(4), [], [], new Engagement(0, 0, 0));

    private sealed class TestChatClient(params object[] responses) : IChatClient
    {
        private readonly Queue<object> _responses = new(responses);
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(_responses.Dequeue()))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
