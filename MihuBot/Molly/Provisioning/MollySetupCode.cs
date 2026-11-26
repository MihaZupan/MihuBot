using System.Buffers.Text;
using QRCoder;

#nullable enable

namespace MihuBot.Molly.Provisioning;

/// <summary>
/// A setup code (Provisioning/README.md): everything a new device needs to reach this server and register, shown as a QR
/// code or entered by hand.
/// </summary>
/// <param name="ApiUrl">The absolute URL of the Molly endpoint, e.g. <c>https://mihubot.xyz/api/molly</c>.</param>
/// <param name="Key">Unpadded base64url of the static X25519 discovery key the device pins.</param>
/// <param name="Token">The registration token, unpadded base64url.</param>
public sealed record MollySetupCode(string ApiUrl, string Key, string Token)
{
    public const string Prefix = "molly2://setup";

    /// <param name="discoveryPublicKey">Standard base64 of the discovery key, as in <see cref="Api.MollyTransportKeyResponse"/>.</param>
    public static MollySetupCode Create(string apiUrl, string discoveryPublicKey, string token) =>
        new(apiUrl, Base64Url.EncodeToString(Convert.FromBase64String(discoveryPublicKey)), token);

    /// <summary>The text encoded in the QR code.</summary>
    public string Text => $"{Prefix}?url={Uri.EscapeDataString(ApiUrl)}&key={Key}&token={Token}";

    /// <summary>Renders <see cref="Text"/> as a PNG QR code, as a <c>data:</c> URI.</summary>
    public string ToQrCodeDataUri()
    {
        using QRCodeData data = QRCodeGenerator.GenerateQrCode(Text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 6);

        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }
}
