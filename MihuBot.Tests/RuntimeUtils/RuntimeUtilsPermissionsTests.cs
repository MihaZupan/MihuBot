using MihuBot.Configuration;
using MihuBot.RuntimeUtils;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class RuntimeUtilsPermissionsTests
{
    [Theory]
    [InlineData("RuntimeUtils.Admin.admin-user")]
    [InlineData("RuntimeUtils.AuthorizedUser.admin-user")]
    [InlineData("RuntimeUtils.AuthorizedUser.dotnet.admin-user")]
    [InlineData("RuntimeUtils.Admin.dotnet")]
    public void AdminsAndExistingAuthorizationRulesAllowJobSubmission(string key)
    {
        var configuration = new TestConfiguration();
        configuration.Set(null, key, "true");

        Assert.True(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "admin-user", 123, configuration));
    }

    [Theory]
    [InlineData("RuntimeUtils.BlockedUser.admin-user")]
    [InlineData("RuntimeUtils.BlockedUser.123")]
    public void ExplicitBlocksOverrideAdminAndAuthorizedFlags(string blockedKey)
    {
        var configuration = new TestConfiguration();
        configuration.Set(null, "RuntimeUtils.Admin.admin-user", "true");
        configuration.Set(null, "RuntimeUtils.AuthorizedUser.admin-user", "true");
        configuration.Set(null, blockedKey, "true");

        Assert.False(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "admin-user", 123, configuration));
    }

    [Theory]
    [InlineData("", "admin-user", 123)]
    [InlineData(" ", "admin-user", 123)]
    [InlineData("dotnet", "", 123)]
    [InlineData("dotnet", " ", 123)]
    [InlineData("dotnet", "admin-user", 0)]
    public void AdminsStillRequireValidIdentity(string owner, string login, long id)
    {
        var configuration = new TestConfiguration();
        configuration.Set(null, $"RuntimeUtils.Admin.{login}", "true");

        Assert.False(RuntimeUtilsService.CheckGitHubUserPermissions(owner, login, id, configuration));
    }

    [Fact]
    public void UnconfiguredOrDisabledAdminDoesNotGrantAccess()
    {
        var configuration = new TestConfiguration();
        Assert.Null(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "admin-user", 123, configuration));

        configuration.Set(null, "RuntimeUtils.Admin.admin-user", "false");
        Assert.Null(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "admin-user", 123, configuration));

        configuration.Set(null, "RuntimeUtils.Admin.another-user", "true");
        Assert.Null(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "admin-user", 123, configuration));
    }

    [Fact]
    public void CopilotPolicyRemainsSeparateFromExplicitAdminAuthorization()
    {
        var configuration = new TestConfiguration();
        long id = GitHubDataIngestionService.CopilotUserId;
        Assert.True(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "copilot", id, configuration));

        configuration.Set(null, "RuntimeUtils.AllowCopilot.dotnet", "false");
        Assert.Null(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "copilot", id, configuration));

        configuration.Set(null, "RuntimeUtils.Admin.copilot", "true");
        Assert.True(RuntimeUtilsService.CheckGitHubUserPermissions("dotnet", "copilot", id, configuration));
    }

    private sealed class TestConfiguration : IConfigurationService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public void Set(ulong? context, string key, string value)
        {
            Assert.Null(context);
            _values[key] = value;
        }

        public bool Remove(ulong? context, string key) => throw new NotSupportedException();

        public bool TryGet(ulong? context, string key, out string value)
        {
            Assert.Null(context);
            return _values.TryGetValue(key, out value!);
        }
    }
}
