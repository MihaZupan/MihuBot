using MihuBot.Discord;
using MihuBot.Discord.Permissions;

namespace MihuBot.Agents;

public sealed class AgentCommand(CopilotAgentService agent, IPermissionsService permissions) : CommandBase
{
    public override string Command => "agent";

    public override Task ExecuteAsync(CommandContext ctx) => HandlePromptAsync(ctx, ctx.ArgumentStringTrimmed);

    public override Task HandleAsync(MessageContext ctx) =>
        TryGetPrompt(ctx.Content, out string prompt) ? HandlePromptAsync(ctx, prompt) : Task.CompletedTask;

    internal static bool TryGetPrompt(string content, out string prompt)
    {
        prompt = null;

        if (!content.StartsWith("agent ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        prompt = content[6..].Trim();
        return true;
    }

    private async Task HandlePromptAsync(MessageContext ctx, string prompt)
    {
        if (!permissions.HasPermission(CopilotAgentService.Permission, ctx.AuthorId))
        {
            await ctx.ReplyAsync("The `agent.run` permission is required to run local agents.", suppressMentions: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            await ctx.ReplyAsync("Usage: `agent <task>`. Runs have a thirty-minute limit and files are deleted afterwards.", suppressMentions: true);
            return;
        }

        var progress = new AgentProgress();
        var status = await ctx.Channel.SendMessageAsync(embed: progress.Render(TimeSpan.Zero), allowedMentions: AllowedMentions.None);
        using var progressCancellation = new CancellationTokenSource();
        Task progressTask = progress.RunUpdatesAsync(
            embed => status.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.AllowedMentions = AllowedMentions.None;
            }),
            ex => ctx.DebugLog(ex, "Agent progress update failed"),
            progressCancellation.Token);
        string response;

        try
        {
            response = await agent.RunAsync(ctx, prompt, progress.OnEvent);
        }
        catch (CopilotAgentService.AgentBusyException)
        {
            response = "Another agent is already running. Please try again after it finishes.";
        }
        catch (CopilotAgentService.AgentTimeoutException ex)
        {
            ctx.DebugLog(ex, "Agent timed out");
            response = "The agent exceeded its thirty-minute time limit and was stopped.";
        }
        catch (OperationCanceledException)
        {
            response = "Agent cancelled.";
        }
        catch (Exception ex)
        {
            ctx.DebugLog(ex, "Agent failed");
            response = "The agent failed. Details were logged for the bot administrator.";
        }
        finally
        {
            await progressCancellation.CancelAsync();
            // Let any in-flight Discord edit finish before sending the final response.
            await progressTask;
        }

        Embed[] usageEmbeds = progress.RenderUsage() is { } usageEmbed ? [usageEmbed] : [];

        if (response.Length <= 2000)
        {
            await status.ModifyAsync(m =>
            {
                m.Content = response;
                m.Embeds = usageEmbeds;
                m.AllowedMentions = AllowedMentions.None;
            });
        }
        else
        {
            using var file = new MemoryStream(Encoding.UTF8.GetBytes(response));
            await ctx.Channel.SendFileAsync(file, "agent-response.txt", allowedMentions: AllowedMentions.None);
            await status.ModifyAsync(m =>
            {
                m.Content = "Agent finished. The full response is attached below.";
                m.Embeds = usageEmbeds;
                m.AllowedMentions = AllowedMentions.None;
            });
        }
    }
}
