using System.Globalization;
using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Alerts;

/// <summary>The <c>data</c> of a <see cref="MollyAlertType.Location"/> alert.</summary>
public sealed class MollyLocationAlert
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    /// <summary>Radius in metres, if the device reported one.</summary>
    [JsonPropertyName("accuracy")]
    public double? Accuracy { get; set; }

    public bool IsValid =>
        double.IsFinite(Latitude) && Latitude is >= -90 and <= 90 &&
        double.IsFinite(Longitude) && Longitude is >= -180 and <= 180;

    /// <summary>An OpenStreetMap link with a marker on these coordinates, if they're usable.</summary>
    public string? MapUrl => IsValid
        ? string.Create(CultureInfo.InvariantCulture,
            $"https://www.openstreetmap.org/?mlat={Latitude}&mlon={Longitude}#map=16/{Latitude}/{Longitude}")
        : null;

    public override string ToString()
    {
        // The link keeps full precision; the text is rounded to keep the summary readable. Five
        // decimals is roughly metre-level, well under the accuracy any phone GPS actually reports.
        string location = string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.#####}, {Longitude:0.#####}");

        return Accuracy is { } accuracy
            ? string.Create(CultureInfo.InvariantCulture, $"{location} (±{accuracy:0.#}m)")
            : location;
    }
}
