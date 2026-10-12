namespace MihuBot.Tests;

public sealed class LoggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UneditedMessageUpdatesDoNotTriggerBackups(bool hasPreviousMessage)
    {
        Assert.False(Logger.IsGenuineMessageEdit(null, null, hasPreviousMessage, Now));
    }

    [Fact]
    public void CachedEmbedRefreshDoesNotTriggerBackupsEvenAfterARecentEdit()
    {
        DateTimeOffset editedAt = Now.AddMinutes(-1);

        Assert.False(Logger.IsGenuineMessageEdit(editedAt, editedAt, true, Now));
    }

    [Fact]
    public void UncachedHistoricalEditDoesNotTriggerBackups()
    {
        Assert.False(Logger.IsGenuineMessageEdit(Now.AddYears(-5), null, false, Now));
    }

    [Theory]
    [InlineData(-601, false)]
    [InlineData(-600, true)]
    [InlineData(-599, true)]
    [InlineData(0, true)]
    public void UncachedEditsUseATenMinuteAgeLimit(int ageSeconds, bool expected)
    {
        Assert.Equal(expected, Logger.IsGenuineMessageEdit(Now.AddSeconds(ageSeconds), null, false, Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenuineEditsStillTriggerBackups(bool hasPreviousMessage)
    {
        Assert.True(Logger.IsGenuineMessageEdit(Now.AddMinutes(-1), null, hasPreviousMessage, Now));
        Assert.True(Logger.IsGenuineMessageEdit(Now.AddMinutes(-1), Now.AddYears(-5), hasPreviousMessage, Now));
    }

    [Fact]
    public void CachedEditsReplayedAfterReconnectStillTriggerBackups()
    {
        Assert.True(Logger.IsGenuineMessageEdit(Now.AddMinutes(-1), Now.AddYears(-5), true, Now));
    }

    [Theory]
    [InlineData("attachments/123/456/file.png")]
    [InlineData("ephemeral-attachments/123/456/file.png")]
    public void AttachmentDeduplicationIgnoresRefreshedSignatures(string path)
    {
        string original = path + "?ex=old&is=old&hm=old";
        string refreshed = path + "?ex=new&is=new&hm=new";
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Logger.NormalizeCdnLink(original)
        };

        Assert.Equal(path, Logger.NormalizeCdnLink(original));
        Assert.False(keys.Add(Logger.NormalizeCdnLink(refreshed)));
        Assert.False(keys.Add(Logger.NormalizeCdnLink(path)));
        Assert.NotEqual(Logger.NormalizeCdnLink(original), Logger.NormalizeCdnLink("attachments/123/789/file.png"));
    }

    [Theory]
    [InlineData("attachments/123/456/file.png#fragment", "attachments/123/456/file.png")]
    [InlineData("ATTACHMENTS/123/456/file.png?ex=old", "ATTACHMENTS/123/456/file.png")]
    [InlineData("avatars/123/avatar.png?size=128", "avatars/123/avatar.png?size=128")]
    public void CdnNormalizationPreservesOtherResources(string link, string expected)
    {
        Assert.Equal(expected, Logger.NormalizeCdnLink(link));
        Assert.Equal(expected, Logger.NormalizeCdnLink(expected));
    }
}
