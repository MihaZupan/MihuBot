using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;

namespace MihuBot.Commands;

public sealed class DuplicatesCommand : CommandBase
{
    public override string Command => "duplicates";
    public override string[] Aliases => ["forcetriage", "testduplicates"];

    private readonly FileBackedHashSet _processedIssuesForDuplicateDetection = new("ProcessedIssuessForDuplicateDetection.txt");
    private readonly ConcurrentDictionary<string, (IssueInfo Issue, string DuplicatesSummary)> _duplicatesToPost = [];

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly GitHubClient _github;
    private readonly IssueTriageService _triageService;
    private readonly IssueTriageHelper _triageHelper;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;
    private readonly DiscordSocketClient _discord;
    private readonly GithubGraphQLClient _graphQL;
    private readonly OpenAIService _openAI;
    private readonly SemaphoreSlim _sempahore = new(2, 2);

    private bool SkipManualVerificationBeforePosting => _configuration.GetOrDefault(null, $"{Command}.AutoPost", false);
    private bool DoThirdVerificationCheck => _configuration.GetOrDefault(null, $"{Command}.ThirdTest", true);
    private double CertaintyThreshold => _configuration.GetOrDefault(null, $"{Command}.{nameof(CertaintyThreshold)}", 0.89d);

    public DuplicatesCommand(IDbContextFactory<GitHubDbContext> db, IssueTriageService triageService, IssueTriageHelper triageHelper, ServiceConfiguration serviceConfiguration, Logger logger, IConfigurationService configuration, GitHubClient github, DiscordSocketClient discord, GithubGraphQLClient graphQL, OpenAIService openAI)
    {
        _db = db;
        _triageService = triageService;
        _triageHelper = triageHelper;
        _serviceConfiguration = serviceConfiguration;
        _logger = logger;
        _configuration = configuration;
        _github = github;
        _discord = discord;
        _graphQL = graphQL;
        _openAI = openAI;
    }

