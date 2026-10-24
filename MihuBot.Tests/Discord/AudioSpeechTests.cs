using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Discord;
using Discord.Audio;
using Discord.Audio.Streams;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using MihuBot.Configuration;
using MihuBot.Discord.Audio;
using MihuBot.Helpers;

namespace MihuBot.Tests.Discord;

public sealed class AudioSpeechTests
{
    [Fact]
    public async Task SpokenCommandFailurePingsOnlyTheRequester()
    {
        IMessageChannel channel = DispatchProxy.Create<IMessageChannel, MessageChannelProxy>();
        var proxy = Assert.IsAssignableFrom<MessageChannelProxy>(channel);
        const string message = "I couldn't find that song. @everyone <@999> <@&123>";

        await AudioPlayer.SendSpokenCommandFailureAsync(channel, 42, message, CancellationToken.None);

        Assert.NotNull(proxy.Arguments);
        Assert.Equal($"<@42>: {message}", proxy.Arguments[0]);
        AllowedMentions mentions = Assert.Single(proxy.Arguments.OfType<AllowedMentions>());
        Assert.Null(mentions.AllowedTypes);
        Assert.Equal([42ul], mentions.UserIds);
        Assert.Empty(mentions.RoleIds);
    }

    [Theory]
    [InlineData(0, "HTTP 403 (Forbidden)")]
    [InlineData(1, "could not be reached")]
    [InlineData(2, "timed out")]
    [InlineData(3, "could not be read or decoded")]
    [InlineData(4, "could not be loaded or played")]
    public async Task PlaybackFailuresShowTheTrackAndSafeReasonWithoutPingingAnyone(int failure, string reason)
    {
        IMessageChannel channel = DispatchProxy.Create<IMessageChannel, MessageChannelProxy>();
        var proxy = Assert.IsAssignableFrom<MessageChannelProxy>(channel);
        await using var source = new QueueSource("Jack Emery - Halo @everyone <@999>", "Artist");
        const string privateDetails = "Private signed URL https://audio.example/?token=secret";
        Exception error = failure switch
        {
            0 => new HttpRequestException(privateDetails, null, HttpStatusCode.Forbidden),
            1 => new HttpRequestException(privateDetails),
            2 => new TimeoutException(privateDetails),
            3 => new IOException(privateDetails),
            _ => new InvalidOperationException(privateDetails)
        };
        using var cancellation = new CancellationTokenSource();

        await AudioPlayer.SendPlaybackFailureAsync(channel, source, new AudioSourceException(source, error), cancellation.Token);

        Assert.NotNull(proxy.Arguments);
        Embed embed = Assert.Single(proxy.Arguments.OfType<Embed>());
        Assert.Equal("Playback failed", embed.Title);
        Assert.Equal(source.Description, embed.Description);
        Assert.Contains(reason, Assert.Single(embed.Fields).Value, StringComparison.Ordinal);
        Assert.DoesNotContain(privateDetails, JsonSerializer.Serialize(embed), StringComparison.Ordinal);
        Assert.Contains("skipped", embed.Footer!.Value.Text, StringComparison.Ordinal);
        AllowedMentions mentions = Assert.Single(proxy.Arguments.OfType<AllowedMentions>());
        Assert.Null(mentions.AllowedTypes);
        Assert.Empty(mentions.UserIds);
        Assert.Empty(mentions.RoleIds);
        Assert.Equal(cancellation.Token, Assert.Single(proxy.Arguments.OfType<RequestOptions>()).CancelToken);
    }

    [Fact]
    public async Task PlaybackFailureNoticesBoundLongTrackTitles()
    {
        IMessageChannel channel = DispatchProxy.Create<IMessageChannel, MessageChannelProxy>();
        var proxy = Assert.IsAssignableFrom<MessageChannelProxy>(channel);
        await using var source = new QueueSource(new string('x', 5000), "Artist");

        await AudioPlayer.SendPlaybackFailureAsync(channel, source, new IOException(), CancellationToken.None);

        Assert.NotNull(proxy.Arguments);
        Assert.Equal(1000, Assert.Single(proxy.Arguments.OfType<Embed>()).Description.Length);
    }

    [Theory]
    [InlineData("Hey MihuBot, could you put on something by a-ha?")]
    [InlineData("Mihu-Bot play the song Take On Me!")]
    [InlineData("Miha Bot pause please")]
    [InlineData("Hey, meehoo bot, pause the music.")]
    [InlineData("Hey me who bot pause the music")]
    [InlineData("\"Hey Me Who Bot,\" Q song take on me.")]
    [InlineData("MihuBot is a music bot")]
    [InlineData("Hey Mi... hu bot, queue Take On Me")]
    [InlineData("Hey Mihu, b-bot, pause")]
    [InlineData("Hey Mihu... bo-bot, resume")]
    public void DetectsBotMentionsLocally(string text)
    {
        Assert.NotNull(SpokenAudioCommand.GetAddressedText(text));
    }

    [Theory]
    [InlineData("queue song Take On Me")]
    [InlineData("Hey bot pause")]
    [InlineData("NotMihuBot skip")]
    [InlineData("MihuBotany is not the bot")]
    public async Task UnaddressedSpeechNeverCallsTheLLM(string text)
    {
        using var chat = new TestChatClient((_, _, _) => throw new InvalidOperationException("Unaddressed speech reached the LLM."));

        Assert.Null(await SpokenAudioCommand.ParseAsync(chat, text, CancellationToken.None,
            logCloudRequest: _ => throw new InvalidOperationException("Unaddressed speech was logged as a cloud request.")));
    }

    [Theory]
    [InlineData("Hey MihuBot, skip.")]
    [InlineData("MihuBot skip one song")]
    [InlineData("MihuBot skip 1 track")]
    [InlineData("MihuBot skip one")]
    [InlineData("MihuBot skip this track")]
    [InlineData("MihuBot skip the current song")]
    [InlineData("MihuBot skip the next track")]
    [InlineData("Hey Miha Bot, please skip the song.")]
    [InlineData("Hey Mihu-Bot, could you skip a track, please?")]
    [InlineData("MihuBot, can you please skip this song?")]
    [InlineData("  HEY MIHUBOT, SKIP TRACK PLEASE!  ")]
    [InlineData("Hey MihuBot\nskip one song")]
    public async Task ClearSingleTrackSkipsDoNotCreateACloudClientOrLogACloudSend(string transcript)
    {
        using var speech = new AudioSpeechService(_ => throw new InvalidOperationException("Transcription should not run."),
            _ => throw new InvalidOperationException("No error expected."),
            createChat: _ => throw new InvalidOperationException("Clear skips must not create a cloud AI client."),
            logCloudRequest: (_, _) => throw new InvalidOperationException("A local skip must not log a cloud send."));

        SpokenAudioCommand? command = await speech.ParseCommandAsync(42, transcript, CancellationToken.None);

        Assert.Equal(new SpokenAudioCommand(SpokenAudioAction.Skip), command);
    }

