#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>The <see cref="MollyApiRequest.Action"/> values the server understands.</summary>
public static class MollyApiActions
{
    public const string TransportKey = "transport-key";
    public const string Login = "login";
    public const string Associate = "associate";
    public const string Ping = "ping";
    public const string Alert = "alert";
    public const string RegistrationToken = "registration-token";

    // File sharing, see FileSharing.md.
    public const string ShareReserve = "share-reserve";
    public const string ShareCreate = "share-create";
    public const string ShareUpdate = "share-update";
    public const string ShareSync = "share-sync";
    public const string ShareGet = "share-get";
    public const string ShareStart = "share-start";
    public const string ShareApprove = "share-approve";
    public const string ShareFetch = "share-fetch";
    public const string ShareDelete = "share-delete";
    public const string ShareDismiss = "share-dismiss";
}
