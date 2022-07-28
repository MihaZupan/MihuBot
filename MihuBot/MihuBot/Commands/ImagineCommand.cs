using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MihuBot.Commands;

public sealed class ImagineCommand : CommandBase
{
    public override string Command => "imagine";
    public override string[] Aliases => new[] { "dalle" };

    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
    protected override int CooldownToleranceCount => 10;

    private readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(3),
        DefaultRequestVersion = HttpVersion.Version20
    };

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.ArgumentString.Length == 0)
        {
            await ctx.ReplyAsync($"Usage: `!{ctx.Command} prompt`", mention: true);
            return;
        }

        Task addReactionTask = ctx.Message.AddReactionAsync(Emotes.Stopwatch);
        try
        {
            string requestJson = JsonSerializer.Serialize(new { prompt = ctx.ArgumentString });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://backend.craiyon.com/generate")
            {
                Content = new StringContent(requestJson, MediaTypeHeaderValue.Parse("application/json"))
            };

            request.Headers.TryAddWithoutValidation("accept", "application/json");
            request.Headers.TryAddWithoutValidation("accept-language", "en-GB,en-US;q=0.9,en;q=0.8,cs;q=0.7");
            request.Headers.TryAddWithoutValidation("origin", "https://www.craiyon.com");
            request.Headers.TryAddWithoutValidation("sec-ch-ua", ".Not/A)Brand\";v=\"99\", \"Google Chrome\";v=\"103\", \"Chromium\";v=\"103");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "Windows");
            request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
            request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
            request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
            request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/103.0.0.0 Safari/537.36");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            ResponseModel responseModel = await response.Content.ReadFromJsonAsync<ResponseModel>();

            if (responseModel.Images.Length == 9)
            {
                Image<Rgba32> newImage = null;
                try
                {
                    for (int i = 0; i < responseModel.Images.Length; i++)
                    {
                        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(Convert.FromBase64String(responseModel.Images[i]));

                        newImage ??= new Image<Rgba32>(image.Width * 3, image.Height * 3);

                        image.ProcessPixelRows(newImage, (source, target) =>
                        {
                            int targetWidthOffset = (i % 3) * source.Width;
                            int targetHeightOffset = (i / 3) * source.Height;

                            for (int i = 0; i < source.Height; i++)
                            {
                                Span<Rgba32> sourceRow = source.GetRowSpan(i);
                                Span<Rgba32> targetRow = target.GetRowSpan(i + targetHeightOffset).Slice(targetWidthOffset);
                                sourceRow.CopyTo(targetRow);
                            }
                        });
                    }

                    using var ms = new MemoryStream(16 * 1024);
                    newImage.SaveAsPng(ms);
                    ms.Position = 0;

                    await ctx.Channel.SendFileAsync(ms, $"{ctx.Message.Id}.png", MentionUtils.MentionUser(ctx.AuthorId));
                }
                finally
                {
                    newImage?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            await ctx.Message.AddReactionAsync(Emotes.RedCross);
            await ctx.DebugAsync(ex);
        }
        finally
        {
            await addReactionTask;
        }
    }

    private record ResponseModel(string[] Images);
}
