using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Alerts;

/// <summary>
/// The <c>data</c> of a <see cref="MollyAlertType.CommandAck"/> alert - the device's last-moment
/// acknowledgement of a blocking command, sent just before it locks or wipes itself.
/// </summary>
public sealed class MollyCommandAckAlert
{
    /// <summary>The acknowledged command as its wire value, e.g. <c>lock</c> or <c>wipe</c>.</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    public MollyCommand AcknowledgedCommand => MollyCommandExtensions.FromWireValue(Command);

    public bool IsValid => AcknowledgedCommand != MollyCommand.None;
}
