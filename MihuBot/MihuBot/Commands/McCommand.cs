using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class McCommand : CommandBase
    {
        public override string Command => "mc";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            try
            {
                if (ctx.ArgumentString.Length > 2000 || ctx.ArgumentString.Any(c => c > 127))
                {
                    await ctx.ReplyAsync("Invalid command format", mention: true);
                }
                else
                {
                    string commandResponse = await RunMinecraftCommandAsync(ctx.ArgumentString);
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
                await ctx.DebugAsync(ex.ToString());
            }
        }


        internal static MinecraftRCON McRCON;

        internal static async Task<string> RunMinecraftCommandAsync(string command, bool isRetry = false)
        {
            try
            {
                string mcResponse = await McRCON.SendCommandAsync(command);
                Console.WriteLine("MC: " + mcResponse);
                return mcResponse;
            }
            catch (Exception ex) when (!isRetry)
            {
                try
                {
                    McRCON = await MinecraftRCON.ConnectAsync(Secrets.MinecraftServerAddress, password: Secrets.MinecraftRconPassword);
                    return await RunMinecraftCommandAsync(command, isRetry: true);
                }
                catch (Exception ex2)
                {
                    throw new AggregateException(ex, ex2);
                }
            }
        }
    }
}
