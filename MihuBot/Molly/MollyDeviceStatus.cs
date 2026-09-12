#nullable enable

namespace MihuBot.Molly;

internal sealed record MollyDeviceStatus(int? BatteryLevel = null, bool? LocationEnabled = null, string? AppVersion = null);
