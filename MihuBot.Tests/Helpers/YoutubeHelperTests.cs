using MihuBot.Helpers;
using Newtonsoft.Json;
using YoutubeExplode.Common;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;

namespace MihuBot.Tests.Helpers;

public sealed class YoutubeHelperTests
{
    private const string VideoId = "afqQwmXTcPA";
    private const string ChannelId = "UC0123456789012345678901";

    [Fact]
    public async Task GetVideoAsync_PrefersYoutubeExplode()
    {
        Video expected = new(VideoId, "Original title", new Author(ChannelId, "Channel"),
            DateTimeOffset.UtcNow, "", TimeSpan.FromSeconds(123), [], [], new Engagement(0, 0, 0));

        Video actual = await YoutubeHelper.GetVideoAsync(VideoId,
            (_, _) => ValueTask.FromResult(expected),
            (_, _) => throw new InvalidOperationException("Fallback should not run."));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetVideoAsync_FallsBackToYtDlp(bool httpFailure)
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
        Exception failure = httpFailure ? new HttpRequestException("Bad response") : new YoutubeExplodeException("Video watch page is broken.");
        using var cancellation = new CancellationTokenSource();
        var messages = new List<string>();

        Video video = await YoutubeHelper.GetVideoAsync(VideoId,
            (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                throw failure;
            },
            (url, token) =>
            {
                Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", url);
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(metadata);
            },
            cancellation.Token,
            messages.Add);

        string diagnostic = Assert.Single(messages);
        Assert.Contains("falling back to yt-dlp", diagnostic, StringComparison.Ordinal);
        Assert.Contains(failure.Message, diagnostic, StringComparison.Ordinal);
        Assert.Equal(VideoId, video.Id.Value);
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
    public async Task GetVideoAsync_FallbackHandlesLiveAndMissingOptionalMetadata()
    {
        Video video = await YoutubeHelper.GetVideoAsync(VideoId,
            (_, _) => throw new YoutubeExplodeException("Broken page"),
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
    public async Task GetVideoAsync_PreservesBothProviderFailures()
    {
        var original = new YoutubeExplodeException("Broken page");
        var fallback = new InvalidOperationException("yt-dlp failed");

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId, (_, _) => throw original, (_, _) => throw fallback));

        Assert.Equal(new Exception[] { original, fallback }, exception.InnerExceptions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("differentId")]
    public async Task GetVideoAsync_RejectsInvalidFallbackMetadata(string? id)
    {
        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId,
                (_, _) => throw new YoutubeExplodeException("Broken page"),
                (_, _) => Task.FromResult(new YoutubeDl.YoutubeDlMetadata { Id = id, Title = "Title" })));

        Assert.IsType<InvalidOperationException>(exception.InnerExceptions[1]);
    }

    [Fact]
    public async Task GetVideoAsync_DoesNotFallbackOnCancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId,
                (_, _) => throw new OperationCanceledException(),
                (_, _) => throw new InvalidOperationException("Fallback should not run.")));
    }

    [Fact]
    public async Task GetVideoAsync_DoesNotWrapFallbackCancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await YoutubeHelper.GetVideoAsync(VideoId,
                (_, _) => throw new YoutubeExplodeException("Broken page"),
                (_, _) => throw new OperationCanceledException()));
    }
}
