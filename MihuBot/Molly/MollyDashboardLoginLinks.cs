#nullable enable

namespace MihuBot.Molly;

/// <summary>Short-lived dashboard credentials, sealed under a per-process key separate from device session tokens.</summary>
public sealed class MollyDashboardLoginLinks(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly MollySealedIdTokens _tokens = MollySealedIdTokens.CreateEphemeral(urlSafe: true);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Issue(Guid entryId) => _tokens.Protect(entryId, _time.GetUtcNow().UtcDateTime);

    public bool TryGetEntryId(string? token, out Guid entryId)
    {
        if (!_tokens.TryUnprotect(token, out entryId, out DateTime issuedAt) || _time.GetUtcNow().UtcDateTime >= issuedAt + Lifetime)
        {
            entryId = Guid.Empty;
            return false;
        }

        return true;
    }
}
