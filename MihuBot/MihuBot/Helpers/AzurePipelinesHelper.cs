using System.Security.Cryptography;

namespace MihuBot.Helpers;

public static class AzurePipelinesHelper
{
    public static async Task TriggerWebhookAsync(Logger logger, HttpClient client, string organization, string webhook, string secret, string payload)
    {
        string url = $"https://dev.azure.com/{organization}/_apis/public/distributedtask/webhooks/{webhook}?api-version=6.0-preview";
        var request = new HttpRequestMessage(HttpMethod.Post, url);

        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        request.Content = new ByteArrayContent(payloadBytes);
        request.Content.Headers.Add("Content-Type", "application/json");

        byte[] hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), payloadBytes);

        request.Headers.Add("X-Webhook-Checksum", Convert.ToHexStringLower(hash));

        using HttpResponseMessage response = await client.SendAsync(request);

        string responseText = await response.Content.ReadAsStringAsync();
        logger.DebugLog($"Azure WebHook responded with: {responseText}");

        response.EnsureSuccessStatusCode();
    }
}
