using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Configuration;
using MihuBot.Helpers;
using MihuBot.YoutubeArchive;

namespace MihuBot.Tests;

public sealed class YoutubeArchiveTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk&list=abc")]
    [InlineData("https://youtu.be/abcdefghijk?t=20")]
    [InlineData("https://music.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://m.youtube.com/shorts/abcdefghijk")]
    [InlineData("https://youtube.com/embed/abcdefghijk")]
    [InlineData("https://youtube.com/live/abcdefghijk")]
    public void RecognizesAndCanonicalizesYouTubeVideos(string url)
    {
        Assert.True(YoutubeHelper.TryParseVideoUrl(url, out string? id));
        Assert.Equal("abcdefghijk", id);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", YoutubeArchiveValidation.GetUrl(id));
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=abc")]
    [InlineData("https://youtube.com.evil/watch?v=abcdefghijk")]
    [InlineData("https://youtube.com@evil/watch?v=abcdefghijk")]
    [InlineData("https://user@youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://youtube.com:8443/watch?v=abcdefghijk")]
    [InlineData("https://youtube.com/watch?v=short")]
    [InlineData("https://youtube.com/watch?v=abcdefghijk&v=12345678901")]
    [InlineData("https://youtu.be/abcdefghijk/extra")]
    [InlineData("file:///abcdefghijk")]
    [InlineData("https://youtu.be/abcdefghij%22")]
    public void RejectsInvalidOrNonYouTubeUrls(string url)
    {
        Assert.False(YoutubeHelper.TryParseVideoUrl(url, out _));
    }

    [Theory]
    [InlineData("https://youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://youtu.be/abcdefghijk")]
    [InlineData("https://youtube.com/embed/abcdefghijk")]
    [InlineData("https://youtube.com/shorts/abcdefghijk")]
    [InlineData("https://youtube.com/live/abcdefghijk")]
    public void SharedYouTubeHelperStillExtractsVideosFromDiscordMessages(string url)
    {
        string message = $"Here is a video: {url}&t=20";
        Assert.True(YoutubeHelper.TryParseVideoId(message, out string? id));
        Assert.Equal("abcdefghijk", id);
        Assert.False(YoutubeHelper.TryParseVideoUrl(message, out _));
    }

    [Theory]
    [InlineData("audio", null, true)]
    [InlineData("audio", 1080, false)]
    [InlineData("video", null, true)]
    [InlineData("video", 1080, true)]
    [InlineData("video", 4320, true)]
    [InlineData("video", 1000, false)]
    [InlineData("video", -1, false)]
    [InlineData("other", null, false)]
    public void ValidatesModeAndResolution(string mode, int? height, bool valid)
    {
        Assert.Equal(valid, YoutubeArchiveValidation.IsValid(new("https://youtu.be/abcdefghijk", mode, height)));
    }

    [Fact]
    public void DownloaderPreservesQualityAndConstrainsBothVideoFallbacks()
    {
        string[] audio = YoutubeArchiveDownloader.GetDownloadArguments(new() { Mode = "audio" }, "archive");
        Assert.Contains("bestaudio", audio);
        Assert.Contains("best", audio);
        Assert.Contains(Path.Combine("archive", "%(channel,uploader,channel_id).100B", "%(title).150B [%(id)s].%(ext)s"), audio);
        Assert.DoesNotContain("--recode-video", audio);
        string[] video = YoutubeArchiveDownloader.GetDownloadArguments(new() { Mode = "video", MaxHeight = 1080 }, "archive");
        Assert.Contains("bv[height<=1080]+ba/b[height<=1080]", video);
        Assert.Contains("--remux-video", video);
        Assert.Contains("mkv", video);
        Assert.DoesNotContain("--recode-video", video);
        using var files = new ArchiveFiles();
        using var configuration = new ConfigurationService(files.Root);
        var downloader = new YoutubeArchiveDownloader(configuration);
        string[] arguments = downloader.GetArguments("abcdefghijk", audio);
        Assert.Equal("--", arguments[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", arguments[^1]);
        Assert.Contains("--ignore-config", arguments);
        Assert.Contains("--no-playlist", arguments);
    }

    [Fact]
    public async Task QueueIsDurableDeduplicatedAndArchivesIntoSeparateFolders()
    {
        using var files = new ArchiveFiles();
        var downloader = new FakeDownloader();
        Guid audioId;
        using (var service = files.CreateService(downloader))
        {
            var audio = service.Enqueue(new("https://youtu.be/abcdefghijk", "audio"));
            audioId = audio.Id;
            Assert.Equal(audio.Id, service.Enqueue(new("https://youtube.com/watch?v=abcdefghijk", "audio")).Id);
            service.Enqueue(new("https://youtu.be/abcdefghijk", "video", 720));
            Assert.Equal(2, service.GetJobs().Length);
        }

        using var reopened = files.CreateService(downloader);
        Assert.Contains(reopened.GetJobs(), j => j.Id == audioId && j.Status == "queued");
        await reopened.StartAsync(default);
        await WaitForAsync(() => reopened.GetJobs().All(j => j.Status == "completed"));
        await reopened.StopAsync(default);
        Assert.Equal(2, downloader.Downloads);
        Assert.True(File.Exists(Path.Combine(files.Root, "Test Channel", "Audio", "abcdefghijk", "media.opus")));
        Assert.True(File.Exists(Path.Combine(files.Root, "Test Channel", "Video", "abcdefghijk-720", "media.mkv")));
        Assert.Equal(audioId, reopened.Enqueue(new("https://youtu.be/abcdefghijk", "audio")).Id);
        File.Delete(Path.Combine(files.Root, "Test Channel", "Audio", "abcdefghijk", "media.opus"));
        Assert.Throws<FileNotFoundException>(() => reopened.Enqueue(new("https://youtu.be/abcdefghijk", "audio")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoversInterruptedJobAndAtomicMoveWithoutRedownloading(bool channelLayout)
    {
        using var files = new ArchiveFiles();
        var downloader = new FakeDownloader();
        YoutubeArchiveJob job;
        using (var service = files.CreateService(downloader))
        {
            job = service.Enqueue(new("https://youtu.be/abcdefghijk", "video", 1080));
        }

        var store = new SynchronizedLocalJsonStore<YoutubeArchiveState>(files.State);
        string relative = channelLayout
            ? Path.Combine("Test Channel", "Video", "abcdefghijk-1080")
            : Path.Combine("Video", "abcdefghijk-1080");
        store.Modify(state => state.Jobs[0] = job with { Status = "running", ArchiveDirectory = channelLayout ? relative : null });
        string destination = Path.Combine(files.Root, relative);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "media.mkv"), "media");
        File.WriteAllText(Path.Combine(destination, ".mihubot-media"), "media.mkv");
        using var restarted = files.CreateService(downloader);
        await restarted.StartAsync(default);
        await WaitForAsync(() => restarted.GetJobs()[0].Status == "completed");
        await restarted.StopAsync(default);
        Assert.Equal(0, downloader.Downloads);
        Assert.Equal(Path.Combine(relative, "media.mkv"), restarted.GetJob(job.Id).File);
    }

    [Fact]
    public async Task FailureIsVisibleAndRetryGetsANewJob()
    {
        using var files = new ArchiveFiles();
        var downloader = new FakeDownloader { Fail = true };
        using var service = files.CreateService(downloader);
        YoutubeArchiveJob first = service.Enqueue(new("https://youtu.be/abcdefghijk", "audio"));
        await service.StartAsync(default);
        await WaitForAsync(() => service.GetJobs().Any(j => j.Status == "failed"));
        Assert.Equal("Download rejected.", service.GetJobs()[0].Error);
        downloader.Fail = false;
        YoutubeArchiveJob retry = service.Enqueue(new("https://youtu.be/abcdefghijk", "audio"));
        Assert.NotEqual(first.Id, retry.Id);
        await WaitForAsync(() => service.GetJobs().Any(j => j.Id == retry.Id && j.Status == "completed"));
        await service.StopAsync(default);
        Assert.Contains(files.Logs, l => l.Contains("Download rejected.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulArchiveCompletesWithoutWarnings()
    {
        using var files = new ArchiveFiles();
        using var service = files.CreateService(new FakeDownloader());
        service.Enqueue(new("https://youtu.be/abcdefghijk", "audio"));
        await service.StartAsync(default);
        await WaitForAsync(() => service.GetJobs()[0].Status == "completed");
        await service.StopAsync(default);
        Assert.Null(service.GetJobs()[0].Warning);
        Assert.Null(service.GetJobs()[0].Error);
        Assert.True(File.Exists(Path.Combine(files.Root, service.GetJobs()[0].File)));
    }

    [Fact]
    public async Task ShutdownRequeuesActiveJobAndRestartResumesIt()
    {
        using var files = new ArchiveFiles();
        var downloader = new FakeDownloader { Block = true };
        Guid id;
        using (var service = files.CreateService(downloader))
        {
            id = service.Enqueue(new("https://youtu.be/abcdefghijk", "audio")).Id;
            await service.StartAsync(default);
            await WaitForAsync(() => service.GetJobs()[0].Status == "running" && downloader.Downloads == 1);
            await service.StopAsync(default);
            Assert.Equal("queued", service.GetJobs()[0].Status);
        }

        downloader.Block = false;
        using var restarted = files.CreateService(downloader);
        await restarted.StartAsync(default);
        await WaitForAsync(() => restarted.GetJobs()[0].Status == "completed");
        await restarted.StopAsync(default);
        Assert.Equal(id, restarted.GetJobs()[0].Id);
        Assert.Equal(2, downloader.Downloads);
    }

    [Fact]
    public void RejectsQueueOverflowButAllowsDeduplicatedRequests()
    {
        using var files = new ArchiveFiles();
        using var service = files.CreateService(new FakeDownloader());

        for (int i = 0; i < 100; i++)
        {
            service.Enqueue(new($"https://youtu.be/{i:D11}", "audio"));
        }

        Assert.Throws<InvalidOperationException>(() => service.Enqueue(new("https://youtu.be/abcdefghijk", "audio")));
        Assert.Equal(100, service.GetJobs().Length);
        Assert.NotNull(service.Enqueue(new("https://youtu.be/00000000000", "audio")));
    }

    [Fact]
    public async Task AuthenticationRequiresDedicatedBearerTokenRatherThanCookies()
    {
        using var files = new ArchiveFiles();
        using var runtimeConfiguration = new ConfigurationService(files.Root);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        string secret = new('a', 64);
        runtimeConfiguration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, secret);
        builder.Services.AddSingleton<IConfigurationService>(runtimeConfiguration);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie()
            .AddScheme<AuthenticationSchemeOptions, YoutubeArchiveAuthenticationHandler>(
                YoutubeArchiveAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/login", async (HttpContext context) =>
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "cookie-user")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        });
        app.MapGet("/test", () => "ok").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = YoutubeArchiveAuthenticationHandler.SchemeName
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
        using var missing = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var login = await http.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.Contains("Set-Cookie"));
        using var cookieOnly = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrong = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var valid = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task ApiValidatesEnqueuesAndExposesAuthenticatedJobStatus()
    {
        using var files = new ArchiveFiles();
        using var runtimeConfiguration = new ConfigurationService(files.Root);
        var downloader = new FakeDownloader();
        using var service = files.CreateService(downloader);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        string secret = new('b', 64);
        runtimeConfiguration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, secret);
        builder.Services.AddSingleton<IConfigurationService>(runtimeConfiguration);
        builder.Services.AddSingleton(service);
        builder.Services.AddControllers().AddApplicationPart(typeof(MihuBot.API.YoutubeArchiveController).Assembly);
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, YoutubeArchiveAuthenticationHandler>(
            YoutubeArchiveAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
        using var anonymous = await http.GetAsync("/api/YoutubeArchive/Jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var invalid = await http.PostAsync("/api/YoutubeArchive/Jobs",
            new StringContent("""{"url":"https://evil.example/","mode":"audio"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var accepted = await http.PostAsync("/api/YoutubeArchive/Jobs",
            new StringContent("""{"url":"https://youtu.be/abcdefghijk","mode":"video","maxHeight":720}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull(accepted.Headers.Location);
        using var job = await http.GetAsync(accepted.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        Assert.Contains("\"status\":\"queued\"", await job.Content.ReadAsStringAsync());
        using var metadata = await http.GetAsync("/api/YoutubeArchive/Metadata?url=https%3A%2F%2Fyoutu.be%2Fabcdefghijk");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Contains("\"isMusic\":true", await metadata.Content.ReadAsStringAsync());
        YoutubeArchiveCookie[] cookies = [new(".youtube.com", "/", "TEST_COOKIE", "dummy-cookie-value", true, true)];
        string metadataBody = System.Text.Json.JsonSerializer.Serialize(new YoutubeArchiveMetadataRequest("https://youtu.be/abcdefghijk", cookies));
        using var cookieMetadata = await http.PostAsync("/api/YoutubeArchive/Metadata",
            new StringContent(metadataBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, cookieMetadata.StatusCode);
        Assert.Equal(cookies, downloader.LastMetadataCookies);
        string archiveBody = System.Text.Json.JsonSerializer.Serialize(new YoutubeArchiveRequest("https://youtu.be/abcdefghijk", "audio", Cookies: cookies));
        using var cookieArchive = await http.PostAsync("/api/YoutubeArchive/Jobs",
            new StringContent(archiveBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, cookieArchive.StatusCode);
        string publicJob = await cookieArchive.Content.ReadAsStringAsync();
        Assert.DoesNotContain("dummy-cookie-value", publicJob);
        Assert.DoesNotContain("protectedCookies", publicJob);
        using var invalidCookies = await http.PostAsync("/api/YoutubeArchive/Metadata",
            new StringContent("""{"url":"https://youtu.be/abcdefghijk","cookies":[{"domain":".google.com","path":"/","name":"TEST_COOKIE","value":"dummy"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCookies.StatusCode);
        using var oversized = await http.PostAsync("/api/YoutubeArchive/Jobs",
            new StringContent("{\"url\":\"" + new string('x', 256 * 1024) + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task RuntimeTokensCanBeAddedRotatedAndRevokedWithoutRestarting()
    {
        using var files = new ArchiveFiles();
        using var runtimeConfiguration = new ConfigurationService(files.Root);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IConfigurationService>(runtimeConfiguration);
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, YoutubeArchiveAuthenticationHandler>(
            YoutubeArchiveAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test", () => "ok").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = YoutubeArchiveAuthenticationHandler.SchemeName
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
        string[] tokens = [new('a', 32), new('b', 64), new('c', 2048)];
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[0]);
        using var unconfigured = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, unconfigured.StatusCode);
        runtimeConfiguration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, string.Join(", ", tokens));

        foreach (string token in tokens)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", token);
            using var authorized = await http.GetAsync("/test");
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        }

        runtimeConfiguration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, tokens[1]);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[0]);
        using var revoked = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[1]);
        using var remaining = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.OK, remaining.StatusCode);
        Assert.Throws<ArgumentException>(() => runtimeConfiguration.Set(null,
            YoutubeArchiveAuthenticationHandler.SharedSecretsKey, "too-short," + tokens[1]));
        using var unchanged = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        runtimeConfiguration.Remove(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey);
        using var removed = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task StartupTokensAreIgnoredAndOnlyRuntimeTokensAuthenticate()
    {
        using var files = new ArchiveFiles();
        using var runtimeConfiguration = new ConfigurationService(files.Root);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        string startupToken = new('s', 64);
        string runtimeToken = new('r', 64);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["YoutubeArchive:SharedSecret"] = startupToken });
        builder.Services.AddSingleton<IConfigurationService>(runtimeConfiguration);
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, YoutubeArchiveAuthenticationHandler>(
            YoutubeArchiveAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test", () => "ok").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = YoutubeArchiveAuthenticationHandler.SchemeName
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", startupToken);
        using var fallback = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, fallback.StatusCode);
        runtimeConfiguration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, runtimeToken);
        using var overridden = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, overridden.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runtimeToken);
        using var runtime = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        runtimeConfiguration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, "");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", startupToken);
        using var disabled = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        runtimeConfiguration.Remove(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey);
        using var restored = await http.GetAsync("/test");
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task BrowserCookiesSurviveRestartEncryptedAndAreClearedAfterCompletion()
    {
        using var files = new ArchiveFiles();
        var downloader = new FakeDownloader();
        YoutubeArchiveCookie[] cookies = [new(".youtube.com", "/", "TEST_COOKIE", "dummy-cookie-value", true, true)];
        Guid id;

        using (var service = files.CreateService(downloader))
        {
            var job = service.Enqueue(new("https://youtu.be/abcdefghijk", "audio", Cookies: cookies));
            id = job.Id;
            Assert.NotNull(service.GetJob(id).ProtectedCookies);
            Assert.Null(service.GetJob(Guid.NewGuid()));
            Assert.DoesNotContain("dummy-cookie-value", File.ReadAllText(files.State));
            Assert.DoesNotContain("ProtectedCookies", System.Text.Json.JsonSerializer.Serialize(job));
        }

        using var restarted = files.CreateService(downloader);
        await restarted.StartAsync(default);
        await WaitForAsync(() => restarted.GetJob(id).Status == "completed");
        await restarted.StopAsync(default);
        Assert.Equal(cookies, downloader.LastCookies);
        Assert.Null(restarted.GetJob(id).ProtectedCookies);
        Assert.DoesNotContain("dummy-cookie-value", File.ReadAllText(files.State));
        var persisted = new SynchronizedLocalJsonStore<YoutubeArchiveState>(files.State);
        Assert.Null(persisted.Query(state => state.Jobs.Single().ProtectedCookies));
    }

    [Fact]
    public async Task FailedJobsDropBrowserCookiesAndMetadataReceivesThemWithoutPersistence()
    {
        using var files = new ArchiveFiles();
        var downloader = new FakeDownloader { Fail = true };
        YoutubeArchiveCookie[] cookies = [new(".youtube.com", "/", "TEST_COOKIE", "dummy-cookie-value", true, true)];
        using var service = files.CreateService(downloader);
        await service.GetMetadataAsync("abcdefghijk", default, cookies);
        Assert.Equal(cookies, downloader.LastMetadataCookies);
        Assert.Empty(service.GetJobs());
        var job = service.Enqueue(new("https://youtu.be/abcdefghijk", "audio", Cookies: cookies));
        await service.StartAsync(default);
        await WaitForAsync(() => service.GetJob(job.Id).Status == "failed");
        await service.StopAsync(default);
        Assert.Null(service.GetJob(job.Id).ProtectedCookies);
        Assert.DoesNotContain(files.Logs, message => message.Contains("dummy-cookie-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".youtube.com", "value", true)]
    [InlineData("music.youtube.com", "value", true)]
    [InlineData(".google.com", "value", false)]
    [InlineData("youtube.com.evil.example", "value", false)]
    [InlineData(".youtube.com", "value\ninjected", false)]
    [InlineData(".youtube.com", "value\tinjected", false)]
    [InlineData(".youtube.com", "value\0injected", false)]
    public void CookieValidationRestrictsScopeAndRejectsFileInjection(string domain, string value, bool valid)
    {
        Assert.Equal(valid, YoutubeArchiveValidation.AreCookiesValid([new(domain, "/", "TEST_COOKIE", value, true, true)]));
    }

    [Fact]
    public async Task CookieFilesPreserveAttributesAndRuntimePathChangesImmediately()
    {
        using var files = new ArchiveFiles();
        using var configuration = new ConfigurationService(files.Root);
        var downloader = new YoutubeArchiveDownloader(configuration);
        configuration.Set(null, YoutubeArchiveDownloader.CookiesFileKey, "first.txt");
        Assert.Contains("first.txt", downloader.GetArguments("abcdefghijk", []));
        configuration.Set(null, YoutubeArchiveDownloader.CookiesFileKey, "second.txt");
        Assert.Contains("second.txt", downloader.GetArguments("abcdefghijk", []));
        configuration.Remove(null, YoutubeArchiveDownloader.CookiesFileKey);
        Assert.DoesNotContain("--cookies", downloader.GetArguments("abcdefghijk", []));
        string file = await YoutubeArchiveDownloader.CreateCookiesFileAsync(
            [new(".youtube.com", "/", "TEST_COOKIE", "dummy-cookie-value", true, true)], default);

        try
        {
            Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t0\tTEST_COOKIE\tdummy-cookie-value", await File.ReadAllTextAsync(file));
            var arguments = downloader.GetArguments("abcdefghijk", ["--write-info-json"], file);
            Assert.Contains(file, arguments);
            Assert.Contains("--no-write-info-json", arguments);
            Assert.True(Array.IndexOf(arguments, "--no-write-info-json") > Array.IndexOf(arguments, "--write-info-json"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BrowserCookieFilesOverrideRuntimeFallbackAndAreAlwaysDeleted(int outcome)
    {
        using var files = new ArchiveFiles();
        using var configuration = new ConfigurationService(files.Root);
        configuration.Set(null, YoutubeArchiveDownloader.CookiesFileKey, "fallback.txt");
        string? temporary = null;
        var downloader = new YoutubeArchiveDownloader(configuration, async (arguments, _) =>
        {
            int index = Array.IndexOf(arguments, "--cookies");
            Assert.True(index >= 0);
            temporary = arguments[index + 1];
            Assert.NotEqual("fallback.txt", temporary);
            Assert.True(File.Exists(temporary));
            Assert.Contains("dummy-cookie-value", await File.ReadAllTextAsync(temporary));

            if (outcome == 1)
            {
                throw new InvalidOperationException("Simulated downloader failure.");
            }

            if (outcome == 2)
            {
                throw new OperationCanceledException();
            }

            return """{"title":"Test","categories":["Music"],"live_status":"not_live"}""";
        });
        YoutubeArchiveCookie[] cookies = [new(".youtube.com", "/", "TEST_COOKIE", "dummy-cookie-value", true, true)];
        Func<Task> run = () => downloader.GetMetadataAsync("abcdefghijk", default, cookies);

        if (outcome == 1)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(run);
        }
        else if (outcome == 2)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(run);
        }
        else
        {
            await run();
        }

        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task InvalidTokenFileReloadKeepsTheLastValidConfiguration()
    {
        using var files = new ArchiveFiles();
        var errors = new List<string>();
        using var configuration = new ConfigurationService(files.Root, message =>
        {
            lock (errors)
            {
                errors.Add(message);
            }
        });
        string token = new('a', 64);
        configuration.Set(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, token);
        File.WriteAllText(Path.Combine(files.Root, "GlobalConfiguration.json"), """{"YoutubeArchive.SharedSecrets":"short"}""");
        await WaitForAsync(() =>
        {
            lock (errors)
            {
                return errors.Count > 0;
            }
        });
        Assert.True(configuration.TryGet(null, YoutubeArchiveAuthenticationHandler.SharedSecretsKey, out string unchanged));
        Assert.Equal(token, unchanged);
    }

    [Fact]
    public async Task DashboardShowsAllActiveJobsAndBoundedRecentHistoryWithoutExposingCookies()
    {
        using var files = new ArchiveFiles();
        var store = new SynchronizedLocalJsonStore<YoutubeArchiveState>(files.State);
        Guid activeId = Guid.NewGuid();
        store.Modify(state =>
        {
            for (int i = 0; i < 60; i++)
            {
                state.Jobs.Add(new YoutubeArchiveJob
                {
                    VideoId = "abcdefghijk", Mode = "audio", Status = "completed",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
                });
            }

            state.Jobs.Add(new YoutubeArchiveJob
            {
                Id = activeId, VideoId = "12345678901", Mode = "video", MaxHeight = 720, Status = "running",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), ProtectedCookies = "never-show-this-cookie"
            });
            state.Jobs.Add(new YoutubeArchiveJob
            {
                VideoId = "abcdefghijk", Mode = "audio", Status = "queued",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            state.Jobs.Add(new YoutubeArchiveJob
            {
                VideoId = "abcdefghijk", Mode = "audio", Status = "failed", Error = "<script>alert('error')</script>",
                CreatedAt = DateTimeOffset.UtcNow
            });
        });
        using var service = files.CreateService(new FakeDownloader());
        var snapshot = service.GetDashboard();
        Assert.Equal(1, snapshot.Running);
        Assert.Equal(1, snapshot.Queued);
        Assert.Equal(60, snapshot.Completed);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(52, snapshot.Jobs.Length);
        Assert.Equal(activeId, snapshot.Jobs[0].Id);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(service);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new DashboardRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(MihuBot.Components.Pages.YoutubeArchive), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered.ToHtmlString();
        });
        Assert.Contains("1 running", html);
        Assert.Contains("1 queued", html);
        Assert.Contains("720p max", html);
        Assert.Contains("<tbody>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("never-show-this-cookie", html);
        Assert.Contains(activeId.ToString(), html);
    }

    [Fact]
    public void DashboardRequiresAdminAndHandlesUnavailableIntegration()
    {
        Type page = typeof(MihuBot.Components.Pages.YoutubeArchive);
        Assert.Equal("Admin", Assert.Single(page.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Equal("/youtube-archive", Assert.Single(page.GetCustomAttributes<RouteAttribute>()).Template);
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(typeof(YoutubeArchiveService), OptionalDependencies.GetMissingInjectedService(provider, page));
    }

    private static async Task WaitForAsync(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!completed())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class DashboardRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            componentActivator.CreateInstance(componentType);
    }

    private sealed class FakeDownloader : IYoutubeArchiveDownloader
    {
        public int Downloads;
        public bool Fail;
        public bool Block;

        public YoutubeArchiveCookie[]? LastCookies;
        public YoutubeArchiveCookie[]? LastMetadataCookies;

        public Task<YoutubeArchiveMetadata> GetMetadataAsync(string videoId, CancellationToken cancellationToken, YoutubeArchiveCookie[]? cookies = null)
        {
            LastMetadataCookies = cookies;
            return Task.FromResult(new YoutubeArchiveMetadata("title", true, false));
        }

        public async Task<string> DownloadAsync(YoutubeArchiveJob job, string directory, CancellationToken cancellationToken, YoutubeArchiveCookie[]? cookies = null)
        {
            Downloads++;
            LastCookies = cookies;

            if (Block)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Fail)
            {
                throw new InvalidOperationException("Download rejected.");
            }

            string channelDirectory = Path.Combine(directory, "Test Channel");
            Directory.CreateDirectory(channelDirectory);
            string file = Path.Combine(channelDirectory, job.Mode == "audio" ? "media.opus" : "media.mkv");
            await File.WriteAllTextAsync(file, "media", Encoding.UTF8, cancellationToken);
            return file;
        }
    }

    private sealed class ArchiveFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mihubot-youtube-test-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(Root, "queue.json");
        public List<string> Logs { get; } = [];

        public ArchiveFiles() => Directory.CreateDirectory(Root);

        public YoutubeArchiveService CreateService(IYoutubeArchiveDownloader downloader) =>
            new(Root, State, downloader, message => Logs.Add(message));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