    [Theory]
    [InlineData("MihuBot pause", (int)SpokenAudioAction.Pause)]
    [InlineData("Miha Bot pause please", (int)SpokenAudioAction.Pause)]
    [InlineData("Hey, meehoo bot, pause the music.", (int)SpokenAudioAction.Pause)]
    [InlineData("MihuBot, could you please pause playback?", (int)SpokenAudioAction.Pause)]
    [InlineData("MihuBot resume", (int)SpokenAudioAction.Resume)]
    [InlineData("Hey Mihu-Bot, please resume the song.", (int)SpokenAudioAction.Resume)]
    [InlineData("MihuBot, would you resume music, please?", (int)SpokenAudioAction.Resume)]
    [InlineData("MihuBot unpause", (int)SpokenAudioAction.Resume)]
    [InlineData("Hey me who bot can you unpause the track please?", (int)SpokenAudioAction.Resume)]
    [InlineData("  HEY MIHUBOT, UNPAUSE!  ", (int)SpokenAudioAction.Resume)]
    [InlineData("Hey MihuBot\nplease unpause the music", (int)SpokenAudioAction.Resume)]
    public async Task ClearPlaybackControlsDoNotCreateACloudClientOrLogACloudSend(string transcript, int action)
    {
        using var speech = new AudioSpeechService(_ => throw new InvalidOperationException("Transcription should not run."),
            _ => throw new InvalidOperationException("No error expected."),
            createChat: _ => throw new InvalidOperationException("Clear playback controls must not create a cloud AI client."),
            logCloudRequest: (_, _) => throw new InvalidOperationException("A local playback control must not log a cloud send."));
        using var chat = new TestChatClient((_, _, _) => throw new InvalidOperationException("Clear playback controls must not reach the LLM."));
        var expected = new SpokenAudioCommand((SpokenAudioAction)action);

        Assert.Equal(expected, await speech.ParseCommandAsync(42, transcript, CancellationToken.None));
        Assert.Equal(expected, await SpokenAudioCommand.ParseAsync(chat, transcript, CancellationToken.None,
            logCloudRequest: _ => throw new InvalidOperationException("A local playback control must not log a cloud send.")));
    }

