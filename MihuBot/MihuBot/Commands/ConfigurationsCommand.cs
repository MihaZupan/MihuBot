using MihuBot.Configuration;
using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class ConfigurationsCommand : CommandBase
    {
        public override string Command => "config";
        public override string[] Aliases => new[] { "cfg", "configure", "configuration", "configurations" };

        private readonly IConfigurationService _configuration;

        public ConfigurationsCommand(IConfigurationService configuration)
        {
            _configuration = configuration;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            const string Usage = "Usage: `!config [get/set/remove] context key`";

            if (ctx.Arguments.Length < 3)
            {
                await ctx.ReplyAsync(Usage);
                return;
            }

            ulong? context;
            if (ctx.Arguments[1].Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                context = null;
            }
            else if (ulong.TryParse(ctx.Arguments[1], out ulong id))
            {
                context = id;
            }
            else
            {
                await ctx.ReplyAsync(Usage);
                return;
            }

            string key = ctx.Arguments[2];

            switch (ctx.Arguments[0].ToLowerInvariant())
            {
                case "get":
                    if (await ctx.RequirePermissionAsync("configuration.read"))
                    {
                        if (_configuration.TryGet(context, key, out string value))
                        {
                            await ctx.ReplyAsync($"`{value}`");
                        }
                        else
                        {
                            await ctx.ReplyAsync("Not found", mention: true);
                        }
                    }
                    break;

                case "set":
                    if (await ctx.RequirePermissionAsync("configuration.write"))
                    {
                        if (ctx.Arguments.Length < 4)
                        {
                            await ctx.ReplyAsync(Usage);
                            return;
                        }

                        _configuration.Set(context, key, ctx.Arguments[3]);
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    break;

                case "remove":
                    if (await ctx.RequirePermissionAsync("configuration.write"))
                    {
                        _configuration.Remove(context, key);
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    break;

                default:
                    await ctx.ReplyAsync(Usage);
                    break;
            }
        }
    }
}
