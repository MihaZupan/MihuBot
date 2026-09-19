using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;
using Octokit.Internal;
using static MihuBot.Tests.RuntimeUtils.AreaLabelGraphQLTransport;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelTimelineTests
{
    private const string Labeler = "github-actions[bot]";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimelineMetadataAggregatesCallsAndCostAndReturnsLatestBudget(bool partialError)
    {
        using var transport = new AreaLabelGraphQLTransport();

        transport.Respond = (_, _) =>
        {
            bool first = transport.Requests.Count == 1;

            JsonObject data = first
                ? GraphData(
                    ("issue0", TimelineNode("A", [GraphEvent("first", "area-Foo")], true, "next")),
                    ("issue1", TimelineNode("B", [GraphEvent("first", "area-Foo")])))
                : GraphData(("issue0", TimelineNode("A", [GraphEvent("second", "area-Bar")])));

            data["rateLimit"] = JsonSerializer.SerializeToNode(new
            {
                cost = first ? 2 : 3,
                remaining = first ? 98 : 95,
                resetAt = first ? "2026-09-18T21:00:00Z" : "2026-09-18T22:00:00Z",
            });

            return Task.FromResult(GraphResponse(data, !first && partialError
                ? new JsonArray { GraphError("Timeline unavailable", "issue0", "timelineItems") }
                : null));
        };

        var result = await transport.Client.GetIssueLabelTimelinesAsync(["A", "B"], _ => { });

        Assert.Equal(2, result.Calls);
        Assert.Equal(5, result.Cost);
        Assert.NotNull(result.LastRateLimit);
        Assert.Equal(3, result.LastRateLimit.Cost);
        Assert.Equal(95, result.LastRateLimit.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero), result.LastRateLimit.ResetAt);
        Assert.Equal(partialError, result.Timelines[0].Error is not null);
        Assert.Null(result.Timelines[1].Error);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("metadata-error")]
    [InlineData("http-error")]
    public async Task MissingFinalRequestMetadataDoesNotReturnPartialCostOrStaleBudget(string failure)
    {
        using var transport = new AreaLabelGraphQLTransport();

        transport.Respond = (_, _) =>
        {
            if (transport.Requests.Count == 1)
            {
                return Task.FromResult(GraphResponse(GraphData(
                    ("issue0", TimelineNode("A", [GraphEvent("first", "area-Foo")], true, "next")))));
            }

            if (failure == "http-error")
            {
                return Task.FromResult(GraphResponse(null, status: HttpStatusCode.InternalServerError));
            }

            JsonObject data = GraphData(("issue0", TimelineNode("A", [GraphEvent("second", "area-Bar")])));

            if (failure == "malformed")
            {
                data["rateLimit"]!["cost"] = "not a number";
            }
            else
            {
                data.Remove("rateLimit");
            }

            return Task.FromResult(GraphResponse(data, failure == "metadata-error"
                ? new JsonArray { GraphError("Metadata unavailable", "rateLimit") }
                : null));
        };

        var result = await transport.Client.GetIssueLabelTimelinesAsync(["A"], _ => { });

        Assert.Equal(2, result.Calls);
        Assert.Null(result.Cost);
        Assert.Null(result.LastRateLimit);
        Assert.Equal(failure == "http-error", Assert.Single(result.Timelines).Error is not null);
    }

    [Fact]
    public async Task LaterKnownMetadataDoesNotHideEarlierUnknownCost()
    {
        using var transport = new AreaLabelGraphQLTransport();

        transport.Respond = (_, _) =>
        {
            bool first = transport.Requests.Count == 1;

            JsonObject data = GraphData(("issue0", TimelineNode("A",
                [GraphEvent(first ? "first" : "second", first ? "area-Foo" : "area-Bar")], first, first ? "next" : null)));

            if (first)
            {
                data.Remove("rateLimit");
            }

            return Task.FromResult(GraphResponse(data));
        };

        var result = await transport.Client.GetIssueLabelTimelinesAsync(["A"], _ => { });

        Assert.Equal(2, result.Calls);
        Assert.Null(result.Cost);
        Assert.NotNull(result.LastRateLimit);
        Assert.Equal(7, result.LastRateLimit.Cost);
        Assert.Equal(4993, result.LastRateLimit.Remaining);
        Assert.Null(Assert.Single(result.Timelines).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoGraphQLRequestsMeansZeroCostAndNoBudgetSnapshot(bool missingNodeId)
    {
        using var transport = new AreaLabelGraphQLTransport();

        var result = await transport.Client.GetIssueLabelTimelinesAsync(missingNodeId ? [""] : [], Assert.Fail);

        Assert.Equal(0, result.Calls);
        Assert.Equal(0, result.Cost);
        Assert.Null(result.LastRateLimit);
        Assert.Empty(transport.Requests);
        Assert.Equal(missingNodeId ? 1 : 0, result.Timelines.Length);
    }

    [Fact]
    public async Task EmptyBatchDoesNotSendQuery()
    {
        using var transport = new AreaLabelGraphQLTransport();

        var results = await ReadTimelinesAsync(transport.Client, [], Labeler, Assert.Fail, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task TimelineBatchRejectsMoreThan25IssuesBeforeSendingQuery()
    {
        using var transport = new AreaLabelGraphQLTransport();
        Issue[] issues = [.. Enumerable.Range(0, 26).Select(i => Issue($"NODE{i}"))];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ReadTimelinesAsync(transport.Client, issues, Labeler, Assert.Fail, CancellationToken.None));

        Assert.Equal(25, GitHubGraphQL.LabelTimelineBatchSize);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingIssueNodeIdFailsOnlyThatIssueWithoutSendingIt(string? nodeId)
    {
        using var transport = new AreaLabelGraphQLTransport();
        List<string> logs = [];

        var results = await ReadTimelinesAsync(transport.Client, [Issue(nodeId), Issue("VALID")], Labeler, logs.Add, CancellationToken.None);

        Assert.Equal(2, results.Length);
        Assert.Contains("no GitHub node ID", results[0].Error, StringComparison.Ordinal);
        Assert.Empty(results[0].Events);
        Assert.Null(results[1].Error);
        Assert.Single(results[1].Events);
        var request = Assert.Single(transport.Requests);
        AssertVariables(request, ("issue1", "VALID", null));
        Assert.DoesNotContain("issue0", request.GetProperty("query").GetString(), StringComparison.Ordinal);
        Assert.Single(logs);
    }

    [Fact]
    public async Task PaginationUsesIndependentCursorsAndOnlyQueriesIncompleteIssues()
    {
        using var transport = new AreaLabelGraphQLTransport();
        List<string> logs = [];

        transport.Respond = (request, _) =>
        {
            HttpResponseMessage response;

            switch (transport.Requests.Count)
            {
                case 1:
                    AssertVariables(request, ("issue0", "A", null), ("issue1", "B", null), ("issue2", "C", null));

                    response = GraphResponse(GraphData(
                        ("issue0", TimelineNode("A", [GraphEvent("same-id", "area-Foo")], true, "cursor-A")),
                        ("issue1", TimelineNode("B", [GraphEvent("same-id", "area-Foo")], true, "cursor-B")),
                        ("issue2", TimelineNode("C", [GraphEvent("same-id", "area-Foo")]))));

                    break;
                case 2:
                    AssertVariables(request, ("issue0", "A", "cursor-A"), ("issue1", "B", "cursor-B"));
                    Assert.DoesNotContain("issue2", request.GetProperty("query").GetString(), StringComparison.Ordinal);

                    response = GraphResponse(GraphData(
                        ("issue0", TimelineNode("A", [GraphEvent("A-extra", "area-Bar")])),
                        ("issue1", TimelineNode("B", [], true, "cursor-B-next"))));

                    break;
                case 3:
                    AssertVariables(request, ("issue1", "B", "cursor-B-next"));
                    Assert.DoesNotContain("issue0", request.GetProperty("query").GetString(), StringComparison.Ordinal);

                    response = GraphResponse(GraphData(
                        ("issue1", TimelineNode("B",
                            [GraphEvent("B-remove", "area-Foo", added: false, actor: "maintainer", actorType: "User"),
                                GraphEvent("B-add", "area-Baz", actor: "maintainer", actorType: "User")]))));

                    break;
                default:
                    throw new InvalidOperationException("Unexpected extra GraphQL page.");
            }

            return Task.FromResult(response);
        };

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B"), Issue("C")], Labeler, logs.Add, CancellationToken.None);

        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Equal([2, 3, 1], results.Select(result => result.Events.Count));
        Assert.Equal(["area-Foo", "area-Bar"], results[0].Events.Select(e => e.Label));
        Assert.True(AreaLabelHistory.AnalyzeEvents(results[1].Events, ["area-Baz"], Labeler).HumanChanged);
        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal(3, logs.Count);
        Assert.Contains("for 3 issues", logs[0], StringComparison.Ordinal);
        Assert.Contains("for 2 issues", logs[1], StringComparison.Ordinal);
        Assert.Contains("for 1 issues", logs[2], StringComparison.Ordinal);

        Assert.All(logs, log =>
        {
            Assert.Contains("\"cost\":7", log.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("\"remaining\":4993", log.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("resetAt", log, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FullEventPageAndLaterPagePreserveSameTimestampApiOrderNotOpaqueIdOrder()
    {
        using var transport = new AreaLabelGraphQLTransport();

        JsonObject[] firstPage =
        [
            GraphEvent("z-original", "area-Foo"),
            .. Enumerable.Range(0, 49).Select(i => GraphEvent($"opaque-{i}", $"unrelated-{i}")),
        ];

        transport.Respond = (request, _) => Task.FromResult(transport.Requests.Count == 1
            ? GraphResponse(GraphData(("issue0", TimelineNode("A", firstPage, true, "next-page"))))
            : GraphResponse(GraphData(("issue0", TimelineNode("A",
                [GraphEvent("a-removal", "area-Foo", added: false, actor: "human", actorType: "User"),
                    GraphEvent("m-replacement", "area-Bar", actor: "human", actorType: "User")])))));

        List<string> logs = [];

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], Labeler, logs.Add, CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal(52, result.Events.Count);
        AssertVariables(transport.Requests[1], ("issue0", "A", "next-page"));
        var history = AreaLabelHistory.AnalyzeEvents(result.Events, ["area-Bar"], Labeler);
        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.Equal(["area-Foo", "area-Foo", "area-Bar"], history.Events.Select(e => e.Label));
        Assert.Equal([true, false, true], history.Events.Select(e => e.Added));
        Assert.All(history.Events, e => Assert.Equal(history.Events[0].At, e.At));
        Assert.True(history.Consistent);
        Assert.True(history.HumanChanged);
        Assert.Equal("Original area labels differ from current labels; later human label change", history.Status);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task PartialAliasErrorDoesNotDiscardOtherResultsOrContinueFailedAlias()
    {
        using var transport = new AreaLabelGraphQLTransport();

        transport.Respond = (request, _) =>
        {
            AssertVariables(request, ("issue0", "A", null), ("issue1", "B", null));

            return Task.FromResult(GraphResponse(GraphData(
                ("issue0", TimelineNode("A", [GraphEvent("event", "area-Foo")], true, "do-not-follow")),
                ("issue1", TimelineNode("B", [GraphEvent("event", "area-Foo")]))),
                new JsonArray { GraphError("Permission denied", "issue0", "timelineItems", "nodes", 0) }));
        };

        List<string> logs = [];

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B")], Labeler, logs.Add, CancellationToken.None);

        Assert.Contains("Permission denied", results[0].Error, StringComparison.Ordinal);
        Assert.Empty(results[0].Events);
        Assert.Null(results[1].Error);
        Assert.Equal("area-Foo", Assert.Single(results[1].Events).Label);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("no-path")]
    [InlineData("empty-path")]
    [InlineData("unknown-alias")]
    [InlineData("numeric-path")]
    public async Task GlobalGraphQLErrorsFailEveryPendingIssue(string scenario)
    {
        JsonObject error = scenario switch
        {
            "no-path" => new() { ["message"] = "Global failure" },
            "empty-path" => GraphError("Global failure"),
            "unknown-alias" => GraphError("Global failure", "notAnIssue"),
            _ => GraphError("Global failure", 0),
        };

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(GraphData(
                ("issue0", TimelineNode("A", [])), ("issue1", TimelineNode("B", []))), new JsonArray { error })),
        };

        List<string> logs = [];

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B")], Labeler, logs.Add, CancellationToken.None);

        Assert.All(results, result =>
        {
            Assert.Contains("GraphQL timeline error: Global failure", result.Error, StringComparison.Ordinal);
            Assert.Empty(result.Events);
        });

        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task RateLimitMetadataErrorIsLoggedWithoutDiscardingValidHistory()
    {
        JsonObject data = GraphData(("issue0", TimelineNode("A", [GraphEvent("event", "area-Foo")])));
        data["rateLimit"] = null;

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(data,
                new JsonArray { GraphError("Cost metadata unavailable", "rateLimit", "cost") })),
        };

        List<string> logs = [];

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], Labeler, logs.Add, CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal("area-Foo", Assert.Single(result.Events).Label);
        Assert.Contains(logs, log => log.Contains("rate-limit metadata failed: Cost metadata unavailable", StringComparison.Ordinal));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("global")]
    [InlineData("alias")]
    public async Task FailureOnLaterPageDoesNotInvalidateAlreadyCompletedIssue(string failure)
    {
        using var transport = new AreaLabelGraphQLTransport();

        transport.Respond = (_, _) =>
        {
            if (transport.Requests.Count == 1)
            {
                return Task.FromResult(GraphResponse(GraphData(
                    ("issue0", TimelineNode("A", [GraphEvent("first", "area-Foo")], true, "next-page")),
                    ("issue1", TimelineNode("B", [GraphEvent("first", "area-Foo")])))));
            }

            return Task.FromResult(failure switch
            {
                "http" => GraphResponse(null, status: HttpStatusCode.InternalServerError),
                "global" => GraphResponse(null, new JsonArray { GraphError("Query failed") }),
                _ => GraphResponse(GraphData(("issue0", null)),
                    new JsonArray { GraphError("Issue inaccessible", "issue0", "timelineItems") }),
            });
        };

        List<string> logs = [];

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B")], Labeler, logs.Add, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(results[0].Error));
        Assert.Single(results[0].Events);
        Assert.Null(results[1].Error);
        Assert.Equal("area-Foo", Assert.Single(results[1].Events).Label);
        Assert.Equal(2, transport.Requests.Count);
        AssertVariables(transport.Requests[1], ("issue0", "A", "next-page"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOrNullErrorsArrayDoesNotInvalidateSuccessfulResponse(bool explicitNull)
    {
        var envelope = new JsonObject
        {
            ["data"] = GraphData(("issue0", TimelineNode("A", [GraphEvent("event", "area-Foo")]))),
        };

        if (explicitNull)
        {
            envelope["errors"] = null;
        }

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            }),
        };

        List<string> logs = [];

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], Labeler, logs.Add, CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Single(result.Events);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailureMarksEveryPendingIssueWithoutRetryingOrThrowing(HttpStatusCode status)
    {
        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(null, status: status)),
        };

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B")], Labeler, Assert.Fail, CancellationToken.None);

        Assert.All(results, result => Assert.StartsWith("GraphQL timeline request failed:", result.Error, StringComparison.Ordinal));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task MissingOrInvalidResponseEnvelopeIsAnExplicitFailure(string response)
    {
        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
            }),
        };

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], Labeler, Assert.Fail, CancellationToken.None));

        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Empty(result.Events);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("missing-issue")]
    [InlineData("null-issue")]
    [InlineData("mismatched-issue-id")]
    [InlineData("missing-issue-id")]
    [InlineData("missing-timeline")]
    [InlineData("null-timeline")]
    [InlineData("missing-nodes")]
    [InlineData("null-nodes")]
    [InlineData("null-event")]
    [InlineData("missing-event-id")]
    [InlineData("empty-event-id")]
    [InlineData("null-event-id")]
    [InlineData("duplicate-event-id")]
    [InlineData("unexpected-event-type")]
    [InlineData("missing-label")]
    [InlineData("null-label")]
    [InlineData("missing-label-name")]
    [InlineData("null-label-name")]
    [InlineData("empty-label-name")]
    [InlineData("invalid-date")]
    [InlineData("missing-page-info")]
    [InlineData("null-page-info")]
    [InlineData("missing-has-next-page")]
    [InlineData("invalid-has-next-page")]
    [InlineData("missing-cursor")]
    [InlineData("null-cursor")]
    [InlineData("empty-cursor")]
    public async Task InvalidTimelineIsExplicitlyFailedWithoutDiscardingHealthyAlias(string scenario)
    {
        JsonObject item = GraphEvent("event", "area-Foo");
        JsonObject node = TimelineNode("A", [item]);
        JsonObject timeline = node["timelineItems"]!.AsObject();
        JsonObject pageInfo = timeline["pageInfo"]!.AsObject();
        JsonObject data = GraphData(("issue0", node), ("issue1", TimelineNode("B", [])));

        switch (scenario)
        {
            case "missing-issue": data.Remove("issue0"); break;
            case "null-issue": data["issue0"] = null; break;
            case "mismatched-issue-id": node["id"] = "WRONG"; break;
            case "missing-issue-id": node.Remove("id"); break;
            case "missing-timeline": node.Remove("timelineItems"); break;
            case "null-timeline": node["timelineItems"] = null; break;
            case "missing-nodes": timeline.Remove("nodes"); break;
            case "null-nodes": timeline["nodes"] = null; break;
            case "null-event": timeline["nodes"]!.AsArray()[0] = null; break;
            case "missing-event-id": item.Remove("id"); break;
            case "empty-event-id": item["id"] = ""; break;
            case "null-event-id": item["id"] = null; break;
            case "duplicate-event-id": timeline["nodes"]!.AsArray().Add(item.DeepClone()); break;
            case "unexpected-event-type": item["__typename"] = "RenamedTitleEvent"; break;
            case "missing-label": item.Remove("label"); break;
            case "null-label": item["label"] = null; break;
            case "missing-label-name": item["label"]!.AsObject().Remove("name"); break;
            case "null-label-name": item["label"]!["name"] = null; break;
            case "empty-label-name": item["label"]!["name"] = ""; break;
            case "invalid-date": item["createdAt"] = "not a date"; break;
            case "missing-page-info": timeline.Remove("pageInfo"); break;
            case "null-page-info": timeline["pageInfo"] = null; break;
            case "missing-has-next-page": pageInfo.Remove("hasNextPage"); break;
            case "invalid-has-next-page": pageInfo["hasNextPage"] = "true"; break;
            case "missing-cursor": pageInfo["hasNextPage"] = true; pageInfo.Remove("endCursor"); break;
            case "null-cursor": pageInfo["hasNextPage"] = true; pageInfo["endCursor"] = null; break;
            case "empty-cursor": pageInfo["hasNextPage"] = true; pageInfo["endCursor"] = ""; break;
        }

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(data)),
        };

        List<string> logs = [];

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B")], Labeler, logs.Add, CancellationToken.None);

        Assert.StartsWith("Invalid GraphQL timeline:", results[0].Error, StringComparison.Ordinal);
        Assert.Null(results[1].Error);
        Assert.Empty(results[1].Events);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedCursorOrEventIdAcrossPagesFailsOnlyIncompleteHistory(bool repeatedCursor)
    {
        using var transport = new AreaLabelGraphQLTransport();

        transport.Respond = (_, _) => Task.FromResult(transport.Requests.Count == 1
            ? GraphResponse(GraphData(
                ("issue0", TimelineNode("A", [GraphEvent("event", "area-Foo")], true, "cursor")),
                ("issue1", TimelineNode("B", [GraphEvent("event", "area-Foo")]))))
            : GraphResponse(GraphData(("issue0", TimelineNode("A",
                [GraphEvent(repeatedCursor ? "new-event" : "event", "area-Bar")], repeatedCursor, "cursor")))));

        List<string> logs = [];

        var results = await ReadTimelinesAsync(transport.Client, [Issue("A"), Issue("B")], Labeler, logs.Add, CancellationToken.None);

        Assert.Contains(repeatedCursor ? "repeated timeline cursor" : "repeated timeline event ID", results[0].Error, StringComparison.Ordinal);
        Assert.Null(results[1].Error);
        Assert.Single(results[1].Events);
        Assert.Equal(2, transport.Requests.Count);
        AssertVariables(transport.Requests[1], ("issue0", "A", "cursor"));
    }

    [Theory]
    [InlineData("maintainer", "User", 7, true)]
    [InlineData("maintainer", "Bot", 7, false)]
    [InlineData("maintainer", "Mannequin", 7, false)]
    [InlineData("maintainer", null, 7, false)]
    [InlineData("github-actions[bot]", "User", 7, false)]
    [InlineData("dotnet-policy-service", "User", 7, false)]
    [InlineData("DOTNET-POLICY-SERVICE", "User", 7, false)]
    [InlineData("custom-labeler", "User", 7, false)]
    [InlineData("CUSTOM-LABELER", "User", 7, false)]
    [InlineData("renamed-user", "User", 198982749, false)]
    [InlineData("", "User", 7, false)]
    [InlineData(" ", "User", 7, false)]
    [InlineData(null, "User", 7, false)]
    public async Task GraphQLActorClassificationRespectsUserTypeServiceAccountsOverrideAndUnknownLogin(
        string? login, string? actorType, long databaseId, bool expectedHuman)
    {
        JsonObject item = GraphEvent("event", "area-Foo");
        item["actor"] = new JsonObject { ["login"] = login, ["__typename"] = actorType, ["databaseId"] = databaseId };

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(GraphData(("issue0", TimelineNode("A", [item]))))),
        };

        List<string> logs = [];

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], "custom-labeler", logs.Add, CancellationToken.None));

        Assert.Null(result.Error);
        var labelEvent = Assert.Single(result.Events);
        Assert.Equal(login ?? "(unknown)", labelEvent.Actor);
        Assert.Equal(expectedHuman, labelEvent.IsHuman);
    }

    [Fact]
    public async Task NullActorIsNotAHumanOrObservedLabeler()
    {
        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(GraphData(("issue0", TimelineNode("A", [GraphEvent("event", "area-Foo", actor: null)]))))),
        };

        List<string> logs = [];

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], Labeler, logs.Add, CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal("(unknown)", Assert.Single(result.Events).Actor);
        Assert.False(result.Events[0].IsHuman);
        var history = AreaLabelHistory.AnalyzeEvents(result.Events, ["area-Foo"], Labeler);
        Assert.False(history.ObservedLabeler);
        Assert.Equal("No observed labeler application; reason unknown", history.Status);
    }

    [Theory]
    [InlineData("github-actions", "Bot", "github-actions[bot]", true)]
    [InlineData("github-actions[bot]", "Bot", "github-actions", true)]
    [InlineData("GITHUB-ACTIONS", "Bot", "github-actions[BOT]", true)]
    [InlineData("github-actions[bot]", "Bot", "github-actions[bot]", true)]
    [InlineData("custom-labeler", "Bot", "custom-labeler[bot]", true)]
    [InlineData("other-labeler", "Bot", "custom-labeler[bot]", false)]
    [InlineData("custom-labeler", "User", "custom-labeler[bot]", false)]
    [InlineData("custom-labeler[bot]", "User", "custom-labeler", false)]
    [InlineData("custom-labeler", "Mannequin", "custom-labeler[bot]", false)]
    [InlineData("custom-labeler", null, "custom-labeler[bot]", false)]
    [InlineData("custom-labeler", "User", "CUSTOM-LABELER", true)]
    public async Task GraphQLLabelerMatchingNormalizesOnlyConfirmedBotLogins(
        string login, string? actorType, string labelerActor, bool expectedObserved)
    {
        JsonObject item = GraphEvent("event", "area-Foo", actor: login);
        item["actor"]!["__typename"] = actorType;

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(GraphData(("issue0", TimelineNode("A", [item]))))),
        };

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], labelerActor, _ => { }, CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal(login, Assert.Single(result.Events).Actor);

        var history = AreaLabelHistory.AnalyzeEvents(result.Events, ["area-Foo"], labelerActor);

        Assert.True(history.Consistent);
        Assert.Equal(expectedObserved, history.ObservedLabeler);
        Assert.Equal(expectedObserved ? ["area-Foo"] : Array.Empty<string>(), history.OriginalLabels);
    }

    [Theory]
    [InlineData("github-actions[bot]")]
    [InlineData("github-actions")]
    public async Task GraphQLBotFallbackBeforeHumanLabelingIsScoredAsAbstention(string labelerActor)
    {
        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, _) => Task.FromResult(GraphResponse(GraphData(("issue0", TimelineNode("A",
            [
                GraphEvent("fallback", "needs-area-label", actor: "github-actions"),
                GraphEvent("area", "area-VM-coreclr", actor: "jeffschwMSFT", actorType: "User"),
                GraphEvent("removal", "needs-area-label", added: false, actor: "teo-tsirpanis", actorType: "User"),
            ]))))),
        };

        var result = Assert.Single(await ReadTimelinesAsync(transport.Client, [Issue("A")], labelerActor, _ => { }, CancellationToken.None));
        Assert.Null(result.Error);

        var evaluation = new AreaLabelEvaluation(134128, "https://github.com/dotnet/runtime/issues/134128", "Issue", "open",
            ["area-VM-coreclr", "untriaged"])
        {
            History = AreaLabelHistory.AnalyzeEvents(result.Events, ["area-VM-coreclr", "untriaged"], labelerActor),
            Suggestions = [new("area-VM-coreclr", 0.99)],
        };

        Assert.True(evaluation.History.ObservedLabeler);
        Assert.True(evaluation.History.Consistent);
        Assert.True(evaluation.History.HumanChanged);
        Assert.Equal(["needs-area-label"], evaluation.History.OriginalLabels);
        Assert.Equal("Applied needs-area-label (abstained); later human label change", evaluation.History.Status);

        var report = new AreaLabelBacktestReport(new("dotnet/runtime", 134128, 1, labelerActor));
        report.Issues.Add(evaluation);

        string text = report.ToText();

        Assert.Single(report.OriginalScored);
        Assert.Contains("Current area labels -> prediction: 1/1 exact matches; 0 differences", text, StringComparison.Ordinal);
        Assert.Contains("Original labeler -> prediction: 0/1 exact matches; 1 differences", text, StringComparison.Ordinal);
        Assert.Contains("Current area labels -> original labeler: 0/1 exact matches; 1 differences", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationBeforeOrDuringTransportPropagatesInsteadOfReturningFailure(bool beforeRequest)
    {
        using var cancellation = new CancellationTokenSource();

        using var transport = new AreaLabelGraphQLTransport
        {
            Respond = (_, ct) =>
            {
                Assert.True(ct.CanBeCanceled);
                cancellation.Cancel();

                return Task.FromCanceled<HttpResponseMessage>(ct);
            },
        };

        if (beforeRequest)
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadTimelinesAsync(transport.Client, [Issue("A")], Labeler, Assert.Fail, cancellation.Token));

        Assert.Equal(beforeRequest ? 0 : 1, transport.Requests.Count);
    }

    private static async Task<(List<AreaLabelEvent> Events, string? Error)[]> ReadTimelinesAsync(
        GithubGraphQLClient client,
        IReadOnlyList<Issue> issues,
        string labelerActor,
        Action<string> debugLog,
        CancellationToken cancellationToken)
    {
        var (timelines, _, _, _) = await client.GetIssueLabelTimelinesAsync([.. issues.Select(i => i.NodeId)], debugLog, cancellationToken);

        return [.. timelines.Select(t => (t.Events.Select(e => AreaLabelEvent.FromGraphQL(e, labelerActor)).ToList(), t.Error))];
    }

    private static Issue Issue(string? nodeId) =>
        new SimpleJsonSerializer().Deserialize<Issue>(JsonSerializer.Serialize(new { node_id = nodeId }));
}

