using System.Text.Json;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.AI;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelToolDataFilterTests
{
    private static AreaLabelToolDataFilter CreateFilter() => new(new IssueInfo
    {
        Number = 123, Id = "I_target", Title = "Target socket regression",
        Repository = new RepositoryInfo { FullName = "dotnet/runtime" },
    });

    private static AIFunctionArguments Arguments(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);

    [Theory]
    [InlineData("issue_read", """{"owner":"dotnet","repo":"runtime","issue_number":123,"method":"get"}""", true)]
    [InlineData("issue_read", """{"owner":"DOTNET","repo":"RUNTIME","issue_number":"123","method":"get_comments"}""", true)]
    [InlineData("pull_request_read", """{"owner":"dotnet","repo":"runtime","pullNumber":123,"method":"get"}""", true)]
    [InlineData("issue_read", """{"owner":"dotnet","repo":"runtime","issue_number":1234}""", false)]
    [InlineData("issue_read", """{"owner":"dotnet","repo":"aspnetcore","issue_number":123}""", false)]
    [InlineData("get_file_contents", """{"owner":"dotnet","repo":"runtime","ref":"refs/pull/123/head","path":"README.md"}""", true)]
    [InlineData("get_file_contents", """{"owner":"dotnet","repo":"runtime","ref":"refs/pull/1234/head","path":"README.md"}""", false)]
    [InlineData("search_issues", """{"query":"repo:dotnet/runtime 123 label:area-JIT"}""", true)]
    [InlineData("search_issues", """{"query":"repo:dotnet/aspnetcore 123 label:area-JIT"}""", false)]
    [InlineData("search_issues", """{"query":"repo:dotnet/runtime Socket label:area-Networking"}""", false)]
    [InlineData("search_code", """{"query":"repo:dotnet/runtime Socket"}""", false)]
    [InlineData("search_issues", """{"query":"https://github.com/dotnet/runtime/issues/123#issuecomment-456"}""", true)]
    [InlineData("search_issues", """{"query":"dotnet/runtime#123 label:area-JIT"}""", true)]
    [InlineData("search_issues", """{"query":"repo:dotnet/runtime \"Target socket regression\""}""", true)]
    [InlineData("search_repositories", """{"query":"org:dotnet"}""", false)]
    public void RequestFilteringBlocksTargetLookupsWithoutDisablingOtherInvestigations(string tool, string arguments, bool expected)
    {
        Assert.Equal(expected, CreateFilter().IsBlockedRequest(tool, Arguments(arguments)));
    }

    [Theory]
    [InlineData("""{"number":123,"labels":["answer"]}""")]
    [InlineData("""{"number":"123","labels":["answer"]}""")]
    [InlineData("""{"id":"I_target","labels":["answer"]}""")]
    [InlineData("""{"node_id":"I_target","labels":["answer"]}""")]
    [InlineData("""{"html_url":"https://github.com/dotnet/runtime/issues/123","labels":["answer"]}""")]
    [InlineData("""{"url":"https://api.github.com/repos/dotnet/runtime/issues/123","labels":["answer"]}""")]
    [InlineData("""{"url":"https://github.com/DOTNET/RUNTIME/pull/123/files","labels":["answer"]}""")]
    [InlineData("""{"title":"Target socket regression","labels":["answer"]}""")]
    public void TargetRecordsAreRemovedEvenWithoutTheirLabelsField(string item)
    {
        var data = JsonSerializer.Deserialize<JsonElement>($$"""{"items":[{{item}}]}""");
        string result = CreateFilter().FilterResponse("search_issues", Arguments("""{"query":"repo:dotnet/runtime Socket"}"""), data);

        Assert.Equal("""{"items":[]}""", result);
    }

    [Fact]
    public void FilteringSearchCountsDoesNotRevealWhetherTheTargetMatchedALabelQuery()
    {
        var filter = CreateFilter();
        var args = Arguments("""{"query":"repo:dotnet/runtime Socket label:area-Networking"}""");
        var matching = JsonSerializer.Deserialize<JsonElement>("""
            {"total_count":1,"incomplete_results":false,"pageInfo":{"endCursor":"target-cursor","hasNextPage":true},
             "items":[{"number":123,"labels":["area-Networking"]}]}
            """);
        var absent = JsonSerializer.Deserialize<JsonElement>("""
            {"total_count":0,"incomplete_results":false,"pageInfo":{"endCursor":null,"hasNextPage":false},"items":[]}
            """);

        Assert.Equal(filter.FilterResponse("search_issues", args, absent), filter.FilterResponse("search_issues", args, matching));
    }

    [Fact]
    public void OtherIssuesIncludingTheSameNumberInAnotherRepositoryRetainTheirLabels()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("""
            {"items":[
                {"number":456,"html_url":"https://github.com/dotnet/runtime/issues/456","labels":["area-Other"]},
                {"number":123,"repository":{"full_name":"dotnet/aspnetcore"},"labels":["area-Mvc"]},
                {"number":1234,"url":"https://api.github.com/repos/dotnet/runtime/issues/1234","labels":["area-JIT"]}
            ]}
            """);
        string result = CreateFilter().FilterResponse("search_issues", new(), data);

        Assert.Contains("area-Other", result, StringComparison.Ordinal);
        Assert.Contains("area-Mvc", result, StringComparison.Ordinal);
        Assert.Contains("area-JIT", result, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(3, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void EmbeddedMcpJsonAndNestedGraphQlResultsAreFiltered()
    {
        var content = new TextContent("""
            {"data":{"repository":{"nameWithOwner":"dotnet/runtime","issues":{
              "totalCount":2,"nodes":[{"number":123,"labels":["hidden"]},{"number":456,"labels":["visible"]}]}}}}
            """);
        string result = CreateFilter().FilterResponse("list_issues", Arguments("""{"owner":"dotnet","repo":"runtime"}"""), content);

        Assert.DoesNotContain("hidden", result, StringComparison.Ordinal);
        Assert.DoesNotContain("totalCount", result, StringComparison.Ordinal);
        Assert.Contains("visible", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Related to dotnet/runtime#123, assigned area-Secret.")]
    [InlineData("The fix for #123 was assigned area-Secret.")]
    [InlineData("See https://github.com/dotnet/runtime/issues/123 for area-Secret.")]
    [InlineData("The Target socket regression was labeled area-Secret.")]
    public void FreeTextThatMentionsTheTargetIsWithheld(string text)
    {
        string result = CreateFilter().FilterResponse("get_commit",
            Arguments("""{"owner":"dotnet","repo":"runtime"}"""), new TextContent(text));

        Assert.DoesNotContain("Secret", result, StringComparison.Ordinal);
    }
}
