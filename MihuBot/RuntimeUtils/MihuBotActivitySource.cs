using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.Search;

namespace MihuBot.RuntimeUtils;

public static class MihuBotActivitySource
{
    public static readonly ActivitySource Instance = new("MihuBot.Ai", "1.0.0");

    public static Activity SetIssueContext(this Activity activity, IssueInfo issue)
    {
        activity?.SetTag("issue.number", issue.Number);
        activity?.SetTag("issue.repository", issue.Repository.FullName);
        activity?.SetTag("issue.title", issue.Title);
        return activity!;
    }

    public static Activity SetMessagesBuilt(this Activity activity, IList<ChatMessage> messages)
    {
        activity?.AddEvent(new ActivityEvent("MessagesBuilt", tags: new ActivityTagsCollection
        {
            ["messages.count"] = messages.Count
        }));

        return activity!;
    }

    public static Activity SetAiResponse(this Activity activity, ChatResponse response, bool logFullModelResponse)
    {
        var responseText = response.Text;

        var tags = new ActivityTagsCollection
        {
            ["response.count"] = responseText?.Length ?? 0
        };

        if (logFullModelResponse && !string.IsNullOrEmpty(responseText))
        {
            tags.Add("response.full", responseText);
        }

        activity?.AddEvent(new ActivityEvent("AiResponded", tags: tags));

        return activity!;
    }

    public static Activity SetSuccess(this Activity activity)
    {
        activity?.SetStatus(ActivityStatusCode.Ok);
        return activity!;
    }

    public static Activity SetError(this Activity activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        return activity!;
    }

    public static Activity SetError(this Activity activity, string errorMessage)
    {
        activity?.SetStatus(ActivityStatusCode.Error, errorMessage);
        return activity!;
    }

    public static Activity SetIssueSearchContext(this Activity activity, IssueSearchBulkFilters bulkFilters)
    {
        activity?.SetTag("filters.bulk.postProcess.enabled", bulkFilters.PostProcessIssues);
        activity?.SetTag("filters.bulk.postProcess.context", bulkFilters.PostProcessingContext);
        activity?.SetTag("filters.bulk.exclude.issues", bulkFilters.ExcludeIssues?.Select(i => i.Id) ?? []);
        activity?.SetTag("filters.bulk.maxResultsPerTerm", bulkFilters.MaxResultsPerTerm);
        return activity!;
    }

    public static Activity SetIssueSearchContext(this Activity activity, IssueSearchFilters filters)
    {
        activity?.SetTag("filters.search.include.open", filters.IncludeOpen);
        activity?.SetTag("filters.search.include.closed", filters.IncludeClosed);
        activity?.SetTag("filters.search.include.issues", filters.IncludeIssues);
        activity?.SetTag("filters.search.include.pullRequests", filters.IncludePullRequests);
        activity?.SetTag("filters.search.include.commentsInResponse", filters.IncludeCommentsInResponse);
        activity?.SetTag("filters.search.minScore", filters.MinScore);
        activity?.SetTag("filters.search.createdAfter", filters.CreatedAfter);
        activity?.SetTag("filters.search.repository", filters.Repository);
        activity?.SetTag("filters.search.labels", string.Join(';', filters.Labels ?? []));
        return activity!;
    }

    public static Activity SetIssueSearchContext(this Activity activity, IssueSearchResponseOptions options)
    {
        activity?.SetTag("options.maxResults", options.MaxResults);
        activity?.SetTag("options.include.issueComments", options.IncludeIssueComments);
        return activity!;
    }

    public static Activity SetIssueSearchTimings(this Activity activity, SearchTimings timings)
    {
        activity?.SetTag("timings.reclassification", timings.Reclassification.TotalMilliseconds);
        activity?.SetTag("timings.embeddingGeneration", timings.EmbeddingGeneration.TotalMilliseconds);
        activity?.SetTag("timings.vectorSearch", timings.VectorSearch.TotalMilliseconds);
        activity?.SetTag("timings.fullTextSearch", timings.FullTextSearch.TotalMilliseconds);
        activity?.SetTag("timings.database", timings.Database.TotalMilliseconds);
        return activity!;
    }

    public static Activity SetOperation(this Activity activity, string operation, string subOperation)
    {
        activity?.SetTag("operation.type", operation);
        activity?.SetTag("operation.subType", subOperation);
        return activity!;
    }
}

