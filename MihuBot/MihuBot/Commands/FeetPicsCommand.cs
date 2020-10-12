using MihuBot.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class FeetPicsCommand : CommandBase
    {
        public override string Command => "feetpics";

        protected override int CooldownToleranceCount => 0;
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        private readonly HttpClient _http;

        private Image<Rgba32> SourceImage;
        private int _counter = -1;
        private readonly List<int> _coords = new List<int>();

        public FeetPicsCommand(HttpClient httpClient)
        {
            _http = httpClient;
        }

        public override async Task InitAsync()
        {
            var response = await _http.GetAsync("https://cdn.discordapp.com/attachments/731612070843383871/731675070107353108/paul.png");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            SourceImage = Image.Load(bytes).CloneAs<Rgba32>();
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            _counter = (_counter + 1) % 64;

            if (_counter == 0)
            {
                _coords.Clear();
                for (int i = 0; i < 64; i++)
                    _coords.Add(i);
            }

            _coords.RemoveAt(Rng.Next(_coords.Count));

            await ctx.Message.Channel.SendFileAsync(CreatePartialImage(), Guid.NewGuid().ToString() + ".png");
        }

        private MemoryStream CreatePartialImage()
        {
            using var partialImage = new Image<Rgba32>(128, 128);

            for (int i = 0; i < 64; i++)
            {
                if (_coords.Contains(i)) continue;

                int rowSection = (i & 7) << 4;
                int columnSection = (i >> 3) << 4;

                for (int rowIndex = rowSection; rowIndex < rowSection + 16; rowIndex++)
                {
                    var sourceRow = SourceImage.GetPixelRowSpan(rowIndex);
                    var targetRow = partialImage.GetPixelRowSpan(rowIndex);

                    sourceRow.Slice(columnSection, 16).CopyTo(targetRow.Slice(columnSection, 16));
                }
            }

            var ms = new MemoryStream();
            partialImage.SaveAsPng(ms);
            ms.Position = 0;

            return ms;
        }
    }
}