internal sealed class AreaLabelGraphQLTransport : HttpMessageHandler
{
    private readonly HttpClient _http;
    public GithubGraphQLClient Client { get; }
    public List<JsonElement> Requests { get; } = [];
    public Func<JsonElement, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

    public AreaLabelGraphQLTransport()
    {
        _http = new HttpClient(this, disposeHandler: false);
        Client = new GithubGraphQLClient("tests", ["test-token"], NullLogger.Instance, _http);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/graphql", request.RequestUri?.AbsoluteUri);
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        JsonElement body = document.RootElement.Clone();
        Requests.Add(body);
        AssertReadOnlyTimelineQuery(body);

        return Respond is null ? DefaultGraphResponse(body) : await Respond(body, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    internal static void AssertVariables(JsonElement request, params (string Alias, string Id, string? Cursor)[] expected)
    {
        JsonElement variables = request.GetProperty("variables");
        Assert.Equal(expected.Length * 2, variables.EnumerateObject().Count());

        foreach (var (alias, id, cursor) in expected)
        {
            Assert.Equal(id, variables.GetProperty($"{alias}Id").GetString());
            Assert.Equal(cursor, variables.GetProperty($"{alias}Cursor").GetString());
        }
    }

    private static void AssertReadOnlyTimelineQuery(JsonElement body)
    {
        string query = Assert.IsType<string>(body.GetProperty("query").GetString());
        query = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("query", query, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rateLimit { cost remaining resetAt }", query, StringComparison.Ordinal);
        Assert.Contains("... on Issue", query, StringComparison.Ordinal);
        Assert.Contains("... on PullRequest", query, StringComparison.Ordinal);
        Assert.Contains("... on LabeledEvent", query, StringComparison.Ordinal);
        Assert.Contains("... on UnlabeledEvent", query, StringComparison.Ordinal);
        Assert.Contains("actor { ... ActorIds }", query, StringComparison.Ordinal);
        Assert.Contains("fragment ActorIds on Actor { __typename login", query, StringComparison.Ordinal);
        Assert.Contains("... on User { id databaseId }", query, StringComparison.Ordinal);
        Assert.Contains("pageInfo { hasNextPage endCursor }", query, StringComparison.Ordinal);
        var variables = body.GetProperty("variables").EnumerateObject().ToArray();
        Assert.InRange(variables.Length, 2, 50);
        Assert.Equal(0, variables.Length % 2);

        foreach (var variable in variables.Where(v => v.Name.EndsWith("Id", StringComparison.Ordinal)))
        {
            string alias = variable.Name[..^2];
            Assert.Contains($"${alias}Id: ID!", query, StringComparison.Ordinal);
            Assert.Contains($"${alias}Cursor: String", query, StringComparison.Ordinal);
            Assert.Contains($"{alias}: node(id: ${alias}Id)", query, StringComparison.Ordinal);
            Assert.Contains($"timelineItems(first: 50, after: ${alias}Cursor, itemTypes: [LABELED_EVENT, UNLABELED_EVENT])", query, StringComparison.Ordinal);
            Assert.True(body.GetProperty("variables").TryGetProperty($"{alias}Cursor", out _));
        }
    }

    internal static HttpResponseMessage DefaultGraphResponse(JsonElement request)
    {
        var nodes = request.GetProperty("variables").EnumerateObject()
            .Where(variable => variable.Name.EndsWith("Id", StringComparison.Ordinal))
            .Select(variable => (variable.Name[..^2], (JsonNode?)TimelineNode(variable.Value.GetString()!, [GraphEvent("event", "area-Foo")])))
            .ToArray();

        return GraphResponse(GraphData(nodes));
    }

    internal static JsonObject GraphData(params (string Alias, JsonNode? Node)[] issues)
    {
        var data = new JsonObject
        {
            ["rateLimit"] = new JsonObject { ["cost"] = 7, ["remaining"] = 4993, ["resetAt"] = "2026-09-18T20:00:00Z" },
        };

        foreach (var (alias, node) in issues)
        {
            data.Add(alias, node);
        }

        return data;
    }

    internal static JsonObject TimelineNode(string id, IEnumerable<JsonObject> events, bool hasNextPage = false, string? endCursor = null) =>
        new()
        {
            ["id"] = id,
            ["timelineItems"] = new JsonObject
            {
                ["nodes"] = new JsonArray([.. events]),
                ["pageInfo"] = new JsonObject { ["hasNextPage"] = hasNextPage, ["endCursor"] = endCursor },
            },
        };

    internal static JsonObject GraphEvent(
        string id, string label, bool added = true, string? actor = "github-actions[bot]", string actorType = "Bot") =>
        new()
        {
            ["__typename"] = added ? "LabeledEvent" : "UnlabeledEvent",
            ["id"] = id,
            ["createdAt"] = "2026-09-11T12:00:00Z",
            ["actor"] = actor is null ? null : new JsonObject { ["login"] = actor, ["__typename"] = actorType, ["databaseId"] = 7 },
            ["label"] = new JsonObject { ["name"] = label },
        };

    internal static JsonObject GraphError(string message, params object[] path) =>
        new() { ["message"] = message, ["path"] = JsonSerializer.SerializeToNode(path) };

    internal static HttpResponseMessage GraphResponse(JsonObject? data, JsonArray? errors = null, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                new JsonObject { ["data"] = data, ["errors"] = errors ?? [] }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
}
