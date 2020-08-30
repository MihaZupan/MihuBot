using MihuBot.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class RoverCommand : CommandBase
    {
        public override string Command => "rover";

        protected override int CooldownToleranceCount => 0;
        protected override TimeSpan Cooldown => TimeSpan.FromHours(1);

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            string rover = Directory.GetFiles("/home/miha/MihuBot/Rovers").Random();
            using FileStream fs = File.OpenRead(rover);
            await ctx.Channel.SendFileAsync(fs, "rover" + Path.GetExtension(rover));
        }
    }
}
