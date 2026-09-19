using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;

namespace MihuBot.RuntimeUtils.AI;

internal sealed partial class AreaLabelToolDataFilter
{
    private readonly string _repository;
    private readonly int _number;
    private readonly string _id;
    private readonly string _title;
    private readonly Regex _qualifiedReference;
    private readonly Regex _shortReference;
    private readonly Regex _numberReference;
    private readonly Regex _pullRef;

    private static readonly string[] s_numberKeys = ["number", "issue_number", "pullNumber", "pull_number"];
    private static readonly string[] s_urlKeys = ["html_url", "url", "issue_url", "repository_url"];

    private static readonly FrozenSet<string> s_searchMetadata = new[]
    {
        "total_count", "totalCount", "count", "issueCount", "pullRequestCount",
        "incomplete_results", "pageInfo", "page_info", "nextCursor", "cursor",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public AreaLabelToolDataFilter(IssueInfo target)
    {
        _repository = target.Repository.FullName;
        _number = target.Number;
        _id = target.Id;
        _title = target.Title;
        _qualifiedReference = CreateRegex($@"(?<![\w.-]){Regex.Escape(_repository)}(?:\s*#|/(?:issues|pulls?|discussions)/){_number}(?!\d)");
        _shortReference = CreateRegex($@"(?:#|GH-)\s*{_number}(?!\d)");
        _numberReference = CreateRegex($@"(?<!\d){_number}(?!\d)");
        _pullRef = CreateRegex($@"^refs/pull/{_number}(?:/|$)");
    }

    public bool IsBlockedRequest(string toolName, AIFunctionArguments arguments)
    {
        var args = JsonSerializer.SerializeToNode(arguments, AIJsonUtilities.DefaultOptions).AsObject();
        bool targetRepository = RequestUsesTargetRepository(args);

        if (targetRepository && (toolName is "issue_read" or "pull_request_read") &&
            HasTargetNumber(args))
        {
            return true;
        }

        foreach (var value in args.Select(p => p.Value).OfType<JsonValue>())
        {
            if (value.TryGetValue<string>(out string text) &&
                (ContainsTargetReference(text, targetRepository) ||
                 (targetRepository && toolName == "get_file_contents" && _pullRef.IsMatch(text))))
            {
                return true;
            }
        }

        string query = GetString(args, "query");

        return targetRepository && (toolName is "search_issues" or "search_pull_requests") &&
            query is not null && _numberReference.IsMatch(query);
    }

    public string FilterResponse(string toolName, AIFunctionArguments arguments, object result)
    {
        var args = JsonSerializer.SerializeToNode(arguments, AIJsonUtilities.DefaultOptions).AsObject();
        var data = JsonSerializer.SerializeToNode(result, AIJsonUtilities.DefaultOptions);
        bool removeSearchMetadata = toolName.StartsWith("search_", StringComparison.Ordinal) ||
            toolName.StartsWith("list_", StringComparison.Ordinal);

        return Filter(data, RequestUsesTargetRepository(args), removeSearchMetadata, 0)?.ToJsonString() ?? "[]";
    }

    private JsonNode Filter(JsonNode node, bool targetRepository, bool removeSearchMetadata, int depth)
    {
        if (depth > 64)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            string repository = GetRepository(obj);
            targetRepository = repository is null ? targetRepository : IsTargetRepository(repository);

            if ((targetRepository && HasTargetNumber(obj)) ||
                (!string.IsNullOrEmpty(_id) && (GetString(obj, "id") == _id || GetString(obj, "node_id") == _id)) ||
                (targetRepository && !s_numberKeys.Any(obj.ContainsKey) && !s_urlKeys.Any(obj.ContainsKey) &&
                    !string.IsNullOrEmpty(_title) && GetString(obj, "title")?.Equals(_title, StringComparison.OrdinalIgnoreCase) is true) ||
                obj.Any(p => p.Key.EndsWith("url", StringComparison.OrdinalIgnoreCase) &&
                    p.Value is JsonValue value && value.TryGetValue<string>(out string url) && _qualifiedReference.IsMatch(url)))
            {
                return null;
            }

            foreach (var property in obj.ToArray())
            {
                if (removeSearchMetadata && s_searchMetadata.Contains(property.Key))
                {
                    obj.Remove(property.Key);

                    continue;
                }

                var filtered = Filter(property.Value, targetRepository, removeSearchMetadata, depth + 1);

                if (filtered is null)
                {
                    obj.Remove(property.Key);
                }
                else if (!ReferenceEquals(filtered, property.Value))
                {
                    obj[property.Key] = filtered;
                }
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (int i = array.Count - 1; i >= 0; i--)
            {
                var filtered = Filter(array[i], targetRepository, removeSearchMetadata, depth + 1);

                if (filtered is null)
                {
                    array.RemoveAt(i);
                }
                else if (!ReferenceEquals(filtered, array[i]))
                {
                    array[i] = filtered;
                }
            }

            return array;
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out string text))
        {
            ReadOnlySpan<char> trimmed = text.AsSpan().Trim();

            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                JsonNode embedded;

                try
                {
                    embedded = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    // Non-JSON tool text still goes through reference filtering below.
                    embedded = null;
                }

                if (embedded is not null)
                {
                    return JsonValue.Create(Filter(embedded, targetRepository, removeSearchMetadata, depth + 1)?.ToJsonString() ?? "[]");
                }
            }

            if (ContainsTargetReference(text, targetRepository))
            {
                return null;
            }
        }

        return node;
    }

    private bool RequestUsesTargetRepository(JsonObject args)
    {
        if (GetString(args, "owner") is { } owner && GetString(args, "repo") is { } repo)
        {
            return IsTargetRepository($"{owner}/{repo}");
        }

        if (GetString(args, "query") is { } query)
        {
            var repositories = RepositoryQualifier().Matches(query);

            if (repositories.Count > 0)
            {
                return repositories.Any(m => IsTargetRepository(m.Groups[1].Value));
            }
        }

        return true;
    }

    private bool ContainsTargetReference(string text, bool targetRepository) =>
        _qualifiedReference.IsMatch(text) ||
        (!string.IsNullOrEmpty(_id) && text.Contains(_id, StringComparison.Ordinal)) ||
        (targetRepository && (_shortReference.IsMatch(text) ||
            (_title is { Length: >= 12 } && text.Contains(_title, StringComparison.OrdinalIgnoreCase))));

    private bool HasTargetNumber(JsonObject obj) =>
        s_numberKeys
            .Any(key => obj[key] is JsonValue value &&
                ((value.TryGetValue<int>(out int number) && number == _number) ||
                 (value.TryGetValue<string>(out string text) && text == _number.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private bool IsTargetRepository(string repository) => repository.Equals(_repository, StringComparison.OrdinalIgnoreCase);

    private static string GetRepository(JsonObject obj)
    {
        if (obj["repository"] is JsonObject repository)
        {
            string name = GetString(repository, "full_name") ?? GetString(repository, "nameWithOwner");

            if (name is not null)
            {
                return name;
            }
        }

        foreach (string key in s_urlKeys)
        {
            if (GetString(obj, key) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                {
                    return $"{parts[0]}/{parts[1]}";
                }

                if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && parts is ["repos", var owner, var repo, ..])
                {
                    return $"{owner}/{repo}";
                }
            }
        }

        return null;
    }

    private static string GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out string text) ? text : null;

    private static Regex CreateRegex(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"(?:^|\s)repo:([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryQualifier();
}
