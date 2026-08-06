using System.Globalization;
using System.Text;
using MihuBot.Molly;
using MihuBot.Molly.Alerts;

namespace MihuBot.Tests.Molly;

public sealed class MollyAlertTests : IClassFixture<MollyServiceFixture>
{
    private readonly MollyServiceFixture _fixture;
    private MollyService Molly => _fixture.Service;

    public MollyAlertTests(MollyServiceFixture fixture) => _fixture = fixture;

    private static byte[] Payload(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>A well formed location alert, as the app would send it.</summary>
    private static byte[] LocationPayload(string id, double latitude = 51.5007, double longitude = -0.1246) =>
        Payload($$$"""{"id":"{{{id}}}","type":"location","data":{"latitude":{{{latitude}}},"longitude":{{{longitude}}}}}""");

    private async Task<(string Token, Guid Id)> RegisterAsync(string nickname = "alert-user")
    {
        MollyLoginResult login = await Molly.LoginAsync(MollyTestKeys.NewKeyHash(), default);
        await Molly.AssociateAsync(login.ProtectedId, $"{nickname}-{Guid.NewGuid():N}"[..32], default);

        return (login.ProtectedId!, _fixture.Unprotect(login.ProtectedId));
    }

    [Fact]
    public async Task LocationAlert_IsStoredAndSummarisedOnTheDashboard()
    {
        (string token, Guid id) = await RegisterAsync();

        MollyCommandResult result = await Molly.SubmitAlertAsync(token, LocationPayload(token, 12.5, 56.25), default);

        Assert.Equal(MollyResultStatus.Ok, result.Status);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.Equal("12.5, 56.25", alert.Summary);
    }

    [Fact]
    public async Task LocationAlert_IncludesAccuracyWhenReported()
    {
        (string token, Guid id) = await RegisterAsync();

        byte[] payload = Payload(
            $$$"""{"id":"{{{token}}}","type":"location","data":{"latitude":1,"longitude":2,"accuracy":7.5}}""");

        await Molly.SubmitAlertAsync(token, payload, default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal("1, 2 (±7.5m)", alert.Summary);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("Location")]
    [InlineData("LOCATION")]
    public async Task AlertType_IsMatchedCaseInsensitively(string type)
    {
        (string token, Guid id) = await RegisterAsync();

        byte[] payload = Payload(
            $$$"""{"id":"{{{token}}}","type":"{{{type}}}","data":{"latitude":1,"longitude":2}}""");

        await Molly.SubmitAlertAsync(token, payload, default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Location, alert.Type);
    }

    [Fact]
    public async Task LocationAlert_DoesNotLosePrecision()
    {
        (string token, Guid id) = await RegisterAsync();

        // More decimals than any GPS actually resolves, to prove nothing is silently rounded away.
        const double Latitude = 51.50073456789;
        const double Longitude = -0.12345678901234;

        await Molly.SubmitAlertAsync(token, LocationPayload(token, Latitude, Longitude), default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);

        Assert.Contains(Latitude.ToString(CultureInfo.InvariantCulture), alert.Summary);
        Assert.Contains(Longitude.ToString(CultureInfo.InvariantCulture), alert.Summary);
        Assert.Contains($"mlat={Latitude.ToString(CultureInfo.InvariantCulture)}", alert.MapUrl);
        Assert.Contains($"mlon={Longitude.ToString(CultureInfo.InvariantCulture)}", alert.MapUrl);
    }

    [Fact]
    public async Task LocationAlert_LinksToAMap()
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SubmitAlertAsync(token, LocationPayload(token, 51.5007, -0.1246), default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);

        Assert.NotNull(alert.MapUrl);
        Assert.StartsWith("https://www.openstreetmap.org/", alert.MapUrl);

        // Coordinates have to be invariant, not formatted for the server's locale.
        Assert.Contains("mlat=51.5007", alert.MapUrl);
        Assert.Contains("mlon=-0.1246", alert.MapUrl);
    }

    [Fact]
    public async Task LocationAlert_WithoutAnAlertType_HasNoMapLink()
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SubmitAlertAsync(token, Payload($$"""{"id":"{{token}}","battery":15}"""), default);

        Assert.Null(Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id).MapUrl);
    }

    [Fact]
    public async Task MutedDevice_StillHasItsAlertsStored()
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SetAlertsMutedAsync(id, muted: true);

        Assert.True((await _fixture.GetEntryAsync(id)).AlertsMuted);
        Assert.True(Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == id).AlertsMuted);

        MollyCommandResult result = await Molly.SubmitAlertAsync(token, LocationPayload(token), default);

        // Muting only silences the notification, the alert itself is kept.
        Assert.Equal(MollyResultStatus.Ok, result.Status);
        Assert.Equal(1, await _fixture.CountAlertsAsync(id));

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.NotNull(alert.MapUrl);
    }

    [Fact]
    public async Task Muting_CanBeUndone()
    {
        (_, Guid id) = await RegisterAsync();

        await Molly.SetAlertsMutedAsync(id, muted: true);
        await Molly.SetAlertsMutedAsync(id, muted: false);

        Assert.False((await _fixture.GetEntryAsync(id)).AlertsMuted);
    }

    [Fact]
    public async Task Muting_DoesNotAffectOtherDevices()
    {
        (_, Guid muted) = await RegisterAsync("muted");
        (_, Guid other) = await RegisterAsync("other");

        await Molly.SetAlertsMutedAsync(muted, muted: true);

        Assert.True((await _fixture.GetEntryAsync(muted)).AlertsMuted);
        Assert.False((await _fixture.GetEntryAsync(other)).AlertsMuted);
    }

    [Fact]
    public async Task Muting_IsAvailableForWipedDevices()
    {
        (_, Guid id) = await RegisterAsync();

        await Molly.RequestWipeAsync(id);
        await Molly.SetAlertsMutedAsync(id, muted: true);

        // A wiped device may well keep reporting, so silencing it has to stay possible.
        Assert.True((await _fixture.GetEntryAsync(id)).AlertsMuted);
    }

    [Fact]
    public async Task NewDevices_AreNotMuted()
    {
        (_, Guid id) = await RegisterAsync();

        Assert.False((await _fixture.GetEntryAsync(id)).AlertsMuted);
    }

    [Fact]
    public async Task Alert_DoesNotStoreTheSessionToken()
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SubmitAlertAsync(token, LocationPayload(token), default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);

        Assert.DoesNotContain(token, alert.Payload);
        Assert.DoesNotContain("\"id\"", alert.Payload);

        // The rest of the payload survives untouched.
        Assert.Contains("latitude", alert.Payload);
        Assert.Equal(MollyAlertType.Location, alert.Type);
    }

    [Fact]
    public async Task Alert_KeepsUnknownPropertiesWhenStrippingTheId()
    {
        (string token, Guid id) = await RegisterAsync();

        byte[] payload = Payload($$"""{"id":"{{token}}","battery":15,"charging":true}""");

        await Molly.SubmitAlertAsync(token, payload, default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);

        Assert.DoesNotContain(token, alert.Payload);
        Assert.Contains("battery", alert.Payload);
        Assert.Contains("charging", alert.Payload);
    }

    [Fact]
    public async Task Alert_IsEncryptedAtRest()
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SubmitAlertAsync(token, LocationPayload(token, 89.125, 179.5), default);

        byte[] stored = Assert.Single(await _fixture.GetRawAlertPayloadsAsync(id));

        Assert.False(stored.AsSpan().IndexOf("89.125"u8) >= 0, "The payload must not be stored unencrypted.");
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("")]
    public async Task Alert_WithAnUnrecognizedType_IsStoredButNotSummarised(string type)
    {
        (string token, Guid id) = await RegisterAsync();

        byte[] payload = Payload($$$"""{"id":"{{{token}}}","type":"{{{type}}}","data":{"anything":true}}""");

        Assert.Equal(MollyResultStatus.Ok, (await Molly.SubmitAlertAsync(token, payload, default)).Status);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Unknown, alert.Type);
        Assert.Null(alert.Summary);
        Assert.Contains("anything", alert.Payload);
    }

    [Fact]
    public async Task Alert_WithNoTypeAtAll_IsStored()
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SubmitAlertAsync(token, Payload($$"""{"id":"{{token}}","battery":15}"""), default);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Unknown, alert.Type);
        Assert.Contains("battery", alert.Payload);
    }

    [Fact]
    public async Task Alert_ThatIsNotJson_IsStored()
    {
        (string token, Guid id) = await RegisterAsync();

        Assert.Equal(MollyResultStatus.Ok, (await Molly.SubmitAlertAsync(token, Payload("not json at all"), default)).Status);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Unknown, alert.Type);
    }

    [Theory]
    [InlineData(91, 0)]         // Latitude out of range.
    [InlineData(-91, 0)]
    [InlineData(0, 181)]        // Longitude out of range.
    [InlineData(0, -181)]
    public async Task LocationAlert_WithImpossibleCoordinates_IsStoredButNotReported(double latitude, double longitude)
    {
        (string token, Guid id) = await RegisterAsync();

        await Molly.SubmitAlertAsync(token, LocationPayload(token, latitude, longitude), default);

        // Still recorded, but not treated as a usable location.
        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.Null(alert.MapUrl);
        Assert.Equal(1, await _fixture.CountAlertsAsync(id));
    }

    [Fact]
    public async Task LocationAlert_WithTheWrongDataShape_IsStored()
    {
        (string token, Guid id) = await RegisterAsync();

        byte[] payload = Payload($$$"""{"id":"{{{token}}}","type":"location","data":"just a string"}""");

        Assert.Equal(MollyResultStatus.Ok, (await Molly.SubmitAlertAsync(token, payload, default)).Status);

        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == id);
        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.Null(alert.Summary);
    }

    [Fact]
    public async Task Alert_RefreshesLastSeen()
    {
        (string token, Guid id) = await RegisterAsync();

        await _fixture.SetLastSeenAsync(id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5));

        await Molly.SubmitAlertAsync(token, LocationPayload(token), default);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), (await _fixture.GetEntryAsync(id)).LastSeenDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task Alert_WithAnInvalidToken_IsRejected(string? token)
    {
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.SubmitAlertAsync(token, Payload("{}"), default)).Status);
    }

    [Fact]
    public async Task Alert_WithAnEmptyPayload_IsRejected()
    {
        (string token, _) = await RegisterAsync();

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.SubmitAlertAsync(token, default, default)).Status);
    }

    [Fact]
    public async Task Alert_ThatIsTooLarge_IsRejected()
    {
        (string token, Guid id) = await RegisterAsync();

        byte[] oversized = Payload($$"""{"id":"{{token}}","data":"{{new string('a', MollyService.MaxAlertLength)}}"}""");

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.SubmitAlertAsync(token, oversized, default)).Status);
        Assert.Equal(0, await _fixture.CountAlertsAsync(id));
    }

    [Fact]
    public async Task Alert_ForAnUnknownEntry_IsRejected()
    {
        string token = new MollyIdProtector(MollyTestKeys.OtherServerKeyBytes).Protect(Guid.NewGuid());

        // A token issued under a different server key can't even be unprotected.
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.SubmitAlertAsync(token, Payload("{}"), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alert_FromABlockedDevice_IsStillStored(bool wipe)
    {
        (string token, Guid id) = await RegisterAsync();

        if (wipe)
        {
            await Molly.RequestWipeAsync(id);
        }
        else
        {
            await Molly.SetLockRequestedAsync(id, lockRequested: true);
        }

        MollyCommandResult result = await Molly.SubmitAlertAsync(token, LocationPayload(token), default);

        // Knowing where a locked or wiped device is, is the whole point of the feature.
        Assert.Equal(wipe ? MollyCommand.Wipe : MollyCommand.Lock, result.Command);
        Assert.Equal(1, await _fixture.CountAlertsAsync(id));
    }

    [Fact]
    public async Task DeleteExcessAlerts_KeepsTheNewestHundredPerDevice()
    {
        (string noisyToken, Guid noisyId) = await RegisterAsync("noisy");
        (string quietToken, Guid quietId) = await RegisterAsync("quiet");

        for (int i = 0; i < 105; i++)
        {
            await Molly.SubmitAlertAsync(noisyToken, Payload($$"""{"id":"{{noisyToken}}","seq":{{i}}}"""), default);
        }

        await Molly.SubmitAlertAsync(quietToken, Payload($$"""{"id":"{{quietToken}}","seq":0}"""), default);

        await Molly.DeleteExcessAlertsAsync();

        Assert.Equal(100, await _fixture.CountAlertsAsync(noisyId));
        Assert.Equal(1, await _fixture.CountAlertsAsync(quietId));

        // The oldest ones went, not the newest.
        MollyAlertInfo[] remaining = await Molly.GetRecentAlertsAsync(limit: 200);
        string[] payloads = [.. remaining.Where(a => a.EntryId == noisyId).Select(a => a.Payload)];

        Assert.Contains(payloads, p => p.Contains("\"seq\":104"));
        Assert.DoesNotContain(payloads, p => p.Contains("\"seq\":0}"));
    }

    [Fact]
    public async Task DeleteExcessAlerts_LeavesDevicesUnderTheLimitAlone()
    {
        (string token, Guid id) = await RegisterAsync();

        for (int i = 0; i < 5; i++)
        {
            await Molly.SubmitAlertAsync(token, Payload($$"""{"id":"{{token}}","seq":{{i}}}"""), default);
        }

        await Molly.DeleteExcessAlertsAsync();

        Assert.Equal(5, await _fixture.CountAlertsAsync(id));
    }

    [Fact]
    public async Task Alerts_AreDeletedWithTheirEntry()
    {
        MollyLoginResult login = await Molly.LoginAsync(MollyTestKeys.NewKeyHash(), default);
        Guid id = _fixture.Unprotect(login.ProtectedId);

        await Molly.SubmitAlertAsync(login.ProtectedId, LocationPayload(login.ProtectedId!), default);
        Assert.Equal(1, await _fixture.CountAlertsAsync(id));

        // The entry never associated a nickname, so the cleanup drops it - and its alerts with it.
        await _fixture.SetLastSeenAsync(id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        await Molly.DeleteUnassociatedEntriesAsync();

        Assert.False(await _fixture.EntryExistsAsync(id));
        Assert.Equal(0, await _fixture.CountAlertsAsync(id));
    }

    [Fact]
    public async Task GetRecentAlerts_ReturnsTheNewestFirst()
    {
        (string token, Guid id) = await RegisterAsync();

        for (int i = 0; i < 3; i++)
        {
            await Molly.SubmitAlertAsync(token, Payload($$"""{"id":"{{token}}","seq":{{i}}}"""), default);
        }

        MollyAlertInfo[] alerts = [.. (await Molly.GetRecentAlertsAsync()).Where(a => a.EntryId == id)];

        Assert.Equal(3, alerts.Length);
        Assert.Contains("\"seq\":2", alerts[0].Payload);
    }
}