    [Theory]
    [InlineData("MihuBot skip 3 songs")]
    [InlineData("MihuBot skip two tracks")]
    [InlineData("MihuBot skip 0 songs")]
    [InlineData("MihuBot skip until the track by Darude")]
    [InlineData("MihuBot don't skip this track")]
    [InlineData("MihuBot skip one song and pause")]
    [InlineData("MihuBot skip skip")]
    [InlineData("MihuBot should I skip this track?")]
    [InlineData("If I said MihuBot skip")]
    [InlineData("\"Hey MihuBot skip\"")]
    [InlineData("Hey \"MihuBot skip\"")]
    [InlineData("MihuBot skip the song called One")]
    [InlineData("MihuBot don't pause the music")]
    [InlineData("MihuBot pause and resume")]
    [InlineData("MihuBot pause pause")]
    [InlineData("MihuBot should I resume?")]
    [InlineData("If I said MihuBot unpause")]
    [InlineData("\"Hey MihuBot pause\"")]
    [InlineData("Hey \"MihuBot resume\"")]
    [InlineData("MihuBot resume the song called Pause")]
    [InlineData("MihuBot pause for five minutes")]
    [InlineData("MihuBot unpause after this song")]
    [InlineData("MihuBot play Unpause")]
    public async Task OtherPhrasingStillUsesCloudIntentExtraction(string transcript)
    {
        int calls = 0;
        using var chat = new TestChatClient((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"Action":"None","Query":""}""")));
        });

        Assert.Null(await SpokenAudioCommand.ParseAsync(chat, transcript, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IntentExtractionSendsOnlyBotAddressedTextAndUsesAnEnumSchema()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new SpokenAudioCommand(SpokenAudioAction.Queue, "Take On Me by a-ha");
        List<(ulong GuildId, string Message)> logs = [];
        using var chat = new TestChatClient((messages, options, token) =>
        {
            var log = Assert.Single(logs);
            Assert.Equal(42ul, log.GuildId);
            Assert.Contains("intent extraction", log.Message, StringComparison.Ordinal);
            Assert.Contains("Hey Miha Bot, could you put Take On Me by a-ha on next?", log.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Unrelated conversation", log.Message, StringComparison.Ordinal);
            ChatMessage[] sent = messages.ToArray();
            Assert.Equal(2, sent.Length);
            Assert.Equal(ChatRole.System, sent[0].Role);
            Assert.Contains("untrusted speech", sent[0].Text, StringComparison.Ordinal);
            Assert.Equal(ChatRole.User, sent[1].Role);
            Assert.Equal("Hey Miha Bot, could you put Take On Me by a-ha on next?", sent[1].Text);
            Assert.All(sent.SelectMany(message => message.Contents), content => Assert.IsType<TextContent>(content));
            Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(expected))));
        });

        using var speech = new AudioSpeechService(_ => throw new InvalidOperationException("Transcription should not run."),
            _ => throw new InvalidOperationException("No error expected."), createChat: _ => chat,
            logCloudRequest: (guildId, message) => logs.Add((guildId, message)));
        SpokenAudioCommand? result = await speech.ParseCommandAsync(42,
            "Unrelated conversation. Hey Miha Bot, could you put Take On Me by a-ha on next?", cancellation.Token);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedTranscriptsDoNotLogCloudSends(bool canceled)
    {
        using var cancellation = new CancellationTokenSource();

        if (canceled)
        {
            cancellation.Cancel();
        }

        using var chat = new TestChatClient((_, _, _) => throw new InvalidOperationException("No cloud request expected."));
        string transcript = canceled ? "Hey MihuBot skip" : "Hey MihuBot " + new string('x', 1000);
        List<string> logs = [];

        if (canceled)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SpokenAudioCommand.ParseAsync(chat, transcript, cancellation.Token, logCloudRequest: logs.Add));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SpokenAudioCommand.ParseAsync(chat, transcript, cancellation.Token, logCloudRequest: logs.Add));
        }

        Assert.Empty(logs);
    }

    [Fact]
    public async Task MentionWithoutAnIntentDoesNotExecuteACommand()
    {
        using var chat = new TestChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"Action":"None","Query":""}"""))));

        Assert.Null(await SpokenAudioCommand.ParseAsync(chat, "MihuBot is a music bot.", CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"Action":"Queue","Query":""}""")]
    [InlineData("""{"Action":"Skip","Query":"another command"}""")]
    [InlineData("""{"Action":999,"Query":""}""")]
    [InlineData("""{"Action":"Queue","Query":null}""")]
    [InlineData("""{"Action":"Skip","Query":"","Count":0}""")]
    [InlineData("""{"Action":"Skip","Query":"","Count":-3}""")]
    [InlineData("""{"Action":"Queue","Query":"A song","Count":3}""")]
    [InlineData("""{"Action":"Skip","Query":"","TargetIndex":0}""")]
    [InlineData("""{"Action":"SkipUntil","Query":"Darude","TargetIndex":0}""")]
    [InlineData("""{"Action":"Volume","Query":""}""")]
    [InlineData("""{"Action":"Volume","Query":"","VolumePercent":-1}""")]
    [InlineData("""{"Action":"Volume","Query":"","VolumePercent":101}""")]
    [InlineData("""{"Action":"Volume","Query":"","VolumePercent":-101,"IsRelativeVolume":true}""")]
    [InlineData("""{"Action":"Volume","Query":"","VolumePercent":101,"IsRelativeVolume":true}""")]
    [InlineData("""{"Action":"Pause","Query":"","VolumePercent":30}""")]
    [InlineData("""{"Action":"None","Query":"","IsRelativeVolume":true}""")]
    [InlineData("""{"Action":"Manage","Query":"rewritten request"}""")]
    [InlineData("""{"Action":"Manage","Query":"Hey MihuBot skip 2 songs","Count":2}""")]
    public async Task InvalidAIIntentsFailExplicitly(string json)
    {
        using var chat = new TestChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SpokenAudioCommand.ParseAsync(chat, "Hey MihuBot skip 2 songs", CancellationToken.None));
    }

    [Theory]
    [InlineData("MihuBot set volume to thirty percent, queue Sandstorm, then move it next")]
    [InlineData("MihuBot remove the second upcoming song and pause")]
    public async Task MultiActionRequestsRetainTheOriginalSpeechForPlaybackTools(string transcript)
    {
        var expected = new SpokenAudioCommand(SpokenAudioAction.Manage, transcript);
        using var chat = new TestChatClient((messages, _, _) =>
        {
            Assert.Contains("multiple requested actions", messages.First().Text, StringComparison.Ordinal);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(expected))));
        });

        Assert.Equal(expected, await SpokenAudioCommand.ParseAsync(chat, transcript, CancellationToken.None));
    }

    [Theory]
    [InlineData("MihuBot set volume to thirty percent", 30, false)]
    [InlineData("MihuBot set volume to zero percent", 0, false)]
    [InlineData("MihuBot set volume to one hundred percent", 100, false)]
    [InlineData("MihuBot turn it up by twenty percent", 20, true)]
    [InlineData("MihuBot turn it down by twenty percent", -20, true)]
    [InlineData("MihuBot louder", 10, true)]
    [InlineData("MihuBot quieter", -10, true)]
    public async Task VolumeIntentRetainsTheRequestedLevelAndAdjustmentMode(string transcript, int percent, bool relative)
    {
        var expected = new SpokenAudioCommand(SpokenAudioAction.Volume, VolumePercent: percent, IsRelativeVolume: relative);
        using var chat = new TestChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(expected)))));
        using var speech = new AudioSpeechService(_ => throw new InvalidOperationException("Transcription should not run."),
            _ => throw new InvalidOperationException("No error expected."), createChat: _ => chat);

        Assert.Equal(expected, await speech.ParseCommandAsync(42, transcript, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, 30, false, 0.3f)]
    [InlineData(0.8f, 0, false, 0f)]
    [InlineData(0.2f, 100, false, 1f)]
    [InlineData(null, 10, true, 0.5f)]
    [InlineData(0.4f, -10, true, 0.3f)]
    [InlineData(0.95f, 10, true, 1f)]
    [InlineData(0.05f, -10, true, 0f)]
    public async Task VolumeChangesUseSavedSettingsAndClampRelativeAdjustments(float? initial, int percent, bool relative, float expected)
    {
        using var files = new ModelFiles();
        string path = Path.Combine(files.DirectoryPath, "audio-settings.json");
        var store = new SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>>(path);
        var settings = new GuildAudioSettings { Volume = initial, UnderlyingStore = store };
        store.Modify(values => values.Add(42, settings));

        await settings.ChangeVolumeAsync(percent, relative);

        Assert.Equal(expected, Assert.IsType<float>(settings.Volume), precision: 5);
        var reopened = new SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>>(path);
        Assert.Equal(expected, Assert.IsType<float>(reopened.Query(values => values[42].Volume)), precision: 5);
    }

    [Fact]
    public async Task IntentExtractionSupportsAnExplicitSkipCount()
    {
        using var chat = new TestChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"Action":"Skip","Query":"","Count":3,"TargetIndex":null}"""))));

        SpokenAudioCommand? command = await SpokenAudioCommand.ParseAsync(chat, "Hey MihuBot skip 3 songs", CancellationToken.None);

        Assert.Equal(new SpokenAudioCommand(SpokenAudioAction.Skip, Count: 3), command);
    }

    [Fact]
    public async Task SkipUntilUsesQueueMetadataAndRetainsTheSelectedSourceIdentity()
    {
        await using var first = new QueueSource("Another song", "Another artist");
        await using var destination = new QueueSource("Sandstorm", "Darude");
        using var chat = new TestChatClient((messages, _, _) =>
        {
            ChatMessage[] sent = messages.ToArray();
            Assert.Equal(3, sent.Length);
            Assert.Equal("Hey MihuBot skip until the track by Darude", sent[1].Text);
            string metadata = sent[2].Text;
            using var data = JsonDocument.Parse(metadata.Substring(metadata.IndexOf('[')));
            Assert.Equal("Sandstorm", data.RootElement[1].GetProperty("Title").GetString());
            Assert.Equal("Darude", data.RootElement[1].GetProperty("Channel").GetString());
            Assert.Equal(1, data.RootElement[1].GetProperty("Index").GetInt32());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"Action":"SkipUntil","Query":"Darude","Count":1,"TargetIndex":1}""")));
        });

        SpokenAudioCommand? command = await SpokenAudioCommand.ParseAsync(chat,
            "Hey MihuBot skip until the track by Darude", CancellationToken.None, [first, destination]);

        Assert.NotNull(command);
        Assert.Equal(SpokenAudioAction.SkipUntil, command.Action);
        Assert.Same(destination, command.TargetSource);
        Assert.DoesNotContain("TargetSource", JsonSerializer.Serialize(command), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task SkipUntilRejectsIndicesOutsideTheProvidedSnapshot(int index)
    {
        await using var first = new QueueSource("First", "Artist");
        await using var second = new QueueSource("Sandstorm", "Darude");
        using var chat = new TestChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(
                new SpokenAudioCommand(SpokenAudioAction.SkipUntil, "Darude", TargetIndex: index))))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SpokenAudioCommand.ParseAsync(chat,
            "Hey MihuBot skip until Darude", CancellationToken.None, [first, second]));
    }

    [Fact]
    public async Task SkipUntilCanReportNoMatchWithoutInventingATarget()
    {
        using var chat = new TestChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"Action":"SkipUntil","Query":"Darude","Count":1,"TargetIndex":null}"""))));

        SpokenAudioCommand? command = await SpokenAudioCommand.ParseAsync(chat,
            "Hey MihuBot skip until the track by Darude", CancellationToken.None, []);

        Assert.NotNull(command);
        Assert.Equal(SpokenAudioAction.SkipUntil, command.Action);
        Assert.Null(command.TargetSource);
    }

    [Fact]
    public void SeparateUtterancesAreNotDeduplicatedBecauseOfPreRollOverlap()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();
        segmenter.AddPcm(Pcm(50, 4096, 4096), now);
        segmenter.AddPcm(Pcm(65, 0, 0), now);
        segmenter.AddPcm(Pcm(50, 4096, 4096), now);
        segmenter.AddPcm(Pcm(60, 0, 0), now);
        var skip = new SpokenAudioCommand(SpokenAudioAction.Skip);

        Assert.Equal(2, segments.Count);
        Assert.True(segments[1].StartSample < segments[0].EndSample);
        Assert.False(AudioVoiceListener.IsRepeatedWindowCommand(segments[1], skip, skip, segments[0].EndSample));
    }

    [Fact]
    public void ContinuousWindowOverlapStillSuppressesTheSameCommandButNotADifferentCount()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        segmenter.AddPcm(Pcm(1500, 4096, 4096), Stopwatch.GetTimestamp());
        var skip = new SpokenAudioCommand(SpokenAudioAction.Skip);

        Assert.True(AudioVoiceListener.IsRepeatedWindowCommand(segments[1], skip, skip, segments[0].EndSample));
        Assert.False(AudioVoiceListener.IsRepeatedWindowCommand(
            segments[1], skip with { Count = 3 }, skip, segments[0].EndSample));
    }

    [Fact]
    public void ContinuousWindowOverlapDistinguishesVolumeLevelsAndAdjustmentModes()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        segmenter.AddPcm(Pcm(1500, 4096, 4096), Stopwatch.GetTimestamp());
        var volume = new SpokenAudioCommand(SpokenAudioAction.Volume, VolumePercent: 10, IsRelativeVolume: true);

        Assert.True(AudioVoiceListener.IsRepeatedWindowCommand(segments[1], volume, volume, segments[0].EndSample));
        Assert.False(AudioVoiceListener.IsRepeatedWindowCommand(
            segments[1], volume with { VolumePercent = -10 }, volume, segments[0].EndSample));
        Assert.False(AudioVoiceListener.IsRepeatedWindowCommand(
            segments[1], volume with { IsRelativeVolume = false }, volume, segments[0].EndSample));
    }

    [Theory]
    [InlineData("Hey MihuBot")]
    [InlineData("Hey MihuBot, queue song")]
    public async Task BotMentionAndFollowingSongNameFormOneRequest(string initial)
    {
        List<string> sent = [];
        var expected = new SpokenAudioCommand(SpokenAudioAction.Queue, "Take On Me by a-ha");
        using var chat = new TestChatClient((messages, _, _) =>
        {
            string text = messages.Last().Text;
            sent.Add(text);
            var command = text.Contains("Take On Me", StringComparison.Ordinal)
                ? expected : new SpokenAudioCommand(SpokenAudioAction.None);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(command))));
        });
        var context = new SpokenAudioContext((text, token) => SpokenAudioCommand.ParseAsync(chat, text, token));

        Assert.Null(await context.ParseAsync(ContextSegment(0, 1), initial, CancellationToken.None));
        Assert.Equal(expected, await context.ParseAsync(ContextSegment(3, 4), "Take On Me by a-ha", CancellationToken.None));
        Assert.Equal(initial + "\nTake On Me by a-ha", sent[1]);

        Assert.Null(await context.ParseAsync(ContextSegment(5, 6), "queue another song", CancellationToken.None));
        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData("Hey Mi...", "hu bot, queue song Take On Me")]
    [InlineData("Hey Mihu...", "b-bot, queue song Take On Me")]
    [InlineData("Hey me...", "who bot, queue song Take On Me")]
    public async Task SplitAndStutteredNamesAreJoinedLocallyBeforeIntentExtraction(string first, string second)
    {
        List<string> sent = [];
        var expected = new SpokenAudioCommand(SpokenAudioAction.Queue, "Take On Me");
        var context = new SpokenAudioContext((text, _) =>
        {
            sent.Add(text);
            return Task.FromResult<SpokenAudioCommand?>(expected);
        });

        Assert.Null(await context.ParseAsync(ContextSegment(0, 1), first, CancellationToken.None));
        Assert.Empty(sent);
        Assert.Equal(expected, await context.ParseAsync(ContextSegment(3, 4), second, CancellationToken.None));
        Assert.Contains("Take On Me", Assert.Single(sent), StringComparison.Ordinal);
        Assert.Contains("\n", sent[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7.9, true)]
    [InlineData(8.1, false)]
    public async Task WakeContextExpiresBasedOnCaptureTiming(double gap, bool accepted)
    {
        List<string> sent = [];
        var context = new SpokenAudioContext((text, _) =>
        {
            sent.Add(text);
            return Task.FromResult<SpokenAudioCommand?>(null);
        });
        await context.ParseAsync(ContextSegment(0, 1), "Hey MihuBot", CancellationToken.None);
        await context.ParseAsync(ContextSegment(1 + gap, 2 + gap), "queue Take On Me", CancellationToken.None);

        Assert.Equal(accepted ? 2 : 1, sent.Count);
    }

    [Fact]
    public async Task AnIncompleteFollowUpDoesNotKeepTheWakeWindowOpenIndefinitely()
    {
        List<string> sent = [];
        var context = new SpokenAudioContext((text, _) =>
        {
            sent.Add(text);
            return Task.FromResult<SpokenAudioCommand?>(null);
        });
        await context.ParseAsync(ContextSegment(0, 1), "Hey MihuBot", CancellationToken.None);
        await context.ParseAsync(ContextSegment(4, 5), "I was thinking", CancellationToken.None);
        await context.ParseAsync(ContextSegment(10, 11), "queue Take On Me", CancellationToken.None);

        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain(sent, text => text.Contains("Take On Me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongFollowUpIsAcceptedWhenItStartsWithinTheWakeWindow()
    {
        List<string> sent = [];
        var context = new SpokenAudioContext((text, _) =>
        {
            sent.Add(text);
            return Task.FromResult<SpokenAudioCommand?>(null);
        });
        await context.ParseAsync(ContextSegment(0, 1), "Hey MihuBot", CancellationToken.None);
        await context.ParseAsync(ContextSegment(4, 16), "queue a song with a long title", CancellationToken.None);

        Assert.Equal(2, sent.Count);
        Assert.Equal("Hey MihuBot\nqueue a song with a long title", sent[1]);
    }

    [Fact]
    public async Task SpeakersCannotCompleteEachOthersRequests()
    {
        List<string> sent = [];

        Task<SpokenAudioCommand?> Parse(string text, CancellationToken _)
        {
            sent.Add(text);
            return Task.FromResult<SpokenAudioCommand?>(null);
        }

        var first = new SpokenAudioContext(Parse);
        var second = new SpokenAudioContext(Parse);
        await first.ParseAsync(ContextSegment(0, 1), "Hey MihuBot", CancellationToken.None);
        await second.ParseAsync(ContextSegment(2, 3), "queue Take On Me", CancellationToken.None);
        Assert.Single(sent);
        await first.ParseAsync(ContextSegment(2, 3), "queue Take On Me", CancellationToken.None);

        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task AccumulatedContextIsBoundedAndClearedOnOverflow()
    {
        int calls = 0;
        var context = new SpokenAudioContext((_, _) =>
        {
            calls++;
            return Task.FromResult<SpokenAudioCommand?>(null);
        });
        await context.ParseAsync(ContextSegment(0, 1), "Hey MihuBot", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.ParseAsync(ContextSegment(2, 3), new string('x', 1000), CancellationToken.None));
        Assert.Null(await context.ParseAsync(ContextSegment(4, 5), "queue Take On Me", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ShortPausesWithinASongNameDoNotSplitTheUtterance()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();
        segmenter.AddPcm(Pcm(50, 4096, 4096), now);
        segmenter.AddPcm(Pcm(40, 0, 0), now);
        Assert.Empty(segments);
        segmenter.AddPcm(Pcm(50, 4096, 4096), now);
        segmenter.AddPcm(Pcm(60, 0, 0), now);

        Assert.Equal(64_000, Assert.Single(segments).Samples.Length);
    }

    [Fact]
    public void SegmentsSpeechAndConvertsStereo48KhzToMono16Khz()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();
        byte[] pcm = Pcm(50, 4096, 0);
        segmenter.AddPcm(pcm.AsSpan(0, 4), now);
        segmenter.AddPcm(pcm.AsSpan(4), now);
        segmenter.AddPcm(Pcm(60, 0, 0), now);

        SpeechSegment segment = Assert.Single(segments);
        Assert.Equal(35_200, segment.Samples.Length);
        Assert.All(segment.Samples.Take(16_000), sample => Assert.Equal(0.0625f, sample));
        Assert.All(segment.Samples.Skip(16_000), sample => Assert.Equal(0f, sample));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(3836)]
    [InlineData(3844)]
    [InlineData(int.MaxValue)]
    public void ResamplingPreservesSamplesAcrossPacketAndFrameBoundaries(int packetBytes)
    {
        const int SampleCount = 3840;
        byte[] pcm = new byte[SampleCount * 6 * sizeof(short)];
        float[] expected = new float[SampleCount];
        int sum = 0;

        for (int i = 0; i < SampleCount * 6; i++)
        {
            short sample = (short)((i * 7919 % 65536) - 32768);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short)), sample);
            sum += sample;

            if (i % 6 == 5)
            {
                expected[i / 6] = sum / (6f * 32768);
                sum = 0;
            }
        }

        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();

        for (int offset = 0; offset < pcm.Length;)
        {
            int length = Math.Min(packetBytes, pcm.Length - offset);
            segmenter.AddPcm(pcm.AsSpan(offset, length), now);
            offset += length;
        }

        segmenter.FlushIfIdle(now + 2 * Stopwatch.Frequency);

        SpeechSegment segment = Assert.Single(segments);
        Assert.Equal(0, segment.StartSample);
        Assert.Equal(SampleCount, segment.EndSample);
        Assert.Equal(expected, segment.Samples.Take(SampleCount));
        Assert.All(segment.Samples.Skip(SampleCount), sample => Assert.Equal(0f, sample));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(13)]
    public void PreRollPreservesSampleOrderAcrossCircularBufferBoundaries(int preRollFrames)
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();

        for (int i = 0; i < preRollFrames; i++)
        {
            short amplitude = (short)((i + 1) * 16);
            segmenter.AddPcm(Pcm(1, amplitude, amplitude), now);
        }

        segmenter.AddPcm(Pcm(10, 4096, 4096), now);
        segmenter.FlushIfIdle(now + 2 * Stopwatch.Frequency);

        float[] expected = Enumerable.Range(Math.Max(0, preRollFrames - 10), Math.Min(preRollFrames, 10))
            .SelectMany(i => Enumerable.Repeat((i + 1) * 16 / 32768f, 320))
            .Concat(Enumerable.Repeat(0.125f, 3200))
            .ToArray();
        SpeechSegment segment = Assert.Single(segments);

        Assert.Equal(Math.Max(0, preRollFrames - 10) * 320, segment.StartSample);
        Assert.Equal((preRollFrames + 10) * 320, segment.EndSample);
        Assert.Equal(expected, segment.Samples.Take(expected.Length));
        Assert.All(segment.Samples.Skip(expected.Length), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void SilenceAndBriefNoiseDoNotReachWhisper()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();
        segmenter.AddPcm(Pcm(50, 0, 0), now);
        segmenter.AddPcm(Pcm(2, 4096, 4096), now);
        segmenter.AddPcm(Pcm(30, 0, 0), now);
        segmenter.FlushIfIdle(now + 2 * Stopwatch.Frequency);

        Assert.Empty(segments);
    }

    [Fact]
    public void MissingSilencePacketsFlushSpeechAndDoNotReuseOldAudio()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();
        segmenter.AddPcm(Pcm(50, 4096, 4096), now);
        segmenter.FlushIfIdle(now + Stopwatch.Frequency);
        Assert.Empty(segments);
        segmenter.FlushIfIdle(now + 2 * Stopwatch.Frequency);

        Assert.Single(segments);
        Assert.Equal(now, segments[0].SpeechStartTimestamp);
        Assert.Equal(now, segments[0].SpeechEndTimestamp);
        Assert.Equal(now + 2 * Stopwatch.Frequency, segments[0].Timestamp);
        segmenter.AddPcm(Pcm(50, 8192, 8192), now + 3 * Stopwatch.Frequency);
        segmenter.FlushIfIdle(now + 5 * Stopwatch.Frequency);

        Assert.Equal(2, segments.Count);
        Assert.All(segments[1].Samples, sample => Assert.Equal(0.25f, sample));
        Assert.Equal(segments[0].EndSample, segments[1].StartSample);
    }

    [Fact]
    public void ContinuousSpeechUsesBoundedOverlappingChunks()
    {
        List<SpeechSegment> segments = [];
        var segmenter = new SpeechSegmenter(segments.Add);
        long now = Stopwatch.GetTimestamp();
        segmenter.AddPcm(Pcm(1500, 4096, 4096), now);
        segmenter.FlushIfIdle(now + 2 * Stopwatch.Frequency);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, segment => Assert.InRange(segment.Samples.Length, 16_000, SpeechSegmenter.MaxSamples));
        Assert.Equal(segments[0].EndSample - SpeechSegmenter.OverlapSamples, segments[1].StartSample);
        Assert.Equal(segments[1].EndSample - SpeechSegmenter.OverlapSamples, segments[2].StartSample);
        Assert.Equal(30 * 16_000, segments[2].EndSample);
    }

    [Fact]
    public async Task AllSpeakersShareOneModelAndOneSequentialWorker()
    {
        var started = Signal();
        var release = Signal();
        var completed = Signal();
        ConcurrentQueue<int> calls = [];
        ConcurrentQueue<int> recipients = [];
        int factoryCalls = 0;
        var transcriber = new FakeTranscriber(async (samples, token) =>
        {
            calls.Enqueue((int)samples[0]);

            if (samples[0] == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }

            return "Hey MihuBot pause";
        });

        await using (var worker = new Worker(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return transcriber;
        }))
        {
            await worker.ReadyAsync();
            Assert.True(worker.Service.TryEnqueue(Request(1, onTranscript: (_, _) =>
            {
                recipients.Enqueue(1);
                return Task.CompletedTask;
            })));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(worker.Service.TryEnqueue(Request(2, onTranscript: (_, _) =>
            {
                recipients.Enqueue(2);
                completed.SetResult();
                return Task.CompletedTask;
            })));
            Assert.Equal([1], calls.ToArray());
            release.SetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([1, 2], calls.ToArray());
            Assert.Equal([1, 2], recipients.ToArray());
            Assert.Equal(1, factoryCalls);
            Assert.Empty(worker.Errors);
        }

        Assert.Equal(1, transcriber.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhisperMetricsMeasureInferenceAndTrackQueuedAudioIncludingDiscardedSegments(bool canceled)
    {
        var started = Signal();
        var release = Signal();
        var handling = Signal();
        var finishHandling = Signal();
        var completed = Signal();
        using var departed = new CancellationTokenSource();
        ConcurrentQueue<string> logs = [];
        await using var worker = new Worker(() => new FakeTranscriber(async (samples, token) =>
        {
            if (samples.Length == SpeechSegmenter.SampleRate)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }

            return "Local transcript";
        }), logWhisperProcessing: logs.Enqueue);
        await worker.ReadyAsync();

        Assert.True(worker.Service.TryEnqueue(Request(1, onTranscript: async (_, token) =>
        {
            handling.SetResult();
            await finishHandling.Task.WaitAsync(token);
        }) with
        {
            Samples = new float[SpeechSegmenter.SampleRate],
            Timestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long observedStart = Stopwatch.GetTimestamp();

        if (canceled)
        {
            departed.Cancel();
        }

        Assert.True(worker.Service.TryEnqueue(Request(2) with
        {
            Samples = new float[2 * SpeechSegmenter.SampleRate],
            CancellationToken = departed.Token,
            Timestamp = Stopwatch.GetTimestamp() - (canceled ? 0 : 21 * Stopwatch.Frequency)
        }));
        Assert.True(worker.Service.TryEnqueue(Request(3, onTranscript: (_, _) =>
        {
            completed.SetResult();
            return Task.CompletedTask;
        }) with { Samples = new float[3 * SpeechSegmenter.SampleRate] }));
        TimeSpan minimumProcessing = Stopwatch.GetElapsedTime(observedStart);
        release.SetResult();
        await handling.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string firstLog = Assert.Single(logs);
        Assert.StartsWith("[AudioWhisper] ", firstLog, StringComparison.Ordinal);
        using var first = JsonDocument.Parse(firstLog.Substring("[AudioWhisper] ".Length));
        JsonElement metrics = first.RootElement;
        double processing = metrics.GetProperty("ProcessingSeconds").GetDouble();
        Assert.True(metrics.GetProperty("Completed").GetBoolean());
        Assert.Equal(1, metrics.GetProperty("ClipSeconds").GetDouble());
        Assert.True(processing >= minimumProcessing.TotalSeconds);
        Assert.Equal(processing, metrics.GetProperty("RealTimeFactor").GetDouble());
        Assert.True(metrics.GetProperty("QueueWaitSeconds").GetDouble() >= 1);
        Assert.Equal(2, metrics.GetProperty("QueuedSegments").GetInt32());
        Assert.Equal(5, metrics.GetProperty("QueuedSeconds").GetDouble());

        finishHandling.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, logs.Count);
        using var last = JsonDocument.Parse(logs.Last().Substring("[AudioWhisper] ".Length));
        Assert.Equal(3, last.RootElement.GetProperty("ClipSeconds").GetDouble());
        Assert.Equal(last.RootElement.GetProperty("ProcessingSeconds").GetDouble() / 3,
            last.RootElement.GetProperty("RealTimeFactor").GetDouble());
        Assert.Equal(0, last.RootElement.GetProperty("QueuedSegments").GetInt32());
        Assert.Equal(0, last.RootElement.GetProperty("QueuedSeconds").GetDouble());
        Assert.Empty(worker.Errors);
    }

    [Fact]
    public async Task CanceledAndExpiredSpeechCannotExecuteCommands()
    {
        var completed = Signal();
        using var departed = new CancellationTokenSource();
        departed.Cancel();
        ConcurrentQueue<int> calls = [];
        ConcurrentQueue<string> notifications = [];
        await using var worker = new Worker(() => new FakeTranscriber((samples, _) =>
        {
            calls.Enqueue((int)samples[0]);
            return Task.FromResult("Hey MihuBot skip");
        }));
        await worker.ReadyAsync();

        Assert.True(worker.Service.TryEnqueue(Request(1) with { CancellationToken = departed.Token }));
        Assert.True(worker.Service.TryEnqueue(Request(2, onError: message =>
        {
            notifications.Enqueue(message);
            return Task.CompletedTask;
        }) with { Timestamp = Stopwatch.GetTimestamp() - 21 * Stopwatch.Frequency }));
        Assert.True(worker.Service.TryEnqueue(Request(3, onTranscript: (_, _) =>
        {
            completed.SetResult();
            return Task.CompletedTask;
        })));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([3], calls.ToArray());
        Assert.Contains("expired", Assert.Single(notifications), StringComparison.Ordinal);
        Assert.Empty(worker.Errors);
    }

    [Fact]
    public async Task QueueIsBoundedAndStoppingCancelsInferenceBeforeDisposal()
    {
        var started = Signal();
        ConcurrentQueue<string> logs = [];
        var transcriber = new FakeTranscriber(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "";
        });

        await using (var worker = new Worker(() => transcriber, logWhisperProcessing: logs.Enqueue))
        {
            await worker.ReadyAsync();
            Assert.True(worker.Service.TryEnqueue(Request(1)));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (int i = 0; i < AudioSpeechService.QueueCapacity; i++)
            {
                Assert.True(worker.Service.TryEnqueue(Request(i)));
            }

            Assert.False(worker.Service.TryEnqueue(Request(100)));
        }

        Assert.Equal(1, transcriber.DisposeCalls);
        using var metrics = JsonDocument.Parse(Assert.Single(logs).Substring("[AudioWhisper] ".Length));
        Assert.False(metrics.RootElement.GetProperty("Completed").GetBoolean());
        Assert.Equal(AudioSpeechService.QueueCapacity, metrics.RootElement.GetProperty("QueuedSegments").GetInt32());
        Assert.Equal((double)AudioSpeechService.QueueCapacity / SpeechSegmenter.SampleRate,
            metrics.RootElement.GetProperty("QueuedSeconds").GetDouble());
    }

    [Fact]
    public async Task FailedRecognitionIsReportedAndDoesNotStopTheWorker()
    {
        var completed = Signal();
        ConcurrentQueue<string> notifications = [];
        ConcurrentQueue<string> logs = [];
        await using var worker = new Worker(() => new FakeTranscriber((samples, _) =>
            samples[0] == 1 ? throw new IOException("Recognition failure") : Task.FromResult("MihuBot resume")),
            logWhisperProcessing: logs.Enqueue);
        await worker.ReadyAsync();

        Assert.True(worker.Service.TryEnqueue(Request(1, onError: message =>
        {
            notifications.Enqueue(message);
            return Task.CompletedTask;
        })));
        Assert.True(worker.Service.TryEnqueue(Request(2, onTranscript: (_, _) =>
        {
            completed.SetResult();
            return Task.CompletedTask;
        })));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<IOException>(Assert.Single(worker.Errors));
        Assert.Single(notifications);
        Assert.Equal(2, logs.Count);
        using var failed = JsonDocument.Parse(logs.First().Substring("[AudioWhisper] ".Length));
        Assert.False(failed.RootElement.GetProperty("Completed").GetBoolean());
    }

    [Fact]
    public async Task LeavingDuringRecognitionCancelsTheResultAndReleasesTheWorker()
    {
        using var departed = new CancellationTokenSource();
        var started = Signal();
        var completed = Signal();
        bool staleCommandExecuted = false;
        await using var worker = new Worker(() => new FakeTranscriber(async (samples, token) =>
        {
            if (samples[0] == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return "MihuBot skip";
        }));
        await worker.ReadyAsync();
        Assert.True(worker.Service.TryEnqueue(Request(1, onTranscript: (_, _) =>
        {
            staleCommandExecuted = true;
            return Task.CompletedTask;
        }) with { CancellationToken = departed.Token }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.Service.TryEnqueue(Request(2, onTranscript: (_, _) =>
        {
            completed.SetResult();
            return Task.CompletedTask;
        })));

        departed.Cancel();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(staleCommandExecuted);
        Assert.Empty(worker.Errors);
    }

    [Fact]
    public async Task RecognitionDeadlineCancelsSlowWorkWithoutExecutingAStaleCommand()
    {
        var time = new FakeTimeProvider();
        var started = Signal();
        var completed = Signal();
        bool executed = false;
        await using var worker = new Worker(() => new FakeTranscriber(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "MihuBot skip";
        }), timeProvider: time);
        await worker.ReadyAsync();

        Assert.True(worker.Service.TryEnqueue(Request(1, onTranscript: (_, _) =>
        {
            executed = true;
            return Task.CompletedTask;
        }, onError: message =>
        {
            Assert.Contains("timed out", message, StringComparison.Ordinal);
            completed.SetResult();
            return Task.CompletedTask;
        }) with { Timestamp = time.GetTimestamp() }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(AudioSpeechService.MaxRequestAge);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(executed);
        Assert.Empty(worker.Errors);
    }

    [Fact]
    public async Task CommandHandlingOutlivesSpeechExpiryButStillHasABoundedDeadline()
    {
        var time = new FakeTimeProvider();
        var started = Signal();
        var timedOut = Signal();
        CancellationToken commandToken = default;
        await using var worker = new Worker(() => new FakeTranscriber((_, _) => Task.FromResult("MihuBot queue several songs")),
            timeProvider: time);
        await worker.ReadyAsync();

        Assert.True(worker.Service.TryEnqueue(Request(1, onTranscript: async (_, token) =>
        {
            commandToken = token;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, onError: message =>
        {
            Assert.Contains("timed out", message, StringComparison.Ordinal);
            timedOut.SetResult();
            return Task.CompletedTask;
        }) with { Timestamp = time.GetTimestamp() }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(AudioSpeechService.MaxRequestAge + TimeSpan.FromSeconds(1));

        Assert.False(commandToken.IsCancellationRequested);
        Assert.False(timedOut.Task.IsCompleted);
        time.Advance(AudioSpeechService.MaxCommandDuration - AudioSpeechService.MaxRequestAge);
        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(commandToken.IsCancellationRequested);
        Assert.Empty(worker.Errors);
    }

    [Fact]
    public async Task ModelLoadFailureDisablesRecognitionExplicitly()
    {
        await using var worker = new Worker(() => throw new FileNotFoundException("Model missing"));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ReadyAsync());

        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.IsType<FileNotFoundException>(Assert.Single(worker.Errors));
        Assert.False(worker.Service.TryEnqueue(Request(1)));
    }

    [Fact]
    public async Task RuntimeThreadChangesReachTheSameTranscriberOnTheNextRequest()
    {
        var configuration = new TestConfiguration();
        configuration.Set(null, "AudioSpeech.Threads", "2");
        var first = Signal();
        var second = Signal();
        int factories = 0;
        var transcriber = new FakeTranscriber((_, _) => Task.FromResult(""));
        await using var worker = new Worker(() =>
        {
            factories++;
            return transcriber;
        }, () => AudioSpeechService.GetThreads(configuration));
        await worker.ReadyAsync();
        Assert.True(worker.Service.TryEnqueue(Request(1, onTranscript: (_, _) =>
        {
            first.SetResult();
            return Task.CompletedTask;
        })));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        configuration.Set(null, "AudioSpeech.Threads", "16");
        Assert.True(worker.Service.TryEnqueue(Request(2, onTranscript: (_, _) =>
        {
            second.SetResult();
            return Task.CompletedTask;
        })));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([2, 16], transcriber.Threads.ToArray());
        Assert.Equal(1, factories);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    public void InvalidRuntimeThreadCountsAreNotSilentlyDefaulted(string value)
    {
        var configuration = new TestConfiguration();
        configuration.Set(null, "AudioSpeech.Threads", value);

        Assert.Throws<InvalidOperationException>(() => AudioSpeechService.GetThreads(configuration));
    }

    [Fact]
    public async Task FramesBufferedBeforeListeningStartsAreDiscarded()
    {
        using var input = new InputStream();
        using var cancellation = new CancellationTokenSource();

        for (int i = 0; i < 101; i++)
        {
            input.WriteHeader((ushort)i, 0, false);
            await input.WriteAsync(new byte[] { (byte)i }, 0, 1, cancellation.Token);
        }

        Assert.Equal(100, input.AvailableFrames);
        var ready = Signal();
        var received = Signal();
        List<byte> captured = [];
        Task pump = AudioVoiceListener.ReadFramesAsync(input, frame =>
        {
            captured.Add(frame.Payload[0]);
            received.SetResult();
            return Task.CompletedTask;
        }, () =>
        {
            ready.TrySetResult();
            return Task.CompletedTask;
        }, cancellation.Token);

        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(captured);
            input.WriteHeader(102, 0, false);
            await input.WriteAsync(new byte[] { 255 }, 0, 1, cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal([255], captured.Select(value => (int)value).ToArray());
    }

    [Fact]
    public async Task DisposedVoiceInputDoesNotBlockReaderCancellation()
    {
        using var input = new InputStream();
        using var cancellation = new CancellationTokenSource();
        var idle = Signal();
        Task pump = AudioVoiceListener.ReadFramesAsync(input, _ => Task.CompletedTask, () =>
        {
            idle.TrySetResult();
            return Task.CompletedTask;
        }, cancellation.Token);

        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Dispose();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task IdleVoiceInputStaysResponsiveToFramesAndCancellation()
    {
        using var input = new InputStream();
        using var cancellation = new CancellationTokenSource();
        var idle = Signal();
        var received = Signal();
        int idleCount = 0;
        Task pump = AudioVoiceListener.ReadFramesAsync(input, frame =>
        {
            Assert.Equal(3840, frame.Payload.Length);
            received.SetResult();
            return Task.CompletedTask;
        }, () =>
        {
            if (++idleCount == 2)
            {
                idle.SetResult();
            }

            return Task.CompletedTask;
        }, cancellation.Token);

        try
        {
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.WriteHeader(1, 0, false);
            await input.WriteAsync(new byte[3840], 0, 3840, cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task IdleHandlerFailureStopsTheVoiceReader()
    {
        using var input = new InputStream();

        await Assert.ThrowsAsync<IOException>(() => AudioVoiceListener.ReadFramesAsync(input, _ => Task.CompletedTask,
            () => Task.FromException(new IOException("Idle handler failed")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ModelDownloadIsAtomicValidatedAndCached()
    {
        using var files = new ModelFiles();
        byte[] bytes = [1, 2, 3, 4];
        int calls = 0;
        using var http = new HttpClient(new TestHttpHandler((_, _) =>
        {
            calls++;
            Assert.False(File.Exists(files.ModelPath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));

        await WhisperModel.EnsureDownloadedAsync(http, files.ModelPath, WhisperModel.DownloadUri,
            Convert.ToHexString(SHA512.HashData(bytes)), CancellationToken.None);
        await WhisperModel.EnsureDownloadedAsync(http, files.ModelPath, WhisperModel.DownloadUri,
            Convert.ToHexString(SHA512.HashData(bytes)), CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(files.ModelPath));
        Assert.Equal(1, calls);
        Assert.Single(Directory.GetFiles(files.DirectoryPath));
    }

    [Fact]
    public async Task InterruptedOrCorruptDownloadsDoNotLeaveAModelOrPartialFile()
    {
        using var files = new ModelFiles();
        using var interrupted = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new InterruptedContent() })));

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => WhisperModel.EnsureDownloadedAsync(interrupted, files.ModelPath,
            WhisperModel.DownloadUri, WhisperModel.Sha512, CancellationToken.None));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Empty(Directory.GetFiles(files.DirectoryPath));

        using var corrupt = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));

        await Assert.ThrowsAsync<InvalidDataException>(() => WhisperModel.EnsureDownloadedAsync(corrupt, files.ModelPath,
            WhisperModel.DownloadUri, WhisperModel.Sha512, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(files.DirectoryPath));
    }

    [Fact]
    public async Task ModelDownloadFailureDoesNotFaultTheHostedService()
    {
        using var files = new ModelFiles();
        using var http = new HttpClient(new TestHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        bool created = false;
        await using var worker = new Worker(async token =>
        {
            await WhisperModel.EnsureDownloadedAsync(http, files.ModelPath, WhisperModel.DownloadUri, WhisperModel.Sha512, token);
            created = true;
            return new FakeTranscriber((_, _) => Task.FromResult(""));
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ReadyAsync());
        await worker.Service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.False(created);
        Assert.False(worker.Service.TryEnqueue(Request(1)));
        Assert.Single(worker.Errors);
        Assert.Empty(Directory.GetFiles(files.DirectoryPath));
    }

    private static SpeechSegment ContextSegment(double start, double end) =>
        new([], (long)(start * 16_000), (long)(end * 16_000),
            (long)((end + 101.2) * Stopwatch.Frequency), (long)((start + 100) * Stopwatch.Frequency), (long)((end + 100) * Stopwatch.Frequency));

    private static AudioSpeechRequest Request(int speaker,
        Func<string, CancellationToken, Task>? onTranscript = null, Func<string, Task>? onError = null) =>
        new([speaker], Stopwatch.GetTimestamp(), CancellationToken.None,
            onTranscript ?? ((_, _) => Task.CompletedTask), onError ?? (_ => Task.CompletedTask));

    private static byte[] Pcm(int frames, short left, short right)
    {
        byte[] pcm = new byte[frames * 960 * 4];

        for (int offset = 0; offset < pcm.Length; offset += 4)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), left);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset + 2), right);
        }

        return pcm;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeTranscriber(Func<float[], CancellationToken, Task<string>> transcribe) : ILocalSpeechTranscriber
    {
        public int DisposeCalls { get; private set; }

        public ConcurrentQueue<int> Threads { get; } = [];

        public Task<string> TranscribeAsync(float[] samples, int threads, CancellationToken cancellationToken)
        {
            Threads.Enqueue(threads);
            return transcribe(samples, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Worker : IAsyncDisposable
    {
        public AudioSpeechService Service { get; }
        public ConcurrentQueue<Exception> Errors { get; } = [];

        public Worker(Func<ILocalSpeechTranscriber> createTranscriber, Func<int>? getThreads = null,
            Action<string>? logWhisperProcessing = null, TimeProvider? timeProvider = null)
            : this(_ => Task.FromResult(createTranscriber()), getThreads, logWhisperProcessing, timeProvider)
        {
        }

        public Worker(Func<CancellationToken, Task<ILocalSpeechTranscriber>> createTranscriber, Func<int>? getThreads = null,
            Action<string>? logWhisperProcessing = null, TimeProvider? timeProvider = null)
        {
            Service = new(createTranscriber, Errors.Enqueue, getThreads, logWhisperProcessing: logWhisperProcessing, timeProvider: timeProvider);
            Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public Task ReadyAsync() => Service.EnsureReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Service.Dispose();
            }
        }
    }

    public class MessageChannelProxy : DispatchProxy
    {
        public object?[]? Arguments { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IMessageChannel.SendMessageAsync))
            {
                throw new NotSupportedException();
            }

            Arguments = args;
            return Task.FromResult<IUserMessage>(null!);
        }
    }

    private sealed class QueueSource(string title, string author) : AudioSourceBase(null!, false, TimeSpan.FromSeconds(30))
    {
        public override string Description => title;
        public override string Author => author;
        public override string Url => "";
        public override string ThumbnailUrl => "";
        public override TimeSpan? Duration => null;
        public override TimeSpan? Remaining => null;

        protected override Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken) => Task.CompletedTask;
        protected override ValueTask DisposeResourcesAsync() => ValueTask.CompletedTask;

        public override ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);
    }

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => respond(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class TestConfiguration : IConfigurationService
    {
        private readonly Dictionary<string, string> _values = [];

        public void Set(ulong? context, string key, string value) => _values[key] = value;

        public bool Remove(ulong? context, string key) => _values.Remove(key);

        public bool TryGet(ulong? context, string key, out string value) => _values.TryGetValue(key, out value!);
    }

    private sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class InterruptedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            stream.Write([1, 2, 3]);
            throw new IOException("Interrupted model download.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ModelFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "mihubot-speech-" + Guid.NewGuid().ToString("N"));
        public string ModelPath => Path.Combine(DirectoryPath, "model.bin");

        public ModelFiles() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
