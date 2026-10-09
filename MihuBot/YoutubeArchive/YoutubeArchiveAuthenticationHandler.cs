using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;

namespace MihuBot.YoutubeArchive;

public sealed class YoutubeArchiveAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "YoutubeArchive";
    public const string SharedSecretsKey = "YoutubeArchive.SharedSecrets";
    private readonly IConfigurationService _runtimeConfiguration;

    public YoutubeArchiveAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfigurationService runtimeConfiguration) : base(options, logger, encoder)
    {
        _runtimeConfiguration = runtimeConfiguration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        const string prefix = "Bearer ";

        if (headers.Count != 1 || headers[0] is not string header ||
            !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || header.Length > 4096)
        {
            return Task.FromResult(AuthenticateResult.Fail("A bearer token is required."));
        }

        if (_runtimeConfiguration.TryGet(null, SharedSecretsKey, out string configured))
        {
            ReadOnlySpan<char> actual = header.AsSpan(prefix.Length);
            ReadOnlySpan<char> secrets = configured.AsSpan();

            foreach (Range range in secrets.Split(','))
            {
                ReadOnlySpan<char> secret = secrets[range].Trim();

                if (!secret.IsEmpty && CryptographicOperations.FixedTimeEquals(secret, actual))
                {
                    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "YoutubeArchive")], SchemeName);
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
                }
            }
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid archive token."));
    }
}
