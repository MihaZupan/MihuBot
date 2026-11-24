using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyPingResponse : MollyCommandResponse
{
    [JsonPropertyName("response")]
    public string Response => "pong";
}