using MihuBot.Helpers;
using Newtonsoft.Json;
using YoutubeExplode.Videos;

namespace MihuBot.Tests.Helpers;

public sealed class YoutubeHelperTests
{
    private const string VideoId = "afqQwmXTcPA";
    private const string ChannelId = "UC0123456789012345678901";

    [Theory]
    [InlineData("Music: &#39;&#39;Sentience&#39;&#39; | Artist", "Music: ''Sentience'' | Artist")]
    [InlineData("&quot;Title&quot; &amp; Artist", "\"Title\" & Artist")]
    [InlineData("Plain \"title\" & artist", "Plain \"title\" & artist")]
    [InlineData("&amp;#39;", "&#39;")]
    public void Transform_SearchResultDecodesTitleOnce(string title, string expected)
    {
        var result = new Google.Apis.YouTube.v3.Data.SearchResult
        {
            Id = new() { VideoId = VideoId },
            Snippet = new()
            {
                Title = title,
                ChannelId = ChannelId,
                ChannelTitle = "Channel",
                Description = "",
                Thumbnails = new()
                {
                    Default__ = new() { Url = "https://img.youtube.com/vi/afqQwmXTcPA/default.jpg", Width = 120, Height = 90 }
                }
            }
        };

        Video video = YoutubeHelper.Transform(result);

        Assert.Equal(expected, video.Title);
    }

    [Fact]
    public async Task GetVideoAsync_UsesYtDlpMetadataForRequestedVideo()
    {
        var metadata = JsonConvert.DeserializeObject<YoutubeDl.YoutubeDlMetadata>(
            $$"""
            {
                "id": "{{VideoId}}",
                "title": "Video title",
                "channel_id": "{{ChannelId}}",
                "channel": "Channel name",
                "upload_date": "20261021",
                "description": "Video description",
                "duration": 123.5,
                "tags": ["music"]
            }
            """)!;
        using var cancellation = new CancellationTokenSource();

        Video video = await YoutubeHelper.GetVideoAsync(VideoId,
            (url, token) =>
            {
                Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", url);
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(metadata);
            },
            cancellation.Token);

        Assert.Equal(VideoId, video.Id.Value);
        Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", video.Url);
        Assert.Equal("Video title", video.Title);
        Assert.Equal(ChannelId, video.Author.ChannelId.Value);
        Assert.Equal("Channel name", video.Author.ChannelTitle);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), video.UploadDate);
        Assert.Equal("Video description", video.Description);
        Assert.Equal(TimeSpan.FromSeconds(123.5), video.Duration);
        Assert.Equal("music", Assert.Single(video.Keywords));
        Assert.Equal($"https://img.youtube.com/vi/{VideoId}/hqdefault.jpg", Assert.Single(video.Thumbnails).Url);
    }

    [Fact]
    public async Task GetVideoAsync_HandlesLiveAndMissingOptionalMetadata()
    {
        Video video = await YoutubeHelper.GetVideoAsync(VideoId,
            (_, _) => Task.FromResult(new YoutubeDl.YoutubeDlMetadata
            {
                Id = VideoId,
                Title = "Live stream",
                ChannelId = ChannelId,
                IsLive = true,
                Duration = 123,
                Uploader = "Uploader"
            }));

        Assert.Equal("Uploader", video.Author.ChannelTitle);
        Assert.Null(video.Duration);
        Assert.Empty(video.Description);
        Assert.Empty(video.Keywords);
    }

    [Fact]
    public async Task GetVideoAsync_PropagatesMetadataFailure()
    {
        var failure = new InvalidOperationException("yt-dlp failed");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId, (_, _) => throw failure));

        Assert.Same(failure, exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("differentId")]
    public async Task GetVideoAsync_RejectsMetadataForAnotherVideo(string? id)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId,
                (_, _) => Task.FromResult(new YoutubeDl.YoutubeDlMetadata { Id = id, Title = "Title" })));
    }

    [Fact]
    public async Task GetVideoAsync_DoesNotLookupMetadataWhenAlreadyCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId,
                (_, _) => throw new InvalidOperationException("Metadata lookup should not run."),
                cancellation.Token));
    }

    [Fact]
    public async Task GetVideoAsync_DoesNotWrapCancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId,
                (_, _) => throw new OperationCanceledException()));
    }
}
