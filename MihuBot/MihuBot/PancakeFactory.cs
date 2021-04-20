using Microsoft.Extensions.Hosting;
using MihuBot.Helpers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public sealed class PancakeFactory : IHostedService
    {
        private readonly InitializedDiscordClient _discord;
        private Timer _workTimer;

        public PancakeFactory(InitializedDiscordClient discord)
        {
            _discord = discord;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _discord.EnsureInitializedAsync();
            _workTimer = new Timer(_ => Task.Run(OnWorkTimerAsync), this, TimeSpan.Zero, TimeSpan.FromSeconds(303));
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _workTimer.DisposeAsync();
        }

        private async Task OnWorkTimerAsync()
        {
            try
            {
                var channel = _discord.GetTextChannel(Channels.DDsBotleeng);
                if (channel is null)
                    return;

                if (!channel.GetUser(_discord.CurrentUser.Id).GetPermissions(channel).SendMessages)
                    return;

                await channel.SendMessageAsync("p!work");

                if (Rng.Chance(4))
                {
                    await Task.Delay(2000);
                    await channel.SendMessageAsync("p!deposit all");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