    public override async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        if (_duplicatesToPost.TryRemove(component.Data.CustomId, out (IssueInfo Issue, string DuplicatesSummary) data))
        {
            await component.UpdateAsync(m => m.Components = null);
            await PostGhCommentSummary(data.Issue, data.DuplicatesSummary);
        }
        else if (component.Data.CustomId.EndsWith("-no", StringComparison.Ordinal))
        {
            await component.UpdateAsync(m => m.Components = null);
        }
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.IsFromAdmin)
        {
            return;
        }

        string repoName = null;

        if (ctx.Command == "testduplicates")
        {
            bool fromPosted = ctx.Arguments.Length > 0 && ctx.Arguments[0].Equals("posted", StringComparison.OrdinalIgnoreCase);
            string[] args = fromPosted ? ctx.Arguments[1..] : ctx.Arguments;

            int count = 10;

            if (args.Length > 0)
            {
                count = int.Parse(args[0]);
            }

            if (args.Length > 1)
            {
                repoName = args[1];
            }

            repoName ??= "dotnet/runtime";

            string source = fromPosted ? "previously posted issues" : "duplicate issues";
            await ctx.ReplyAsync($"Running duplicate detection backtest on last {count} {source} in {repoName}...");
            await RunDuplicateDetectionBacktestAsync(repoName, count, fromPosted, ctx);
            return;
        }

        if (ctx.Arguments.Length != 1 || !GitHubHelper.TryParseIssueOrPRNumber(ctx.Arguments[0], out repoName, out int issueNumber))
        {
            await ctx.ReplyAsync("Invalid issue/PR URL. Use the number or the full link.");
            return;
        }

        IssueInfo issue = await _triageHelper.GetIssueAsync(repoName ?? "dotnet/runtime", issueNumber, ctx.CancellationToken);

        if (issue is null)
        {
            await ctx.ReplyAsync("Issue not found in database.");
            return;
        }

        if (ctx.Command == "duplicates")
        {
            await RunDuplicateDetectionAsync(issue, automated: false, message: ctx);
        }
        else
        {
            Uri issueUrl = await _triageService.ManualTriageAsync(issue, ctx.CancellationToken);
            await ctx.ReplyAsync($"Triage completed. See the issue at <{issueUrl.AbsoluteUri}>.");
        }
    }

    public override Task InitAsync()
    {
        _ = Task.Run(async () =>
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

                while (await timer.WaitForNextTickAsync())
                {
                    try
                    {
                        if (_serviceConfiguration.PauseAutoDuplicateDetection)
                        {
                            continue;
                        }

                        await using GitHubDbContext db = _db.CreateDbContext();

                        DateTime startDate = DateTime.UtcNow.Subtract(TimeSpan.FromHours(1));

                        IQueryable<IssueInfo> query = db.Issues
                            .AsNoTracking()
                            .Where(i => i.CreatedAt >= startDate)
                            .Where(i => i.State == ItemState.Open)
                            .Where(i => i.IssueType == IssueType.Issue)
                            .Where(i => !i.Repository.InitialIngestionInProgress)
                            .OrderByDescending(i => i.CreatedAt);

                        query = IssueTriageHelper.AddIssueInfoIncludes(query);

                        IssueInfo[] issues = await query
                            .Take(100)
                            .AsSplitQuery()
                            .ToArrayAsync();

                        foreach (IssueInfo issue in issues)
                        {
                            if (!issue.User.IsLikelyARealUser() ||
                                !_configuration.GetOrDefault(null, $"{Command}.Enabled.{issue.RepoName()}", false))
                            {
                                continue;
                            }

                            if (DateTime.UtcNow.Subtract(issue.CreatedAt).TotalMinutes < 3)
                            {
                                // Give it 3 minutes before processing in case the author references the duplicate in a comment.
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(issue.Body))
                            {
                                continue;
                            }

                            if (!_processedIssuesForDuplicateDetection.TryAdd(issue.Id))
                            {
                                continue;
                            }

                            _logger.DebugLog($"{nameof(DuplicatesCommand)}: Running duplicate detection for issue <{issue.HtmlUrl}>");

                            _ = Task.Run(async () =>
                            {
                                await RunDuplicateDetectionAsync(issue, automated: true, message: null);
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync($"{nameof(DuplicatesCommand)}: Error during duplicate detection", ex);
                    }
                }
            }
            catch { }
        });

        return Task.CompletedTask;
    }

    private async Task RunDuplicateDetectionAsync(IssueInfo issue, bool automated, MessageContext message)
    {
        await _sempahore.WaitAsync();
        try
        {
            var result = await RunMultiPassDuplicateDetectionAsync(issue, CancellationToken.None);

            if (result.AllDuplicates.Length == 0)
            {
                _logger.DebugLog($"{nameof(DuplicatesCommand)}: No duplicates found for issue <{issue.HtmlUrl}>");

                if (!automated)
                {
                    await message.ReplyAsync("No duplicates found.");
                }

                return;
            }

            SocketTextChannel channel = _logger.Options.Discord.GetTextChannel(Channels.DuplicatesList);
            MessageComponent components = null;

            string reply = FormatDuplicatesSummary(issue, result.AllDuplicates, includeSummary: false);
            string summary = FormatDuplicatesSummary(issue, result.AllDuplicates);

            if (result.IssuesToReport.Length > 0)
            {
                reply = $"{MentionUtils.MentionUser(KnownUsers.Miha)} {reply}";

                bool plural = result.IssuesToReport.Length > 1;

                bool autoPost = result.WouldAutoPost && automated;

                string relationship = autoPost && await AreAllRelatedIssuesLikelyDuplicatesAsync(issue, [.. result.IssuesToReport.Select(i => i.Issue)], message?.CancellationToken ?? default)
                    ? "duplicate"
                    : "related and/or duplicate";

                string ghComment =
                    $"""
                    I'm a bot. Here {(plural ? "are" : "is a")} possible {relationship} issue{(plural ? "s" : "")} (I may be wrong):
                    {string.Join('\n', result.IssuesToReport.Select(d => $"- {d.Issue.HtmlUrl}"))}
                    """;

                if (autoPost)
                {
                    await PostGhCommentSummary(issue, ghComment);
                }
                else
                {
                    if (result.SecondaryTestDuplicates.Length == 0)
                    {
                        reply = $"**Note:** Secondary test did not find overlapping useful duplicates.\n\n{reply}";

                        summary = $"{summary}\n\nSecondary:\n{FormatDuplicatesSummary(issue, result.SecondaryTestDuplicates)}";
                    }
                    else if (DoThirdVerificationCheck && result.ThirdTestDuplicates.Length == 0)
                    {
                        reply = $"**Note:** Third test did not find overlapping useful duplicates.\n\n{reply}";

                        summary = $"{summary}\n\nThird:\n{FormatDuplicatesSummary(issue, result.ThirdTestDuplicates)}";
                    }

                    string id = $"{Command}-{Snowflake.Next()}";
                    _duplicatesToPost.TryAdd(id, (issue, ghComment));

                    components = new ComponentBuilder()
                        .WithButton("Post", id, ButtonStyle.Success)
                        .WithButton("Cancel", $"{Command}-no", ButtonStyle.Danger)
                        .Build();
                }
            }

            reply = reply.TruncateWithDotDotDot(1800);

            await (message?.Channel ?? channel).SendTextFileAsync($"Duplicates-{issue.Number}.txt", summary, reply, components);
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"{nameof(DuplicatesCommand)}: Error during duplicate detection for issue <{issue.HtmlUrl}>", ex);
        }
        finally
        {
            _sempahore.Release();
        }
    }

    private sealed record MultiPassResult(
        (IssueInfo Issue, double Certainty, string Summary)[] AllDuplicates,
        (IssueInfo Issue, double Certainty, string Summary)[] SecondaryTestDuplicates,
        (IssueInfo Issue, double Certainty, string Summary)[] ThirdTestDuplicates,
        (IssueInfo Issue, double Certainty, string Summary)[] IssuesToReport,
        bool WouldAutoPost);

    private async Task<MultiPassResult> RunMultiPassDuplicateDetectionAsync(IssueInfo issue, CancellationToken cancellationToken)
    {
        double certaintyThreshold = CertaintyThreshold;

        // First pass (no reasoning)
        (IssueInfo Issue, double Certainty, string Summary)[] duplicates = await DetectIssueDuplicatesAsync(issue, allowReasoning: false, cancellationToken);

        (IssueInfo Issue, double Certainty, string Summary)[] issuesToReport = [.. duplicates.Where(d => IsLikelyUsefulToReport(issue, d.Issue, d.Certainty, certaintyThreshold))];

        if (issuesToReport.Length == 0)
        {
            return new MultiPassResult(duplicates, [], [], [], WouldAutoPost: false);
        }

        // Second pass (with reasoning, reusing candidates from first pass)
        var secondaryTest = await DetectIssueDuplicatesAsync(issue, allowReasoning: true, cancellationToken, [.. issuesToReport.Select(i => i.Issue)]);

        issuesToReport = [.. issuesToReport.Where(d => secondaryTest.Any(s => s.Issue.Id == d.Issue.Id && IsLikelyUsefulToReport(issue, s.Issue, s.Certainty, certaintyThreshold)))];

        if (issuesToReport.Length == 0)
        {
            return new MultiPassResult(duplicates, secondaryTest, [], [], WouldAutoPost: false);
        }

        // Third pass (with reasoning, optional, reusing candidates from previous passes)
        (IssueInfo Issue, double Certainty, string Summary)[] thirdTestDuplicates = [];
        if (DoThirdVerificationCheck)
        {
            thirdTestDuplicates = await DetectIssueDuplicatesAsync(issue, allowReasoning: true, cancellationToken, [.. issuesToReport.Select(i => i.Issue)]);

            issuesToReport = [.. issuesToReport.Where(d => thirdTestDuplicates.Any(t => t.Issue.Id == d.Issue.Id && IsLikelyUsefulToReport(issue, t.Issue, t.Certainty, certaintyThreshold)))];
        }

        bool wouldAutoPost =
            SkipManualVerificationBeforePosting &&
            issuesToReport.Length > 0 &&
            await ShouldAutoPostAsync(issue, [.. issuesToReport.Select(i => i.Issue)]);

        return new MultiPassResult(duplicates, secondaryTest, thirdTestDuplicates, issuesToReport, wouldAutoPost);
    }

    private static bool IsLikelyUsefulToReport(IssueInfo issue, IssueInfo duplicate, double certainty, double certaintyThreshold)
    {
        if (certainty < certaintyThreshold)
        {
            return false;
        }

        if (duplicate.UserId == issue.UserId && duplicate.UserId != GitHubDataIngestionService.GhostUserId)
        {
            // Same author? They're likely aware of the other issue.
            return false;
        }

        if (string.IsNullOrEmpty(issue.Title))
        {
            // Shouldn't really happen?
            return false;
        }

        if (duplicate.PullRequest is not null)
        {
            // Let's not report it if only PRs are duplicates.
            return false;
        }

        if (duplicate.CreatedAt >= issue.CreatedAt)
        {
            // Maybe processing old backlog?
            return false;
        }

        if (AreIssuesAlreadyLinked(issue, duplicate))
        {
            // Already linked in some way.
            return false;
        }

        return true;
    }

    private static bool AreIssuesAlreadyLinked(IssueInfo issue, IssueInfo duplicate)
    {
        if (MentionsIssue(issue.Title, duplicate) || MentionsIssue(issue.Body, duplicate))
        {
            return true;
        }

        if (issue.Comments is not null && issue.Comments.Any(c => MentionsIssue(c.Body, duplicate)))
        {
            return true;
        }

        if (duplicate.Comments is not null && duplicate.Comments.Any(c => MentionsIssue(c.Body, issue)))
        {
            return true;
        }

        return false;
    }

    private static bool MentionsIssue(string text, IssueInfo issue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains($"#{issue.Number}", StringComparison.Ordinal)
            || text.Contains($" {issue.Number} ", StringComparison.Ordinal)
            || text.Contains($" {issue.Number}.", StringComparison.Ordinal)
            || text.Contains(issue.HtmlUrl, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ShouldAutoPostAsync(IssueInfo issue, IssueInfo[] issuesToReport)
    {
        try
        {
            Issue ghIssue = await _github.Issue.Get(issue.Repository.Id, issue.Number);

            if (ghIssue.State == ItemState.Closed)
            {
                return false;
            }

            if (ghIssue.Assignee?.Id == GitHubDataIngestionService.CopilotUserId)
            {
                // Copilot assigned to issue.
                return false;
            }

            IReadOnlyList<IssueComment> ghComments = await _github.Issue.Comment.GetAllForIssue(issue.Repository.Id, issue.Number);

            if (issuesToReport.All(dupe => ghComments.Any(c => MentionsIssue(c.Body, dupe))))
            {
                // Someone beat us to it.
                return false;
            }

            try
            {
                IReadOnlyList<Issue> subIssues = await _github.GetAllSubIssuesAsync(issue.RepositoryId, issue.Number, new ApiOptions { PageCount = 1, PageSize = 1 });

                if (subIssues.Count > 0)
                {
                    // Related tasks seem to already be known.
                    return false;
                }

                foreach (IssueInfo dupe in issuesToReport)
                {
                    if (dupe.IssueType == IssueType.Discussion)
                    {
                        continue;
                    }

                    subIssues = await _github.GetAllSubIssuesAsync(dupe.RepositoryId, dupe.Number);

                    if (subIssues.Any(i => i.NodeId == issue.Id))
                    {
                        // The new issue is already referenced as a sub-issue of the duplicate.
                        return false;
                    }
                }
            }
            catch (Exception ex) when (ex is not NotFoundException)
            {
                await _logger.DebugAsync($"Failed to fetch sub-issues for <{issue.HtmlUrl}>", ex);
            }
        }
        catch (NotFoundException)
        {
            return false;
        }

        return true;
    }

    private async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectIssueDuplicatesAsync(IssueInfo issue, bool allowReasoning, CancellationToken cancellationToken, IssueInfo[] candidateIssues = null)
    {
        ModelInfo model = _triageHelper.DefaultModel;

        if (_configuration.TryGet(null, "Duplicates.Model", out string modelName))
        {
            model = OpenAIService.AllModels.FirstOrDefault(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)) ?? model;
        }

        var options = new IssueTriageHelper.TriageOptions(model, "MihaZupan", issue, OnToolLog: log => _logger.DebugLog($"[Duplicates {issue.Repository.FullName}#{issue.Number}]: {log}"), SkipCommentsOnCurrentIssue: true, AllowReasoning: allowReasoning);

        int attemptCount = 0;
        while (true)
        {
            try
            {
                return await _triageHelper.DetectDuplicateIssuesAsync(options, cancellationToken, candidateIssues);
            }
            catch (JsonException) when (++attemptCount < 3) { }
        }
    }

    private static string FormatDuplicatesSummary(IssueInfo issue, (IssueInfo Issue, double Certainty, string Summary)[] duplicates, bool includeSummary = true)
    {
        return $"Duplicate issues for [{issue.Repository.FullName}#{issue.Number}](<{issue.HtmlUrl}>) - {issue.Title}:\n" +
            string.Join('\n', duplicates.Select(r => $"- ({r.Certainty:F2}) [#{r.Issue.Number} - {r.Issue.Title}](<{r.Issue.HtmlUrl}>){(includeSummary ? $"\n  - {r.Summary}" : null)}"));
    }

    private async Task RunDuplicateDetectionBacktestAsync(string repoName, int count, bool fromPosted, CommandContext ctx)
    {
        string label = fromPosted ? "Backtest-Posted" : "Backtest";

        try
        {
            string[] parts = repoName.Split('/');
            if (parts.Length != 2)
            {
                await ctx.ReplyAsync("Invalid repo name. Use owner/repo format.");
                return;
            }

            var issues = new List<(int IssueNumber, int ExpectedDuplicate)>();

            if (fromPosted)
            {
                var messages = await _discord.GetTextChannel(Channels.DuplicatesPosted).GetMessagesAsync(count * 3).FlattenAsync();

                foreach (IMessage message in messages)
                {
                    string content = message.Content?.Trim('<', '>', ' ') ?? "";

                    if (GitHubHelper.TryParseIssueOrPRNumber(content, out string msgRepo, out int issueNumber) &&
                        repoName.Equals(msgRepo, StringComparison.OrdinalIgnoreCase) &&
                        !issues.Any(e => e.IssueNumber == issueNumber) &&
                        content.Contains("#issuecomment-", StringComparison.Ordinal) &&
                        long.TryParse(content.AsSpan(content.IndexOf("#issuecomment-", StringComparison.Ordinal) + "#issuecomment-".Length), out long commentId))
                    {
                        try
                        {
                            IssueComment ghComment = await _github.Issue.Comment.Get(parts[0], parts[1], commentId);

                            if (ghComment?.Body is { } body)
                            {
                                foreach (string line in GitHubHelper.ExtractGitHubLinks(body))
                                {
                                    if (GitHubHelper.TryParseIssueOrPRNumber(line, out _, out int dupeNumber))
                                    {
                                        issues.Add((issueNumber, dupeNumber));
                                        break;
                                    }
                                }

                                if (issues.Count >= count)
                                {
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            else
            {
                var duplicateIssues = await _graphQL.GetIssuesMarkedAsDuplicateAsync(parts[0], parts[1], count, ctx.CancellationToken);

                foreach (var (closedIssue, duplicatedAgainst) in duplicateIssues)
                {
                    GitHubHelper.TryParseIssueOrPRNumber(closedIssue, out _, out int issueNumber);
                    GitHubHelper.TryParseIssueOrPRNumber(duplicatedAgainst, out _, out int duplicateNumber);
                    issues.Add((issueNumber, duplicateNumber));
                }
            }

            await ctx.ReplyAsync($"Found {issues.Count} issues. Processing...");

            var results = new List<string>();
            int successCount = 0;
            int total = 0;

            foreach ((int issueNumber, int expectedDuplicate) in issues)
            {
                try
                {
                    IssueInfo issue = await _triageHelper.GetIssueAsync(repoName, issueNumber, ctx.CancellationToken);

                    if (issue is null)
                    {
                        string skipExtra = $" (duplicate: #{expectedDuplicate})";
                        results.Add($"⏭️ Skip | #{issueNumber} - Not found in database{skipExtra}");
                        continue;
                    }

                    total++;

                    MultiPassResult result = await RunMultiPassDuplicateDetectionAsync(issue, ctx.CancellationToken);

                    string status;
                    bool matchesActualDuplicate = result.IssuesToReport.Any(d => d.Issue.Number == expectedDuplicate);

                    if (matchesActualDuplicate && result.WouldAutoPost)
                    {
                        status = "✅ Correct";
                        successCount++;
                    }
                    else if (matchesActualDuplicate)
                    {
                        status = "🟡 Found but wouldn't post";
                    }
                    else if (result.WouldAutoPost)
                    {
                        status = "⚠️ Would post (different issue)";
                    }
                    else
                    {
                        status = "❌ Not detected";
                    }

                    string topResults = result.IssuesToReport.Length > 0
                        ? string.Join(", ", result.IssuesToReport.Select(d => $"#{d.Issue.Number} ({d.Certainty:F2})"))
                        : "none";

                    string passInfo = $"2nd={result.SecondaryTestDuplicates.Length > 0}, 3rd={result.ThirdTestDuplicates.Length > 0}";

                    string dupeInfo = $"Duplicate: #{expectedDuplicate} | ";

                    results.Add($"{status} | [#{issue.Number}](<{issue.HtmlUrl}>) - {issue.Title}\n" +
                        $"  {dupeInfo}Detected: {topResults} | Passes: {passInfo}");

                    _logger.DebugLog($"[{label}] {status}: #{issue.Number} {dupeInfo}detected={topResults} {passInfo}");
                }
                catch (Exception ex)
                {
                    results.Add($"💥 Error | #{issueNumber}: {ex.Message}");
                }
            }

            string successLabel = "Correctly detected";

            string summary =
                $"""
                **Duplicate Detection {label} Results** ({repoName})
                Tested: {total} | {successLabel}: {successCount} | Rate: {(total > 0 ? (double)successCount / total * 100 : 0):F0}%

                {string.Join("\n\n", results)}
                """;

            await ctx.Channel.SendTextFileAsync($"{label}-{Snowflake.NextString()}.txt", summary, $"{label} complete: {successCount}/{total} {successLabel.ToLowerInvariant()}.", components: null);
        }
        catch (Exception ex)
        {
            await ctx.ReplyAsync($"{label} failed: {ex.Message}");
            await _logger.DebugAsync($"{nameof(DuplicatesCommand)}: {label} error", ex);
        }
    }

    private async Task<bool> AreAllRelatedIssuesLikelyDuplicatesAsync(IssueInfo issue, IssueInfo[] candidates, CancellationToken cancellationToken)
    {
        try
        {
            IChatClient chatClient = _openAI.GetChat(OpenAIService.DefaultModel, secondary: true);

            string issueJson = (await IssueInfoForPrompt.CreateAsync(issue, _db, cancellationToken, contextLimitForIssueBody: 4000, contextLimitForCommentBody: 2000)).AsJson();

            string candidatesJson = string.Join("\n\n", await Task.WhenAll(candidates.Select(async c =>
                (await IssueInfoForPrompt.CreateAsync(c, _db, cancellationToken, contextLimitForIssueBody: 4000, contextLimitForCommentBody: 2000)).AsJson())));

            string prompt =
                $"""
                You are an assistant helping classify the relationship between GitHub issues.
                You will be given a NEW issue and one or more CANDIDATE issues that have been identified as potential duplicates.

                Determine whether the candidates are true duplicates (describing the same underlying problem) or not.
                Respond with true if you are confident they describe the same problem, false otherwise.

                NEW ISSUE:
                ```json
                {issueJson}
                ```

                CANDIDATE ISSUE(S):
                ```json
                {candidatesJson}
                ```
                """;

            ChatResponse<bool> response = await chatClient.GetResponseAsync<bool>(prompt, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken);

            return response.Result;
        }
        catch (Exception ex)
        {
            _logger.DebugLog($"{nameof(DuplicatesCommand)}: Error classifying duplicate vs related for issue <{issue.HtmlUrl}>: {ex.Message}");
        }

        return false;
    }

    private async Task PostGhCommentSummary(IssueInfo issue, string comment)
    {
        try
        {
            IssueComment newComment = await _github.Issue.Comment.Create(issue.Repository.Id, issue.Number, comment);

            _logger.DebugLog($"{nameof(DuplicatesCommand)}: Posted comment <{newComment.HtmlUrl}>");

            await _discord.GetTextChannel(Channels.DuplicatesPosted).TrySendMessageAsync($"<{newComment.HtmlUrl}>");
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"{nameof(DuplicatesCommand)}: Error posting duplicate summary comment for issue <{issue.HtmlUrl}>", ex);
        }
    }
}
