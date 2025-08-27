using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class DuplicatesCommand : CommandBase
{
    public override string Command => "duplicates";
    public override string[] Aliases => ["forcetriage"];

    private readonly FileBackedHashSet _processedIssuesForDuplicateDetection = new("ProcessedIssuessForDuplicateDetection.txt");
    private readonly ConcurrentDictionary<string, (IssueInfo Issue, string DuplicatesSummary)> _duplicatesToPost = [];

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly GitHubClient _github;
    private readonly IssueTriageService _triageService;
    private readonly IssueTriageHelper _triageHelper;
    private readonly GitHubSearchService _search;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;
    private readonly DiscordSocketClient _discord;
    private readonly SemaphoreSlim _sempahore = new(2, 2);

    private bool SkipManualVerificationBeforePosting => _configuration.GetOrDefault(null, $"{Command}.AutoPost", false);
    private bool DoThirdVerificationCheck => _configuration.GetOrDefault(null, $"{Command}.ThirdTest", true);
    private double CertaintyThreshold => _configuration.GetOrDefault(null, $"{Command}.{nameof(CertaintyThreshold)}", 0.94d);

    public DuplicatesCommand(IDbContextFactory<GitHubDbContext> db, IssueTriageService triageService, IssueTriageHelper triageHelper, ServiceConfiguration serviceConfiguration, Logger logger, IConfigurationService configuration, GitHubClient github, DiscordSocketClient discord, GitHubSearchService search)
    {
        _db = db;
        _triageService = triageService;
        _triageHelper = triageHelper;
        _serviceConfiguration = serviceConfiguration;
        _logger = logger;
        _configuration = configuration;
        _github = github;
        _discord = discord;
        _search = search;
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

        if (ctx.Arguments.Length != 1 || !GitHubHelper.TryParseIssueOrPRNumber(ctx.Arguments[0], out string repoName, out int issueNumber))
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
            await RunDuplicateDetectionAsync_EmbeddingsOnly(issue, automated: false, message: ctx);
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
                            .OrderByDescending(i => i.CreatedAt);

                        query = IssueTriageHelper.AddIssueInfoIncludes(query);

                        IssueInfo[] issues = await query
                            .Take(100)
                            .AsSplitQuery()
                            .ToArrayAsync();

                        foreach (IssueInfo issue in issues)
                        {
                            if (issue.PullRequest is not null ||
                                !issue.User.IsLikelyARealUser() ||
                                _configuration.GetOrDefault(null, $"{Command}.Pause.{issue.RepoName()}", false))
                            {
                                continue;
                            }

                            if (DateTime.UtcNow.Subtract(issue.CreatedAt).TotalMinutes < 3)
                            {
                                // Give it 3 minutes before processing in case the author references the duplicate in a comment.
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
                                await RunDuplicateDetectionAsync_EmbeddingsOnly(issue, automated: true, message: null);
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
            (IssueInfo Issue, double Certainty, string Summary)[] duplicates = await DetectIssueDuplicatesAsync(issue, CancellationToken.None);

            if (duplicates.Length == 0)
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
            double certaintyThreshold = CertaintyThreshold;

            string reply = FormatDuplicatesSummary(issue, duplicates, includeSummary: false);
            string summary = FormatDuplicatesSummary(issue, duplicates);

            if (duplicates.Any(d => IsLikelyUsefulToReport(issue, d.Issue, d.Certainty, certaintyThreshold)))
            {
                reply = $"{MentionUtils.MentionUser(KnownUsers.Miha)} {reply}";

                var secondaryTest = await DetectIssueDuplicatesAsync(issue, CancellationToken.None);

                bool secondaryTestIsUseful = secondaryTest.Any(d =>
                    IsLikelyUsefulToReport(issue, d.Issue, d.Certainty, certaintyThreshold) &&
                    duplicates.FirstOrDefault(i => i.Issue.Id == d.Issue.Id) is { Issue: not null } other &&
                    IsLikelyUsefulToReport(issue, other.Issue, other.Certainty, certaintyThreshold));

                var issuesToReport = duplicates;

                if (secondaryTestIsUseful)
                {
                    issuesToReport = [.. duplicates.Where(d => secondaryTest.Any(s => s.Issue.Id == d.Issue.Id))];
                }

                issuesToReport = [.. issuesToReport.Where(d => d.Certainty >= 0.89 && !AreIssuesAlreadyLinked(issue, d.Issue)).Take(5)];
                bool plural = issuesToReport.Length > 1;

                if (issuesToReport.Length == 0)
                {
                    throw new UnreachableException("No issues?");
                }

                string ghComment =
                    $"""
                    I'm a bot. Here {(plural ? "are" : "is a")} possible related and/or duplicate issue{(plural ? "s" : "")} (I may be wrong):
                    {string.Join('\n', issuesToReport.Select(d => $"- {d.Issue.HtmlUrl}"))}
                    """;

                bool thirdTestIsUseful = true;
                if (DoThirdVerificationCheck)
                {
                    var thirdTest = await DetectIssueDuplicatesAsync(issue, CancellationToken.None);

                    thirdTestIsUseful = thirdTest.Any(d =>
                        IsLikelyUsefulToReport(issue, d.Issue, d.Certainty, certaintyThreshold) &&
                        duplicates.FirstOrDefault(i => i.Issue.Id == d.Issue.Id) is { Issue: not null } other &&
                        IsLikelyUsefulToReport(issue, other.Issue, other.Certainty, certaintyThreshold));
                }

                if (SkipManualVerificationBeforePosting && secondaryTestIsUseful && thirdTestIsUseful && automated && await ShouldAutoPostAsync(issue, [.. issuesToReport.Select(i => i.Issue)]))
                {
                    await PostGhCommentSummary(issue, ghComment);
                }
                else
                {
                    if (!secondaryTestIsUseful)
                    {
                        reply = $"**Note:** Secondary test did not find overlapping useful duplicates.\n\n{reply}";

                        summary = $"{summary}\n\nSecondary:\n{FormatDuplicatesSummary(issue, secondaryTest)}";
                    }

                    if (!thirdTestIsUseful)
                    {
                        reply = $"**Note:** Third test did not find overlapping useful duplicates.\n\n{reply}";
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

    private async Task RunDuplicateDetectionAsync_EmbeddingsOnly(IssueInfo issue, bool automated, MessageContext message)
    {
        try
        {
            string titleInfo = $"{issue.Repository.FullName}#{issue.Number}: {issue.Title}";
            string author = $"{(issue.PullRequest is null ? "Issue" : "Pull request")} author: {issue.User.Login}";
            string description = $"{titleInfo}\n{author}\n\n{issue.Body?.Trim()}";

            description = SemanticMarkdownChunker.TrimTextToTokens(_search.Tokenizer, description, SemanticMarkdownChunker.MaxSectionTokens);

            var searchResults = await _search.SearchIssuesAndCommentsAsync(
                description,
                maxResults: 10,
                new GitHubSearchService.IssueSearchFilters(true, true, true, true) { Repository = issue.Repository.FullName },
                includeAllIssueComments: false,
                cancellationToken: CancellationToken.None);

            var duplicates = searchResults.Results
                .Where(r => r.Score >= 0.5 && r.Results[0].Issue.Id != issue.Id)
                .Select(r => (r.Results[0].Issue, r.Score, string.Empty))
                .ToArray();

            if (duplicates.Length == 0)
            {
                _logger.DebugLog($"{nameof(DuplicatesCommand)}: No duplicates found for issue <{issue.HtmlUrl}> (embeddings only)");

                if (!automated)
                {
                    await message.ReplyAsync("No duplicates found (embeddings only).");
                }

                return;
            }

            SocketTextChannel channel = _logger.Options.Discord.GetTextChannel(Channels.DuplicatesEmbeddings);

            string reply = FormatDuplicatesSummary(issue, duplicates, includeSummary: false);
            double certaintyThreshold = CertaintyThreshold;

            if (duplicates.Any(d => d.Score >= 0.7 && IsLikelyUsefulToReport(issue, d.Issue, certainty: 1, certaintyThreshold)))
            {
                reply = $"{MentionUtils.MentionUser(KnownUsers.Miha)} {reply}";
            }

            reply = reply.TruncateWithDotDotDot(1800);

            reply = $"**Embeddings only**\n{reply}";

            await (message?.Channel ?? channel).SendMessageAsync(reply, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"{nameof(DuplicatesCommand)}: Error during duplicate detection for issue <{issue.HtmlUrl}> (embeddings only)", ex);
        }
    }

    private static bool IsLikelyUsefulToReport(IssueInfo issue, IssueInfo duplicate, double certainty, double certaintyThreshold)
    {
        if (certainty < certaintyThreshold)
        {
            return false;
        }

        if (duplicate.UserId == issue.UserId && duplicate.UserId != GitHubDataService.GhostUserId)
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

            if (ghIssue.Assignee?.Id == GitHubDataService.CopilotUserId)
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
                    subIssues = await _github.GetAllSubIssuesAsync(dupe.RepositoryId, dupe.Number);

                    if (subIssues.Any(i => i.Id == issue.GitHubIdentifier))
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

    private async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectIssueDuplicatesAsync(IssueInfo issue, CancellationToken cancellationToken)
    {
        ModelInfo model = _triageHelper.DefaultModel;

        if (_configuration.TryGet(null, "Duplicates.Model", out string modelName))
        {
            model = OpenAIService.AllModels.FirstOrDefault(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)) ?? model;
        }

        var options = new IssueTriageHelper.TriageOptions(model, "MihaZupan", issue, OnToolLog: log => _logger.DebugLog($"[Duplicates {issue.Repository.FullName}#{issue.Number}]: {log}"), SkipCommentsOnCurrentIssue: true);

        int attemptCount = 0;
        while (true)
        {
            try
            {
                return await _triageHelper.DetectDuplicateIssuesAsync(options, cancellationToken);
            }
            catch (JsonException) when (++attemptCount < 3) { }
        }
    }

    private static string FormatDuplicatesSummary(IssueInfo issue, (IssueInfo Issue, double Certainty, string Summary)[] duplicates, bool includeSummary = true)
    {
        return $"Duplicate issues for [{issue.Repository.FullName}#{issue.Number}](<{issue.HtmlUrl}>) - {issue.Title}:\n" +
            string.Join('\n', duplicates.Select(r => $"- ({r.Certainty:F2}) [#{r.Issue.Number} - {r.Issue.Title}](<{r.Issue.HtmlUrl}>){(includeSummary ? $"\n  - {r.Summary}" : null)}"));
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
