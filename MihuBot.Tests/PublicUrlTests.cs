using MihuBot.Helpers;

namespace MihuBot.Tests;

public sealed class PublicUrlTests
{
    [Fact]
    public void BaseUrlMatchesDeployment()
    {
        Assert.Equal(OperatingSystem.IsLinux() ? "https://mihubot.xyz" : "http://localhost:5000", Constants.PublicBaseUrl);
    }

    [Fact]
    public void ArtifactAndShortUrlsUseTheSamePublicOrigin()
    {
        using var http = new HttpClient();
        var storage = new StorageClient(http, "artifacts", "test-key", isPublic: true);
        Assert.Equal($"{Constants.PublicBaseUrl}/s/artifacts/job/Results.zip",
            storage.GetFileUrl("job/Results.zip", TimeSpan.FromMinutes(1), writeAccess: false));
        Assert.Equal($"{Constants.PublicBaseUrl}/s/artifacts",
            storage.GetContainerUrl(TimeSpan.FromMinutes(1), writeAccess: false));

        var entry = new UrlShortenerService.Entry { Id = 123 };
        Assert.Equal($"{Constants.PublicBaseUrl}/r/{Snowflake.GetString(123)}", entry.ShortUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignedStorageUrlsKeepTheCorrectSchemeAndPort(bool writeAccess)
    {
        using var http = new HttpClient();
        var storage = new StorageClient(http, "private", "test-key", isPublic: false);
        foreach (string url in new[]
        {
            storage.GetFileUrl("job/Results.zip", TimeSpan.FromMinutes(1), writeAccess),
            storage.GetContainerUrl(TimeSpan.FromMinutes(1), writeAccess),
        })
        {
            var uri = new Uri(url);
            Assert.Equal(Constants.PublicBaseUrl, uri.GetLeftPart(UriPartial.Authority));
            Assert.Contains("&sig=", uri.Query, StringComparison.Ordinal);
            Assert.Contains($"&w={(writeAccess ? 1 : 0)}", uri.Query, StringComparison.Ordinal);
        }
    }
}
