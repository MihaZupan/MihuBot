using GitHub.Copilot;
using System.Text.Json;

namespace MihuBot.Agents;

internal sealed class AgentProgress
{
    internal static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(3);
    private const int MaxMessageLength = 2500;
    private const int MaxToolSummaryLength = 280;
    private const int MaxRunningTools = 4;
    private const int MaxRecentTools = 6;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Agent, string Call), ToolActivity> _running = [];
    private readonly Queue<(ToolActivity Tool, bool Success)> _recent = [];
    private readonly AgentUsage _usage = new();
    private string _status = "Starting Copilot...";
    private string _messageId;
    private string _message = "";
    private int _finished;
    private int _failed;

    internal sealed record ToolActivity(string Name, string Details);

    public void OnEvent(SessionEvent evt)
    {
        lock (_lock)
        {
            switch (evt)
            {
                case AssistantUsageEvent usage:
                    _usage.Add(usage);
                    break;

                case ToolExecutionStartEvent start:
                    _status = "Using tools...";
                    _running[(evt.AgentId, start.Data.ToolCallId)] = DescribeTool(start.Data);
                    break;

                case ToolExecutionCompleteEvent complete:
                    if (_running.Remove((evt.AgentId, complete.Data.ToolCallId), out ToolActivity tool))
                    {
                        _finished++;
                        _failed += complete.Data.Success ? 0 : 1;
                        _recent.Enqueue((tool, complete.Data.Success));

                        while (_recent.Count > MaxRecentTools)
                        {
                            _recent.Dequeue();
                        }

                        if (_running.Count == 0)
                        {
                            _status = "Working...";
                        }
                    }
                    break;

                case AssistantTurnStartEvent when evt.AgentId is null:
                    _status = "Working...";
                    break;

                case AssistantMessageDeltaEvent delta when evt.AgentId is null:
                    _status = "Writing...";

                    if (_messageId != delta.Data.MessageId)
                    {
                        _messageId = delta.Data.MessageId;
                        _message = "";
                    }

                    _message = Tail(_message + Tail(delta.Data.DeltaContent));
                    break;

                case AssistantMessageEvent message when evt.AgentId is null:
                    _messageId = message.Data.MessageId;
                    _message = Tail(message.Data.Content);
                    break;

                case SessionIdleEvent when evt.AgentId is null:
                    _status = "Finishing and cleaning up...";
                    break;

                default:
                    break;
            }
        }
    }

    internal Embed Render(TimeSpan elapsed)
    {
        lock (_lock)
        {
            var embed = new EmbedBuilder()
                .WithTitle("MihuBot Agent")
                .WithDescription(_status)
                .WithColor(new Color(88, 101, 242))
                .AddField("Elapsed", $"{elapsed:mm\\:ss}", inline: true)
                .AddField("Tools", $"{_running.Count} running, {_finished} finished, {_failed} failed", inline: true);

            foreach (ToolActivity tool in _running.Values.Take(MaxRunningTools))
            {
                embed.AddField($"Running: {tool.Name}", string.IsNullOrEmpty(tool.Details) ? "In progress" : tool.Details);
            }

            if (_running.Count > MaxRunningTools)
            {
                embed.AddField("Other tools", $"{_running.Count - MaxRunningTools} more running.");
            }

            if (_recent.Count > 0)
            {
                embed.AddField("Recent activity", string.Join('\n', _recent.Select(entry => FormatRecentActivity(entry.Tool, entry.Success))));
            }

            if (!string.IsNullOrWhiteSpace(_message))
            {
                AddDetails(embed, "Latest update", _message);
            }

            if (_usage.FormatFooter() is { } footer)
            {
                embed.WithFooter(footer);
            }

            return embed.Build();
        }
    }

    internal Embed RenderUsage()
    {
        lock (_lock)
        {
            return _usage.FormatFooter() is { } footer
                ? new EmbedBuilder().WithTitle("Agent usage").WithColor(new Color(88, 101, 242)).WithFooter(footer).Build()
                : null;
        }
    }

    internal static ToolActivity DescribeTool(ToolExecutionStartData tool)
    {
        string description = GetArgument("description") ?? GetArgument("intent") ?? tool.ToolTitle;
        string target = GetArgument("query") ?? GetArgument("text") ?? GetArgument("url") ?? GetArgument("path") ??
            GetArgument("file_path") ?? GetArgument("pattern") ?? GetArgument("authorId");
        string name = Clean(tool.ToolName.Replace('_', ' ')).TruncateWithDotDotDot(64);
        string details = !string.IsNullOrWhiteSpace(description) && !description.Equals(tool.ToolName, StringComparison.OrdinalIgnoreCase)
            ? description
            : "";

        if (target is not null && !details.Contains(target, StringComparison.Ordinal))
        {
            details = string.IsNullOrEmpty(details) ? Clean(target) : $"{Clean(details)}\n\n{Clean(target)}";
        }
        else
        {
            details = Clean(details);
        }

        return new ToolActivity(name, details.TruncateWithDotDotDot(MaxToolSummaryLength - name.Length));

        static string Clean(string text) => text.Replace('`', '\'').ReplaceLineEndings(" ").Replace('\t', ' ');

        string GetArgument(string name) =>
            tool.Arguments is { ValueKind: JsonValueKind.Object } arguments &&
            arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    }

    private static string FormatRecentActivity(ToolActivity tool, bool success)
    {
        string summary = $"- {(success ? "Done" : "Failed")}: **{tool.Name}**";
        string detail = tool.Details.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

        if (!string.IsNullOrEmpty(detail))
        {
            summary += $" - {detail}";
        }

        return summary.TruncateWithDotDotDot(160);
    }

    private static void AddDetails(EmbedBuilder embed, string name, string text)
    {
        for (int offset = 0; offset < text.Length;)
        {
            int length = Math.Min(1024, text.Length - offset);

            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }

            embed.AddField(offset == 0 ? name : $"{name} (continued)", text.Substring(offset, length));
            offset += length;
        }
    }

    public async Task RunUpdatesAsync(Func<Embed, Task> update, Action<Exception> logError, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                // Delay after each completed edit, so a slow/rate-limited request cannot cause a burst of edits.
                await Task.Delay(UpdateInterval, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await update(Render(elapsed.Elapsed));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logError(ex);
        }
    }

    private static string Tail(string text)
    {
        if (text.Length <= MaxMessageLength)
        {
            return text;
        }

        int start = text.Length - MaxMessageLength + 3;

        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return "..." + text[start..];
    }
}
