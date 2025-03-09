using Google.Apis.YouTube.v3;
using SpotifyAPI.Web;
using System.Globalization;
using System.Text.RegularExpressions;
using YoutubeExplode.Videos;

namespace MihuBot.Audio;

public sealed partial class AudioCommands : CommandBase
{
    private static readonly string[] AvailableCommands = ["p", "play", "pause", "unpause", "resume", "skip", "volume", "queue"];

    public override string Command => "mplay";
    public override string[] Aliases => [.. AvailableCommands, "audiocommands", "audiodebug", "audiotempsettings"];

    private readonly AudioService _audioService;
    private readonly SpotifyClient _spotifyClient;
    private readonly YouTubeService _youtubeService;

    public AudioCommands(AudioService audioService, IEnumerable<SpotifyClient> spotifyClients, YouTubeService youtubeService)
    {
        _audioService = audioService;
        _spotifyClient = spotifyClients.SingleOrDefault();
        _youtubeService = youtubeService;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        ChannelPermissions permissions = ctx.ChannelPermissions;

        _audioService.TryGetAudioPlayer(ctx.Guild.Id, out AudioPlayer audioPlayer);

        if (ctx.Command == "audiotempsettings")
        {
            if (await ctx.RequirePermissionAsync(ctx.Command) &&
                ctx.Arguments.Length == 3 &&
                int.TryParse(ctx.Arguments[0], out int streamBufferMs) &&
                int.TryParse(ctx.Arguments[1], out int packetLoss) &&
                int.TryParse(ctx.Arguments[2], out int bitrateKbit))
            {
                GlobalAudioSettings.StreamBufferMs = streamBufferMs;
                GlobalAudioSettings.PacketLoss = packetLoss;
                GlobalAudioSettings.MinBitrateKb = bitrateKbit;
            }

            return;
        }

        string command = ctx.Command;
        if (ctx.Arguments.Length == 1 && (command == "p" || command == "play") && AvailableCommands.Contains(ctx.Arguments[0], StringComparison.OrdinalIgnoreCase))
        {
            command = ctx.Arguments[0].ToLowerInvariant();
        }

        if (command is "mplay" or "audiocommands")
        {
            await ctx.ReplyAsync($"I know of these: {string.Join(", ", AvailableCommands.Select(c => $"`!{c}`"))}");
            return;
        }

        if (command is "pause" or "unpause" or "resume" or "skip" or "volume" or "audiodebug" or "queue")
        {
            if (audioPlayer is not null)
            {
                audioPlayer.LastTextChannel = ctx.Channel;

                Task reactionTask = null;

                void SendThumbsUpReaction()
                {
                    reactionTask = ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                }

                try
                {
                    if (command == "pause")
                    {
                        SendThumbsUpReaction();
                        audioPlayer.Pause();
                    }
                    else if (command is "unpause" or "resume")
                    {
                        SendThumbsUpReaction();
                        audioPlayer.Unpause();
                    }
                    else if (command == "skip")
                    {
                        if (ctx.ArgumentString is { Length: > 0 } argument)
                        {
                            if (TryParseSimpleDurationString(argument, out TimeSpan duration) && duration.TotalDays < 100)
                            {
                                SendThumbsUpReaction();
                                await audioPlayer.SkipInCurrentSourceAsync(duration);
                            }
                            else
                            {
                                await ctx.ReplyAsync("I didn't understand that time format. Try `!skip 10 sec`");
                            }
                        }
                        else
                        {
                            SendThumbsUpReaction();
                            await audioPlayer.SkipAsync();
                        }
                    }
                    else if (command == "volume")
                    {
                        string currentOrNew = "Current";

                        if (ctx.Arguments.Length > 0 && TryParseDb(ctx.ArgumentString, out float db))
                        {
                            db = -Math.Abs(db);
                            await audioPlayer.AudioSettings.ModifyAsync((settings, volume) => settings.Volume = volume, VolumeHelper.GetVolumeSliderForDecibels(db));
                            currentOrNew = "New";
                        }
                        else if (ctx.Arguments.Length > 0 && uint.TryParse(ctx.Arguments[0].TrimEnd('%'), out uint volume) && volume > 0 && volume <= 100)
                        {
                            await audioPlayer.AudioSettings.ModifyAsync((settings, volume) => settings.Volume = volume, volume / 100f);
                            currentOrNew = "New";
                        }

                        float currentVolume = audioPlayer.AudioSettings.Volume ?? Constants.VCDefaultVolume;
                        float factor = VolumeHelper.GetAmplitudeFactorForVolumeSlider(currentVolume);
                        float volumeDB = VolumeHelper.GetVolumeAsDecibels(factor);

                        await ctx.ReplyAsync($"{currentOrNew} volume is {(int)(currentVolume * 100)}% (amplitude factor {factor:N2} or {volumeDB:N2} dB)");

                        static bool TryParseDb(string input, out float db)
                        {
                            input = input.Replace(',', '.');

                            if (VolumeDbRegex().Match(input) is { Success: true } match &&
                                float.TryParse(match.Groups[1].ValueSpan, NumberStyles.Any, CultureInfo.InvariantCulture, out db))
                            {
                                return true;
                            }

                            db = default;
                            return false;
                        }
                    }
                    else if (command == "queue")
                    {
                        await audioPlayer.PostQueueAsync();
                    }
                    else if (command == "audiodebug" && await ctx.RequirePermissionAsync(command))
                    {
                        var sb = new StringBuilder();
                        audioPlayer.DebugDump(sb);
                        await ctx.Channel.SendTextFileAsync($"AudioDebug-{ctx.Guild.Id}.txt", sb.ToString());
                    }
                }
                finally
                {
                    if (reactionTask is not null)
                    {
                        await reactionTask;
                    }
                }
            }
        }
        else
        {
            if (audioPlayer is null)
            {
                SocketVoiceChannel voiceChannel = ctx.Guild.VoiceChannels.FirstOrDefault(vc => vc.GetUser(ctx.AuthorId) is not null);

                if (voiceChannel is null)
                {
                    await ctx.ReplyAsync("Join a voice channel first");
                    return;
                }

                audioPlayer = await _audioService.GetOrCreateAudioPlayerAsync(ctx.Discord, ctx.Channel, voiceChannel);
                audioPlayer.LastTextChannel = ctx.Channel;
            }

            if (ctx.Arguments.Length > 0)
            {
                string argument = ctx.Arguments[0];
                try
                {
                    if (YoutubeHelper.TryParseVideoId(argument, out string videoId))
                    {
                        Video video = await YoutubeHelper.GetVideoAsync(videoId);
                        audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    else if (YoutubeHelper.TryParsePlaylistId(argument, out string playlistId))
                    {
                        List<IVideo> videos = await YoutubeHelper.GetVideosAsync(playlistId, _youtubeService);
                        foreach (IVideo video in videos)
                        {
                            audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                        }
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    else if (TryParseSpotifyPlaylistId(argument, out playlistId))
                    {
                        if (_spotifyClient is null)
                        {
                            return;
                        }

                        var page = await _spotifyClient.Playlists.GetItems(playlistId);

                        bool foundAny = false;

                        await foreach (FullTrack track in _spotifyClient.Paginate(page)
                            .Select(t => t.Track)
                            .OfType<FullTrack>())
                        {
                            IVideo searchResult = await YoutubeHelper.TryFindSongAsync(track.Name, track.Artists.FirstOrDefault()?.Name, _youtubeService);
                            if (searchResult is not null)
                            {
                                Video video = await YoutubeHelper.GetVideoAsync(searchResult.Id);
                                audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));

                                if (!foundAny)
                                {
                                    foundAny = true;
                                    await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                                }
                            }
                        }
                    }
                    else if (TryParseSpotifyTrackId(argument, out string trackId))
                    {
                        if (_spotifyClient is null)
                        {
                            return;
                        }

                        FullTrack track = await _spotifyClient.Tracks.Get(trackId);
                        IVideo searchResult = await YoutubeHelper.TryFindSongAsync(track.Name, track.Artists.FirstOrDefault()?.Name, _youtubeService);
                        if (searchResult is not null)
                        {
                            Video video = await YoutubeHelper.GetVideoAsync(searchResult.Id);
                            audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                        }
                    }
                    else if (await YoutubeHelper.TrySearchAsync(ctx.ArgumentString.Replace('-', ' '), _youtubeService) is { } video)
                    {
                        audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    else
                    {
                        await ctx.ReplyAsync("Sorry, I don't know that");
                    }
                }
                catch (Exception ex)
                {
                    await ctx.DebugAsync(ex);
                    await ctx.ReplyAsync($"Something went wrong :(\n`{ex.Message}`");
                }
            }
        }
    }

    private static bool TryParseSpotifyPlaylistId(string argument, out string playlistId)
    {
        playlistId = null;

        if (!argument.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
            !argument.Contains("playlist", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(argument, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        if (!path.StartsWith("/playlist/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        playlistId = path.Substring("/playlist/".Length);
        return !playlistId.Contains('/');
    }

    private static bool TryParseSpotifyTrackId(string argument, out string trackId)
    {
        trackId = null;

        if (!argument.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
            !argument.Contains("track", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(argument, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        if (!path.StartsWith("/track/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        trackId = path.Substring("/track/".Length);
        return !trackId.Contains('/');
    }

    private static bool TryParseSimpleDurationString(string argument, out TimeSpan duration)
    {
        string[] parts = argument.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length > 2)
        {
            duration = default;
            return false;
        }

        if (parts.Length == 1)
        {
            string part = parts[0];
            int endOfNumber = part.AsSpan().IndexOfAnyExceptInRange('0', '9');
            if (endOfNumber >= 0)
            {
                parts = [part.Substring(0, endOfNumber), part.Substring(endOfNumber)];
            }
        }

        int multiplier = 1;

        if (parts.Length == 2)
        {
            string modifier = parts[1].ToLowerInvariant();

            if (modifier is "s" or "sec" or "secs" or "second" or "seconds")
            {
                multiplier = 1;
            }
            else if (modifier is "m" or "min" or "mins" or "minute" or "minutes")
            {
                multiplier = 60;
            }
            else
            {
                duration = default;
                return false;
            }
        }

        if (uint.TryParse(parts[0], out uint value) && value > 0)
        {
            duration = TimeSpan.FromSeconds(value * multiplier);
            return true;
        }

        duration = default;
        return false;
    }

    [GeneratedRegex(@"(-?\d+\.?\d*) ?db", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeDbRegex();
}