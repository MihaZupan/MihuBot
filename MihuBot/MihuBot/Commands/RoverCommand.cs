using MihuBot.Helpers;

namespace MihuBot.Commands
{
    public sealed class RoverCommand : CommandBase
    {
        public override string Command => "rover";

        protected override int CooldownToleranceCount => 0;
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(5);

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            string rover = Directory.GetFiles($"{Constants.StateDirectory}/Rovers").Random();
            using FileStream fs = File.OpenRead(rover);
            await ctx.Channel.SendFileAsync(fs, "rover" + Path.GetExtension(rover));
        }
    }
}
