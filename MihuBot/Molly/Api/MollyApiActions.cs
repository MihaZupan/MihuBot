#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>The <see cref="MollyApiRequest.Action"/> values the server understands.</summary>
public static class MollyApiActions
{
    public const string Login = "login";
    public const string Associate = "associate";
    public const string Ping = "ping";
    public const string Alert = "alert";
}
