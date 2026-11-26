using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyAssociateRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    /// <summary>The Signal identity to bind the entry to, if any (Sharing/README.md, "Identity binding").</summary>
    [JsonPropertyName("identity")]
    public MollyAssociateIdentity? Identity { get; set; }
}

/// <summary>A Signal identity proven within the exchange. Byte fields are standard padded base64.</summary>
public sealed class MollyAssociateIdentity
{
    /// <summary>The 16-byte ACI.</summary>
    [JsonPropertyName("aci")]
    public string? Aci { get; set; }

    /// <summary>The 33-byte Signal identity public key.</summary>
    [JsonPropertyName("identityKey")]
    public string? IdentityKey { get; set; }

    /// <summary>The 64-byte identity proof MAC for action <c>associate</c> and an empty body.</summary>
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }
}
