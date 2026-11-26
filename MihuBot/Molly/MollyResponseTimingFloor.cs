using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// The randomized minimum time every Molly API response takes, so response times say less about how far a request got.
/// </summary>
public sealed class MollyResponseTimingFloor
{
    /// <summary>Always on in the app; tests turn it off so their many requests don't each wait.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Starts the floor for a request: 10 to 20 ms.</summary>
    public Task StartAsync() => Enabled ? Task.Delay(RandomNumberGenerator.GetInt32(10, 20), CancellationToken.None) : Task.CompletedTask;
}
