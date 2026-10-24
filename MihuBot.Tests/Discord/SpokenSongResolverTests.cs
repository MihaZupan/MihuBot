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
        using var chat = new TestChatClient(Search("Darude Sandstorm"), new SpokenSongMatch(0));
        List<(ulong GuildId, string Message)> logs = [];
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult(new[] { video }), _ => chat,
            (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded for a convincing match."),
            (guildId, message) => logs.Add((guildId, message)));

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Same(video, result.Video);
        Assert.Empty(result.Error);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal(42ul, log.GuildId);
            Assert.Contains("song resolution", log.Message, StringComparison.Ordinal);
            Assert.Contains("Hey MihuBot play Darood sandstom", log.Message, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(chat.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents),
            content => content is DataContent);
    }

    [Fact]
    public async Task AmbiguousSearchUsesAudioOnceThenRetriesAndValidatesCorrectedNames()
    {
        IVideo wrong = Video("Unrelated first result", "Another artist");
        IVideo corrected = Video("Darude - Sandstorm", "Darude");
        List<string> searches = [];
        List<(ulong GuildId, string Message)> logs = [];
        using var chat = new TestChatClient(Search("Darood sandstom"), new SpokenSongMatch(null),
            new SpokenSongIdentification("Darude", "Sandstorm", true), Search("Darude Sandstorm"), new SpokenSongMatch(0));
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
            Assert.Equal(3, logs.Count);
            string audioLog = logs[^1].Message;
            Assert.Contains("speech audio and transcript", audioLog, StringComparison.Ordinal);
            Assert.Contains(transcript, audioLog, StringComparison.Ordinal);
            Assert.Contains("\"AudioBytes\":32044", audioLog, StringComparison.Ordinal);
            Assert.Contains("\"AudioSeconds\":1", audioLog, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(wav), audioLog, StringComparison.Ordinal);
            return Task.FromResult("The speaker requested Darude, Sandstorm.");
        }, (guildId, message) => logs.Add((guildId, message)));

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Same(corrected, result.Video);
        Assert.Equal(["Darood sandstom", "Darude Sandstorm"], searches);
        Assert.Equal(1, audioCalls);
        Assert.Equal(5, chat.Requests.Count);
        Assert.Equal(6, logs.Count);
        Assert.All(logs, log => Assert.Equal(42ul, log.GuildId));
        Assert.Contains("song resolution", logs[0].Message, StringComparison.Ordinal);
        Assert.Contains("song resolution", logs[3].Message, StringComparison.Ordinal);
        Assert.Contains("The speaker requested Darude, Sandstorm.", logs[3].Message, StringComparison.Ordinal);
        Assert.Contains("song resolution", logs[4].Message, StringComparison.Ordinal);
        Assert.Contains("Darude Sandstorm", logs[4].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(chat.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents),
            content => content is DataContent);
    }

    [Fact]
    public async Task FailedRetryDoesNotTakeTheFirstResultOrUploadAudioAgain()
    {
        IVideo wrong = Video("Unrelated", "Another artist");
        using var chat = new TestChatClient(Search("Darood sandstom"), new SpokenSongMatch(null),
            new SpokenSongIdentification("Darude", "Sandstorm", true), Search("Darude Sandstorm"), new SpokenSongMatch(null));
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
        using var chat = new TestChatClient(Search("Darood sandstom"), new SpokenSongMatch(null), new SpokenSongIdentification(null, null, false));
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
        using var chat = new TestChatClient(Search("Darude Sandstorm"), new SpokenSongMatch(null));
        List<string> logs = [];
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult<IVideo[]>([]), _ => chat,
            (_, _, _, _) => throw new InvalidOperationException("No usable request audio should be uploaded."),
            (_, message) => logs.Add(message));
        SpokenAudioCommand command = QueueCommand(expired: expired);

        if (!expired)
        {
            command = command with { AudioClip = null };
        }

        SpokenSongResolution result = await resolver.ResolveAsync(42, command, CancellationToken.None);

        Assert.Null(result.Video);
        Assert.Contains("repeat", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(2, logs.Count);
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
        using var chat = new TestChatClient(Search("Darude Sandstorm"));
        var resolver = new SpokenSongResolver((_, _) => throw new IOException("Search unavailable"),
            _ => chat,
            (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task ModelChoosesSearchQueriesAndCanSelectAnEarlierResultAfterRefining()
    {
        IVideo wrong = Video("Unrelated", "Another artist");
        IVideo selected = Video("Darude - Sandstorm", "Darude");
        List<string> searches = [];
        using var cancellation = new CancellationTokenSource();
        using var chat = new TestChatClient(Search("Darude Sandstorm official"), Search("Sandstorm original"), new SpokenSongMatch(1));
        var resolver = new SpokenSongResolver((query, token) =>
        {
        Assert.Equal(cancellation.Token, token);
        searches.Add(query);
        return Task.FromResult(searches.Count == 1 ? new[] { wrong, selected } : new[] { wrong });
        }, _ => chat, (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), cancellation.Token);

        Assert.Same(selected, result.Video);
        Assert.Equal(["Darude Sandstorm official", "Sandstorm original"], searches);
        Assert.All(chat.Tokens, token => Assert.Equal(cancellation.Token, token));
        FunctionResultContent[] results = chat.Requests[^1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("search_Darude Sandstorm official", results[0].CallId);
        JsonElement first = JsonSerializer.SerializeToElement(results[0].Result);
        Assert.Equal(0, first[0].GetProperty("index").GetInt32());
        Assert.Equal(1, first[1].GetProperty("index").GetInt32());
        Assert.Equal(selected.Title, first[1].GetProperty("title").GetString());
        Assert.Equal("Darude", first[1].GetProperty("channel").GetString());
        Assert.Equal(selected.Url, first[1].GetProperty("url").GetString());
        Assert.Equal(240, first[1].GetProperty("durationSeconds").GetDouble());
        JsonElement second = JsonSerializer.SerializeToElement(results[1].Result);
        Assert.Equal(2, second[0].GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task EmptySearchResultsCanBeRefinedByTheModel()
    {
        IVideo video = Video("Darude - Sandstorm", "Darude");
        int searches = 0;
        using var chat = new TestChatClient(Search("Darood sandstom"), Search("Darude Sandstorm"), new SpokenSongMatch(0));
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult<IVideo[]>(++searches == 1 ? [] : [video]), _ => chat,
        (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        SpokenSongResolution result = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Same(video, result.Video);
        Assert.Equal(2, searches);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ModelCannotSelectAVideoNotReturnedBySearch(int index)
    {
        using var chat = new TestChatClient(Search("Song"), new SpokenSongMatch(index));
        var resolver = new SpokenSongResolver((_, _) => Task.FromResult(new[] { Video("Song", "Artist") }), _ => chat,
        (_, _, _, _) => throw new InvalidOperationException("Invalid selection must not trigger an audio upload."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task ModelCannotSelectACandidateWithoutSearching()
    {
        using var chat = new TestChatClient(new SpokenSongMatch(0));
        var resolver = new SpokenSongResolver((_, _) => throw new InvalidOperationException("Search should not run."), _ => chat,
        (_, _, _, _) => throw new InvalidOperationException("Invalid selection must not trigger an audio upload."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSearchQueriesNeverReachYouTube(bool tooLong)
    {
        using var chat = new TestChatClient(Search(tooLong ? new string('x', 301) : " "));
        int searches = 0;
        var resolver = new SpokenSongResolver((_, _) =>
        {
        searches++;
        return Task.FromResult<IVideo[]>([]);
        }, _ => chat, (_, _, _, _) => throw new InvalidOperationException("Invalid queries must not trigger an audio upload."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
        Assert.Equal(0, searches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchLimitDisablesToolsAndRejectsFurtherCalls(bool exceedLimit)
    {
        using var chat = new TestChatClient(Search("first"), Search("second"), Search("third"),
        exceedLimit ? Search("fourth") : new SpokenSongMatch(2));
        int searches = 0;
        IVideo video = Video("Song", "Artist");
        var resolver = new SpokenSongResolver((_, _) =>
        {
        searches++;
        return Task.FromResult(new[] { video });
        }, _ => chat, (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        if (exceedLimit)
        {
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None));
        }
        else
        {
        Assert.Same(video, (await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None)).Video);
        }

        Assert.Equal(3, searches);
        Assert.Equal([true, true, true, false], chat.ToolsAvailable);
    }

    [Fact]
    public async Task CanceledSearchDoesNotContinueToSelectionOrAudioUpload()
    {
        using var cancellation = new CancellationTokenSource();
        using var chat = new TestChatClient(Search("Song"));
        var resolver = new SpokenSongResolver((_, _) =>
        {
        cancellation.Cancel();
        return Task.FromResult(new[] { Video("Song", "Artist") });
        }, _ => chat, (_, _, _, _) => throw new InvalidOperationException("Canceled search must not trigger an audio upload."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(42, QueueCommand(), cancellation.Token));
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task ResponsesSdkPreservesReasoningAndCacheableHistoryAcrossBatchedSearches()
    {
        int requests = 0;
        int clients = 0;
        JsonElement[] previousMessages = [];
        string? tools = null;
        string? schema = null;
        string? model = null;
        List<string> queries = [];
        IVideo video = Video("Darude - Sandstorm", "Darude");
        using var http = new HttpClient(new TestHttpHandler(async (request, token) =>
        {
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            JsonElement root = data.RootElement;
            Assert.Equal("https://test.openai.azure.com/openai/v1/responses", request.RequestUri?.AbsoluteUri);
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Contains(root.GetProperty("include").EnumerateArray(), property => property.GetString() == "reasoning.encrypted_content");
            JsonElement function = Assert.Single(root.GetProperty("tools").EnumerateArray());
            Assert.Equal("Search", function.GetProperty("name").GetString());
            Assert.True(function.GetProperty("parameters").GetProperty("properties").TryGetProperty("query", out _));
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
            string currentTools = root.GetProperty("tools").GetRawText();
            string currentSchema = root.GetProperty("text").GetProperty("format").GetRawText();
            string currentModel = root.GetProperty("model").GetRawText();
            tools ??= currentTools;
            schema ??= currentSchema;
            model ??= currentModel;
            Assert.Equal(tools, currentTools);
            Assert.Equal(schema, currentSchema);
            Assert.Equal(model, currentModel);
            JsonElement[] messages = root.GetProperty("input").EnumerateArray().Select(message => message.Clone()).ToArray();
            Assert.True(messages.Length > previousMessages.Length);

            for (int index = 0; index < previousMessages.Length; index++)
            {
                Assert.Equal(previousMessages[index].GetRawText(), messages[index].GetRawText());
            }

            previousMessages = messages;
            string response;

            if (++requests == 1)
            {
                response = """
                    {"id":"resp_search","object":"response","created_at":1,"model":"gpt-6-luna",
                     "status":"completed","store":false,"output":[
                       {"id":"rs_search","type":"reasoning","summary":[],"encrypted_content":"opaque-test-reasoning"},
                       {"id":"fc_search","type":"function_call","call_id":"call_search","name":"Search",
                        "arguments":"{\"query\":\"Darude Sandstorm\"}","status":"completed"},
                       {"id":"fc_refine","type":"function_call","call_id":"call_refine","name":"Search",
                        "arguments":"{\"query\":\"Darude Sandstorm official audio\"}","status":"completed"}
                     ]}
                    """;
            }
            else
            {
                JsonElement reasoning = Assert.Single(messages, message => message.GetProperty("type").GetString() == "reasoning");
                Assert.Equal("opaque-test-reasoning", reasoning.GetProperty("encrypted_content").GetString());
                JsonElement[] results = messages.Where(message => message.GetProperty("type").GetString() == "function_call_output").ToArray();
                Assert.Equal(2, results.Length);
                Assert.Equal("call_search", results[0].GetProperty("call_id").GetString());
                Assert.Equal("call_refine", results[1].GetProperty("call_id").GetString());

                for (int index = 0; index < results.Length; index++)
                {
                    using var result = JsonDocument.Parse(results[index].GetProperty("output").GetString()!);
                    Assert.Equal(video.Title, result.RootElement[0].GetProperty("title").GetString());
                    Assert.Equal(index, result.RootElement[0].GetProperty("index").GetInt32());
                }

                response = """
                    {"id":"resp_match","object":"response","created_at":1,"model":"gpt-6-luna",
                     "status":"completed","store":false,"output":[
                       {"id":"msg_match","type":"message","role":"assistant","status":"completed",
                        "content":[{"type":"output_text","text":"{\"candidateIndex\":1}","annotations":[]}]}
                     ]}
                    """;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }));
        var client = new OpenAI.OpenAIClient(new ApiKeyCredential("test-key"), new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.openai.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http)
        });
#pragma warning disable OPENAI001
        using var chat = client.GetResponsesClient().AsIChatClient(OpenAIService.DefaultModel);
#pragma warning restore OPENAI001
        var resolver = new SpokenSongResolver((query, _) =>
        {
            queries.Add(query);
            return Task.FromResult(new[] { video });
        }, _ =>
        {
            clients++;
            return chat;
        }, (_, _, _, _) => throw new InvalidOperationException("Audio should not be uploaded."));

        SpokenSongResolution resolution = await resolver.ResolveAsync(42, QueueCommand(), CancellationToken.None);

        Assert.Same(video, resolution.Video);
        Assert.Equal(2, requests);
        Assert.Equal(1, clients);
        Assert.Equal(["Darude Sandstorm", "Darude Sandstorm official audio"], queries);
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

    private static FunctionCallContent Search(string query) =>
        new("search_" + query, "Search", new Dictionary<string, object?> { ["query"] = query });

    private sealed class TestChatClient(params object[] responses) : IChatClient
    {
        private readonly Queue<object> _responses = new(responses);
        public List<ChatMessage[]> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<bool> ToolsAvailable { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            Tokens.Add(cancellationToken);
            ToolsAvailable.Add(options?.Tools is { Count: > 0 } && options.ToolMode != ChatToolMode.None);
            object response = _responses.Dequeue();
            return Task.FromResult(new ChatResponse(response is FunctionCallContent call
                ? new ChatMessage(ChatRole.Assistant, [call])
                : new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(response))));
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
