namespace MihuBot.Commands
{
    public sealed class McCommand : CommandBase
    {
        public override string Command => "mc";

        private readonly IConfiguration _configuration;

        public McCommand(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("mc"))
                return;

            try
            {
                if (ctx.ArgumentString.Length > 2000 || ctx.ArgumentString.Any(c => c > 127))
                {
                    await ctx.ReplyAsync("Invalid command format", mention: true);
                }
                else
                {
                    string commandResponse = await RunMinecraftCommandAsync(ctx.ArgumentString, dreamlings: ctx.Guild.Id != Guilds.RetirementHome, _configuration);
                    if (string.IsNullOrEmpty(commandResponse))
                    {
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    else
                    {
                        await ctx.ReplyAsync($"`{commandResponse}`");
                    }
                }
            }
            catch (Exception ex)
            {
                await ctx.ReplyAsync("Something went wrong :/");
                await ctx.DebugAsync(ex);
            }
        }


        internal static MinecraftRCON McRCON_Dreamlings;
        internal static MinecraftRCON McRCON_RetirementHome;

        internal static async Task<string> RunMinecraftCommandAsync(string command, bool dreamlings, IConfiguration configuration, bool isRetry = false)
        {
            try
            {
                MinecraftRCON Rcon() => dreamlings ? McRCON_Dreamlings : McRCON_RetirementHome;

                if (Rcon() is null || Rcon().Invalid)
                {
                    await ReCreateMinecraftRCONAsync(dreamlings);
                }

                string mcResponse = await Rcon().SendCommandAsync(command);
                // Console.WriteLine("MC: " + mcResponse);
                return mcResponse;
            }
            catch (Exception ex) when (!isRetry)
            {
                try
                {
                    await ReCreateMinecraftRCONAsync(dreamlings);
                    return await RunMinecraftCommandAsync(command, dreamlings, configuration, isRetry: true);
                }
                catch (Exception ex2)
                {
                    throw new AggregateException(ex, ex2);
                }
            }

            async Task ReCreateMinecraftRCONAsync(bool dreamlings)
            {
                if (dreamlings)
                {
                    McRCON_Dreamlings = await MinecraftRCON.ConnectAsync(
                        "dreamlings.io",
                        25575,
                        configuration["Minecraft:RconPassword"]);
                }
                else
                {
                    McRCON_RetirementHome = await MinecraftRCON.ConnectAsync(
                        "retirement-home.darlings.me",
                        25585,
                        configuration["Minecraft:RconPassword"]);
                }
            }
        }
    }
}
