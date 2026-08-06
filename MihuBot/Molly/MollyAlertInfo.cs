using MihuBot.Molly.Alerts;

#nullable enable

namespace MihuBot.Molly;

/// <summary>An alert as shown on the admin dashboard, with its payload decrypted.</summary>
/// <param name="Summary">A readable description of the alert, when its type is one we understand.</param>
/// <param name="MapUrl">A link to the reported position, for alerts that carry usable coordinates.</param>
public sealed record MollyAlertInfo(long Id, Guid EntryId, string Nickname, DateTime CreatedAt, MollyAlertType Type, string? Summary, string? MapUrl, string Payload);
