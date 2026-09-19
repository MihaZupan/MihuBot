using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using static MihuBot.RuntimeUtils.DataIngestion.GitHub.GitHubGraphQL;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class GitHubGraphQLTests
{
    [Fact]
    public async Task ReferencedItemsAreBatchedAndExcludePrivateMissingAndForbiddenItems()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse("""
            {
              "rateLimit":{"cost":6},
              "reference0":{"isPrivate":false,"item":{"url":"https://github.com/o/r/issues/1","title":"Public issue","body":"Issue body","author":{"login":"author"},"repository":{"nameWithOwner":"o/r","isPrivate":false},"labels":{"nodes":[{"name":"area-Test"}]}}},
              "reference1":{"isPrivate":true,"item":{"url":"https://github.com/private/r/issues/2","title":"Private issue","body":"Must not be surfaced","repository":{"nameWithOwner":"private/r","isPrivate":true},"labels":{"nodes":[]}}},
              "reference2":null,
              "reference3":{"isPrivate":false,"item":null},
              "reference4":null,
              "reference5":{"isPrivate":false,"item":{"url":"https://github.com/o/r/pull/6","title":"Public PR","body":"PR body","author":null,"repository":{"nameWithOwner":"o/r","isPrivate":false},"labels":{"nodes":[]}}}
            }
            """), new JsonArray
            {
                Error("Not found", "NOT_FOUND", "reference2"),
                Error("Access denied", "FORBIDDEN", "reference4"),
            })));
        List<string> logs = [];
        var result = await transport.Client.GetReferencedLabelItemsAsync(
            [("o/r", 1), ("private/r", 2), ("o/r", 3), ("o/r", 4), ("o/r", 5), ("o/r", 6)], logs.Add);

        Assert.Equal(["Public issue", "Public PR"], result.Items.Select(i => i.Title));
        Assert.Equal(1, result.Calls);
        Assert.Equal(6, result.Cost);
        Assert.Equal(4, logs.Count);
        Assert.DoesNotContain(logs, l => l.Contains("Must not be surfaced", StringComparison.Ordinal));
        var request = Assert.Single(transport.Requests);
        string query = request.GetProperty("query").GetString()!;
        var variables = request.GetProperty("variables");
        Assert.Contains("... on Issue", query, StringComparison.Ordinal);
        Assert.Contains("... on PullRequest", query, StringComparison.Ordinal);
        Assert.Contains("issueOrPullRequest(number: $number0)", query, StringComparison.Ordinal);
        Assert.Contains("rateLimit { cost }", query, StringComparison.Ordinal);
        Assert.Equal("private", variables.GetProperty("owner1").GetString());
        Assert.Equal("r", variables.GetProperty("name1").GetString());
        Assert.Equal(2, variables.GetProperty("number1").GetInt32());
    }

    [Fact]
    public async Task EmptyOrOversizedReferenceBatchesDoNotCallGitHub()
    {
        using var transport = new GraphQLTransport((_, _) => throw new InvalidOperationException("Must not send a request."));
        var empty = await transport.Client.GetReferencedLabelItemsAsync([], Assert.Fail);

        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Calls);
        Assert.Equal(0, empty.Cost);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.Client.GetReferencedLabelItemsAsync(
            [.. Enumerable.Range(1, ReferencedLabelItemsBatchSize + 1).Select(n => ("o/r", n))], Assert.Fail));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task UnexpectedReferenceQueryErrorsAreNotReportedAsMissingItems()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"rateLimit":{"cost":1},"reference0":null}"""),
            new JsonArray { Error("Query failed", "INTERNAL", "reference0") })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Client.GetReferencedLabelItemsAsync([("o/r", 1)], Assert.Fail));
    }

    [Fact]
    public async Task ReferencedItemsRequireRateLimitMetadata()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"reference0":{"isPrivate":false,"item":null}}"""))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.GetReferencedLabelItemsAsync([("o/r", 1)], _ => { }));

        Assert.Contains("no usable rate-limit metadata", error.Message, StringComparison.Ordinal);
    }

    private const string AliasedQuery = """
        query($id: ID!) {
          first: node(id: $id) { id }
          second: node(id: $id) { id }
          rateLimit { cost remaining resetAt }
        }
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task PullRequestLookupReportsActualCostEvenWhenRepositoryIsMissing(int cost)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["repository"] = null,
            ["rateLimit"] = new JsonObject { ["cost"] = cost },
        })));

        var result = await transport.Client.GetPullRequestLabelInfoAsync("dotnet", "runtime", 42);
        var request = Assert.Single(transport.Requests);

        Assert.Null(result.Repository);
        Assert.Equal(1, result.Calls);
        Assert.Equal(cost, result.Cost);
        Assert.Contains("rateLimit { cost }", request.GetProperty("query").GetString(), StringComparison.Ordinal);
        Assert.Equal(42, request.GetProperty("variables").GetProperty("number").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullRequestLookupsRejectMissingRateLimitMetadata(bool explicitNull)
    {
        using var transport = new GraphQLTransport((_, _) =>
        {
            var data = JsonNode.Parse("""{"repository":null,"path0":{"object":{"history":{"nodes":[]}}}}""")!.AsObject();

            if (explicitNull)
            {
                data["rateLimit"] = null;
            }

            return Task.FromResult(Response(data));
        });

        var metadataError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.GetPullRequestLabelInfoAsync("dotnet", "runtime", 42));
        var historyError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.GetPullRequestFileHistoryAsync("dotnet", "runtime", "base-sha", ["src/file.cs"]));

        Assert.Contains("no usable rate-limit metadata", metadataError.Message, StringComparison.Ordinal);
        Assert.Contains("no usable rate-limit metadata", historyError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullRequestHistoryUsesPathVariablesAndPreservesAliasAssociations()
    {
        string[] paths = ["src/\" tricky\nfile.cs", "src/other.cs"];
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse("""
            {
              "rateLimit": {"cost":7},
              "path1": {"object":{"history":{"nodes":[{"associatedPullRequests":{"nodes":[{"url":"second"}]}}]}}},
              "path0": {"object":{"history":{"nodes":[{"associatedPullRequests":{"nodes":[{"url":"first"}]}}]}}}
            }
            """))));

        var (matches, calls, cost) = await transport.Client.GetPullRequestFileHistoryAsync("dotnet", "runtime", "base-sha", paths);
        var request = Assert.Single(transport.Requests);
        string query = request.GetProperty("query").GetString()!;
        var variables = request.GetProperty("variables");

        for (int i = 0; i < paths.Length; i++)
        {
            Assert.DoesNotContain(paths[i], query, StringComparison.Ordinal);
            Assert.Equal(paths[i], variables.GetProperty($"path{i}").GetString());
            Assert.Contains($"path: $path{i}", query, StringComparison.Ordinal);
        }

        Assert.Equal("dotnet", variables.GetProperty("owner").GetString());
        Assert.Equal("runtime", variables.GetProperty("name").GetString());
        Assert.Equal("base-sha", variables.GetProperty("base").GetString());
        Assert.Contains("object(oid: $base)", query, StringComparison.Ordinal);
        Assert.Equal(paths, matches.Select(m => m.Path));
        Assert.Equal(["first", "second"], matches.Select(m => m.PullRequest.Url));
        Assert.Contains("rateLimit { cost }", query, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.Equal(7, cost);
    }

    [Fact]
    public async Task EmptyPullRequestHistoryLookupMakesNoRequests()
    {
        using var transport = new GraphQLTransport((_, _) => throw new InvalidOperationException("Empty lookup must not send a request."));

        var result = await transport.Client.GetPullRequestFileHistoryAsync("dotnet", "runtime", "base-sha", []);

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.Calls);
        Assert.Equal(0, result.Cost);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task PullRequestHistoryRejectsMissingBaseCommit()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse("""
            {"path0":{"object":null}}
            """))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.GetPullRequestFileHistoryAsync("dotnet", "runtime", "base-sha", ["src/file.cs"]));

        Assert.Equal("GitHub returned no base commit for pull request file history.", error.Message);
    }

    [Fact]
    public async Task AliasedQueryDeserializesFieldsByExactAliasAndPreservesTypedMetadata()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse("""
            {
                "second": {"id":"SECOND"},
                "first": {"id":"FIRST"},
                "unrequested": {"id":"IGNORE"},
                "rateLimit": {"cost":7,"remaining":4993,"resetAt":"2026-09-18T20:00:00Z"}
            }
            """))));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { id = "NODE" }, ["first", "second"]);

        Assert.Equal(["first", "second"], response.Fields.Keys);
        Assert.Equal("FIRST", response.Fields["first"].Data?.Id);
        Assert.Equal("SECOND", response.Fields["second"].Data?.Id);

        Assert.All(response.Fields.Values, field =>
        {
            Assert.Empty(field.Errors);
            Assert.Null(field.InvalidData);
        });

        Assert.NotNull(response.RateLimit);
        Assert.Equal(7, response.RateLimit.Cost);
        Assert.Equal(4993, response.RateLimit.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.Zero), response.RateLimit.ResetAt);
        Assert.Empty(response.RateLimitErrors);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(AliasedQuery, request.GetProperty("query").GetString());
        Assert.Equal("NODE", request.GetProperty("variables").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ExplicitNullAliasIsDistinctFromMissingOrWrongCaseAlias()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse("""
            {"first":null,"SECOND":{"id":"WRONG_CASE"},"rateLimit":{"cost":1}}
            """))));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        Assert.Null(response.Fields["first"].Data);
        Assert.Empty(response.Fields["first"].Errors);
        Assert.Null(response.Fields["first"].InvalidData);
        Assert.Null(response.Fields["second"].Data);
        Assert.Empty(response.Fields["second"].Errors);
        Assert.Equal("GitHub GraphQL returned no field 'second'.", response.Fields["second"].InvalidData);
        Assert.DoesNotContain("SECOND", response.Fields.Keys);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task AliasedQueryMissingDataMarksEachRequestedFieldInvalid(string envelope)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(RawResponse(envelope)));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        Assert.All(response.Fields, pair =>
        {
            Assert.Null(pair.Value.Data);
            Assert.Empty(pair.Value.Errors);
            Assert.Equal($"GitHub GraphQL returned no field '{pair.Key}'.", pair.Value.InvalidData);
        });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"not an object\"")]
    [InlineData("{\"id\":false}")]
    [InlineData("{\"id\":[]}")]
    public async Task MalformedTypedAliasDoesNotDiscardHealthyAlias(string malformed)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["first"] = JsonNode.Parse(malformed),
            ["second"] = new JsonObject { ["id"] = "HEALTHY" },
            ["rateLimit"] = new JsonObject { ["cost"] = 1 },
        })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        Assert.Null(response.Fields["first"].Data);
        Assert.Empty(response.Fields["first"].Errors);
        Assert.StartsWith("Invalid GitHub GraphQL field 'first':", response.Fields["first"].InvalidData, StringComparison.Ordinal);
        Assert.Equal("HEALTHY", response.Fields["second"].Data?.Id);
        Assert.Empty(response.Fields["second"].Errors);
        Assert.Null(response.Fields["second"].InvalidData);
        Assert.Equal(1, response.RateLimit?.Cost);
        Assert.Empty(response.RateLimitErrors);
    }

    [Fact]
    public async Task AliasErrorsPreserveStructuredTypePathAndMessagesWithoutDiscardingOtherAliases()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"first":{"id":"MUST_NOT_USE"},"second":{"id":"HEALTHY"}}"""),
            new JsonArray
            {
                Error("Account deleted", "NOT_FOUND", "first", "followers", 0),
                Error("No permission", "FORBIDDEN", "first"),
            })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        var failed = response.Fields["first"];
        Assert.Null(failed.Data);
        Assert.Null(failed.InvalidData);
        Assert.Equal(["Account deleted", "No permission"], failed.Errors.Select(error => error.Message));
        Assert.Equal(["NOT_FOUND", "FORBIDDEN"], failed.Errors.Select(error => error.Type));
        Assert.All(failed.Errors, error => Assert.Equal("first", error.RootField));
        Assert.NotNull(failed.Errors[0].Path);
        Assert.Equal("followers", failed.Errors[0].Path![1].GetString());
        Assert.Equal(0, failed.Errors[0].Path![2].GetInt32());
        Assert.Equal("HEALTHY", response.Fields["second"].Data?.Id);
        Assert.Empty(response.Fields["second"].Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("[0]")]
    [InlineData("[null]")]
    [InlineData("[\"unknown\"]")]
    [InlineData("[\"FIRST\"]")]
    public async Task GlobalOrUnrecognizedErrorPathsInvalidateAllRequestedAliases(string? path)
    {
        var error = new JsonObject { ["message"] = "Global failure", ["type"] = "INTERNAL" };

        if (path is not null)
        {
            error["path"] = JsonNode.Parse(path);
        }

        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"first":{"id":"A"},"second":{"id":"B"}}"""), new JsonArray { error })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        Assert.All(response.Fields.Values, field =>
        {
            Assert.Null(field.Data);
            Assert.Null(field.InvalidData);
            Assert.Equal("Global failure", Assert.Single(field.Errors).Message);
            Assert.Equal("INTERNAL", field.Errors[0].Type);
        });
    }

    [Fact]
    public async Task GlobalAndFieldErrorsAreCombinedOnlyForAffectedAlias()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(null, new JsonArray
        {
            Error("Global failure", "INTERNAL"),
            Error("First failure", "FORBIDDEN", "first"),
        })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        Assert.Equal(["Global failure", "First failure"], response.Fields["first"].Errors.Select(error => error.Message));
        Assert.Equal("Global failure", Assert.Single(response.Fields["second"].Errors).Message);
        Assert.All(response.Fields.Values, field => Assert.Null(field.InvalidData));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cost\":null}")]
    [InlineData("{\"cost\":\"invalid\"}")]
    [InlineData("{\"cost\":1,\"remaining\":{}}")]
    [InlineData("{\"cost\":1,\"resetAt\":\"invalid\"}")]
    [InlineData("[]")]
    public async Task InvalidRateLimitMetadataDoesNotInvalidateHealthyAliases(string metadata)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["first"] = new JsonObject { ["id"] = "HEALTHY" },
            ["rateLimit"] = JsonNode.Parse(metadata),
        })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]);

        Assert.Equal("HEALTHY", response.Fields["first"].Data?.Id);
        Assert.Null(response.Fields["first"].InvalidData);
        Assert.Empty(response.Fields["first"].Errors);
        Assert.Null(response.RateLimit);
        Assert.StartsWith("Invalid rate-limit metadata:", Assert.Single(response.RateLimitErrors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitErrorsAreKeptSeparateFromAliasErrors()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"first":{"id":"A"},"second":{"id":"B"},"rateLimit":{"cost":1}}"""),
            new JsonArray
            {
                Error("Rate metadata unavailable", "INTERNAL", "rateLimit", "cost"),
                Error("Other metadata error", "INTERNAL", "rateLimit"),
                Error("Alias unavailable", "FORBIDDEN", "second"),
            })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first", "second"]);

        Assert.Equal("A", response.Fields["first"].Data?.Id);
        Assert.Empty(response.Fields["first"].Errors);
        Assert.Equal("Alias unavailable", Assert.Single(response.Fields["second"].Errors).Message);
        Assert.Null(response.RateLimit);
        Assert.Equal(["Rate metadata unavailable", "Other metadata error"], response.RateLimitErrors);
    }

    [Theory]
    [InlineData("{\"first\":{\"id\":\"A\"}}")]
    [InlineData("{\"first\":{\"id\":\"A\"},\"rateLimit\":null}")]
    public async Task OptionalRateLimitCanBeAbsentOrNullForGeneralAliasedQueries(string data)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse(data))));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]);

        Assert.Equal("A", response.Fields["first"].Data?.Id);
        Assert.Null(response.RateLimit);
        Assert.Empty(response.RateLimitErrors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RateLimitCostAloneIsValidIncludingZero(int cost)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["first"] = new JsonObject { ["id"] = "A" },
            ["rateLimit"] = new JsonObject { ["cost"] = cost },
        })));

        var response = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]);

        Assert.NotNull(response.RateLimit);
        Assert.Equal(cost, response.RateLimit.Cost);
        Assert.Null(response.RateLimit.Remaining);
        Assert.Null(response.RateLimit.ResetAt);
        Assert.Empty(response.RateLimitErrors);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("empty")]
    public async Task BothQueryModesAcceptAbsentNullOrEmptyErrors(string errors)
    {
        using var transport = new GraphQLTransport((_, _) =>
        {
            var envelope = new JsonObject
            {
                ["data"] = new JsonObject { ["first"] = new JsonObject { ["id"] = "A" } },
            };

            if (errors != "absent")
            {
                envelope["errors"] = errors == "null" ? null : new JsonArray();
            }

            return Task.FromResult(RawResponse(envelope.ToJsonString()));
        });

        var aliased = await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]);
        var ordinary = await transport.Client.RunQueryAsync<Dictionary<string, IdOnlyModel>>(AliasedQuery, new { });

        Assert.Equal("A", aliased.Fields["first"].Data?.Id);
        Assert.Empty(aliased.Fields["first"].Errors);
        Assert.Equal("A", ordinary["first"].Id);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("field")]
    [InlineData("global")]
    [InlineData("rateLimit")]
    public async Task OrdinaryQueryRejectsGraphQLErrorsEvenWhenDataLooksUsable(string location)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"first":{"id":"PARTIAL"}}"""),
            new JsonArray
            {
                location == "global" ? Error("First error", "INTERNAL") : Error("First error", "INTERNAL", location),
                Error("Second error", "FORBIDDEN", "first"),
            })));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.RunQueryAsync<Dictionary<string, IdOnlyModel>>(AliasedQuery, new { }));

        Assert.Equal("GitHub GraphQL query failed: First error; Second error", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task OrdinaryTypedQueryRejectsMissingOrNullData(string envelope)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(RawResponse(envelope)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.RunQueryAsync<Dictionary<string, IdOnlyModel>>(AliasedQuery, new { }));

        Assert.Equal("GitHub GraphQL returned no data.", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task OrdinaryJsonElementQueryAlsoRejectsMissingOrNullData(string envelope)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(RawResponse(envelope)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Client.RunQueryAsync<JsonElement>(AliasedQuery, new { }));

        Assert.Equal("GitHub GraphQL returned no data.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullResponseEnvelopeIsAnExplicitFailureInBothQueryModes(bool aliased)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(RawResponse("null")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (aliased)
            {
                await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]);
            }
            else
            {
                await transport.Client.RunQueryAsync<IdOnlyModel>(AliasedQuery, new { });
            }
        });

        Assert.Equal("GitHub GraphQL returned an empty response.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("{\"data\":")]
    public async Task InvalidJsonEnvelopeThrowsInsteadOfReturningSuccess(string envelope)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(RawResponse(envelope)));

        await Assert.ThrowsAsync<JsonException>(() => transport.Client.RunQueryAsync<IdOnlyModel>(AliasedQuery, new { }));
        await Assert.ThrowsAsync<JsonException>(() => transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsPropagateInBothQueryModes(HttpStatusCode status)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(RawResponse("""{"data":{"id":"IGNORED"}}""", status)));

        var ordinary = await Assert.ThrowsAsync<HttpRequestException>(() => transport.Client.RunQueryAsync<IdOnlyModel>(AliasedQuery, new { }));
        var aliased = await Assert.ThrowsAsync<HttpRequestException>(() => transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"]));

        Assert.Equal(status, ordinary.StatusCode);
        Assert.Equal(status, aliased.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task EmptyUserLookupHasNoNetworkCallsOrCost()
    {
        using var transport = new GraphQLTransport((_, _) => throw new InvalidOperationException("Empty lookup must not send a request."));

        var result = await transport.Client.GetUsers([]);

        Assert.Empty(result.Users);
        Assert.Equal(0, result.Calls);
        Assert.Equal(0, result.Cost);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task UserLookupUsesVariablesForQuotedAndMultilineLoginsAndPreservesInputOrder()
    {
        string[] logins = ["ordinary", "quoted\"login\nsecond-line", "slash\\login", "ordinary"];

        using var transport = new GraphQLTransport((request, _) =>
        {
            string query = request.GetProperty("query").GetString()!;
            JsonElement variables = request.GetProperty("variables");
            Assert.Equal(logins.Length, variables.EnumerateObject().Count());

            for (int i = 0; i < logins.Length; i++)
            {
                Assert.Equal(logins[i], variables.GetProperty($"login{i}").GetString());
                Assert.Contains($"$login{i}: String!", query, StringComparison.Ordinal);
                Assert.Contains($"User{i}: user(login: $login{i})", query, StringComparison.Ordinal);
                Assert.DoesNotContain(logins[i], query, StringComparison.Ordinal);
            }

            Assert.Contains("fragment UserInfo on User", query, StringComparison.Ordinal);
            var data = new JsonObject { ["rateLimit"] = new JsonObject { ["cost"] = 3 } };

            for (int i = logins.Length - 1; i >= 0; i--)
            {
                data.Add($"User{i}", User(logins[i], 5_000_000_000L + i));
            }

            return Task.FromResult(Response(data));
        });

        var result = await transport.Client.GetUsers(logins);

        Assert.Equal(logins, result.Users.Select(user => user.Login));
        Assert.Equal([5_000_000_000L, 5_000_000_001L, 5_000_000_002L, 5_000_000_003L], result.Users.Select(user => user.DatabaseId));
        Assert.Equal(1, result.Calls);
        Assert.Equal(3, result.Cost);
        Assert.Equal("User name", result.Users[0].Name);
        Assert.Equal(12, result.Users[0].Followers.TotalCount);
        Assert.Equal(3, result.Users[0].Following.TotalCount);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task UserLookupPreservesNullAndAliasScopedNotFoundForRestByIdFallback()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["User0"] = User("healthy", 7),
            ["User1"] = null,
            ["User2"] = null,
            ["User3"] = User("also-healthy", 8),
            ["rateLimit"] = new JsonObject { ["cost"] = 1 },
        }, new JsonArray { Error("Could not resolve account", "NOT_FOUND", "User2") })));

        var result = await transport.Client.GetUsers(["healthy", "deleted", "renamed", "also-healthy"]);

        Assert.Equal(4, result.Users.Length);
        Assert.Equal("healthy", result.Users[0].Login);
        Assert.Null(result.Users[1]);
        Assert.Null(result.Users[2]);
        Assert.Equal("also-healthy", result.Users[3].Login);
        Assert.Equal(1, result.Calls);
        Assert.Equal(1, result.Cost);
    }

    [Fact]
    public async Task AliasScopedNotFoundWithoutDataFieldStillPreservesFallbackSlot()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"rateLimit":{"cost":1}}"""),
            new JsonArray { Error("Deleted account", "NOT_FOUND", "User0") })));

        var result = await transport.Client.GetUsers(["deleted"]);

        Assert.Null(Assert.Single(result.Users));
        Assert.Equal(1, result.Cost);
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("global-not-found")]
    [InlineData("unknown-alias-not-found")]
    [InlineData("wrong-case-not-found")]
    [InlineData("untyped-not-found")]
    [InlineData("mixed")]
    public async Task UserLookupRejectsErrorsOtherThanMatchingAliasNotFound(string scenario)
    {
        JsonObject error = scenario switch
        {
            "forbidden" or "mixed" => Error("Lookup failed", "FORBIDDEN", "User0"),
            "global-not-found" => Error("Lookup failed", "NOT_FOUND"),
            "unknown-alias-not-found" => Error("Lookup failed", "NOT_FOUND", "User1"),
            "wrong-case-not-found" => Error("Lookup failed", "NOT_FOUND", "user0"),
            _ => Error("Lookup failed", null, "User0"),
        };

        var errors = new JsonArray { error };

        if (scenario == "mixed")
        {
            errors.Add(Error("Deleted account", "NOT_FOUND", "User0"));
        }

        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["User0"] = User("partial", 7),
            ["rateLimit"] = new JsonObject { ["cost"] = 1 },
        }, errors)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Client.GetUsers(["requested"]));

        Assert.Contains("GitHub user lookup failed:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Lookup failed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    public async Task UserLookupDoesNotTreatMissingOrMalformedAliasAsDeletedAccount(string scenario)
    {
        var data = new JsonObject { ["rateLimit"] = new JsonObject { ["cost"] = 1 } };

        if (scenario == "invalid")
        {
            data["User0"] = new JsonObject { ["databaseId"] = "not a number" };
        }

        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(data)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Client.GetUsers(["requested"]));

        Assert.Contains("User0", error.Message, StringComparison.Ordinal);
        Assert.Contains(scenario == "missing" ? "returned no field" : "Invalid GitHub GraphQL field", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("missing-cost")]
    [InlineData("invalid-cost")]
    [InlineData("metadata-error")]
    public async Task UserLookupRequiresUsableRateLimitMetadata(string scenario)
    {
        var data = new JsonObject { ["User0"] = User("healthy", 7) };

        if (scenario != "missing")
        {
            data["rateLimit"] = scenario switch
            {
                "null" => null,
                "missing-cost" => new JsonObject(),
                "invalid-cost" => new JsonObject { ["cost"] = "not a number" },
                _ => new JsonObject { ["cost"] = 1 },
            };
        }

        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(data,
            scenario == "metadata-error" ? new JsonArray { Error("Cost unavailable", "INTERNAL", "rateLimit", "cost") } : null)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Client.GetUsers(["healthy"]));

        Assert.StartsWith("GitHub user lookup returned no usable rate-limit metadata:", error.Message, StringComparison.Ordinal);

        if (scenario == "metadata-error")
        {
            Assert.Contains("Cost unavailable", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NodeOnlyConnectionStillDeserializesWithoutPageInfo()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"nodes":[{"id":"LABEL"}]}"""))));

        var result = await transport.Client.RunQueryAsync<ConnectionModel<IdOnlyModel>>("query { nodes { id } }", new { });

        Assert.Equal("LABEL", Assert.Single(result.Nodes).Id);
        Assert.Null(result.PageInfo);
    }

    [Fact]
    public async Task PageInfoOnlyConnectionDoesNotRequireNodes()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"pageInfo":{"hasNextPage":false,"endCursor":null}}"""))));

        var result = await transport.Client.RunQueryAsync<ConnectionModel<IdOnlyModel>>("query { pageInfo { hasNextPage endCursor } }", new { });

        Assert.Null(result.Nodes);
        Assert.False(result.PageInfo.HasNextPage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitHasNextPageValuesAreAccepted(bool hasNextPage)
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(new JsonObject
        {
            ["nodes"] = new JsonArray(),
            ["pageInfo"] = new JsonObject { ["hasNextPage"] = hasNextPage, ["endCursor"] = hasNextPage ? "next" : null },
        })));

        var result = await transport.Client.RunQueryAsync<ConnectionModel<IdOnlyModel>>("query { nodes { id } pageInfo { hasNextPage endCursor } }", new { });

        Assert.Empty(result.Nodes);
        Assert.Equal(hasNextPage, result.PageInfo.HasNextPage);
        Assert.Equal(hasNextPage ? "next" : null, result.PageInfo.EndCursor);
    }

    [Fact]
    public async Task MissingHasNextPageIsInvalidForOrdinaryQueryAndIsolatedForAliasedQuery()
    {
        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(
            JsonNode.Parse("""{"nodes":[],"pageInfo":{"endCursor":"cursor"}}"""))));

        await Assert.ThrowsAsync<JsonException>(() => transport.Client.RunQueryAsync<ConnectionModel<IdOnlyModel>>(
            "query { nodes { id } pageInfo { hasNextPage endCursor } }", new { }));

        using var aliasedTransport = new GraphQLTransport((_, _) => Task.FromResult(Response(JsonNode.Parse("""
            {
                "first":{"nodes":[],"pageInfo":{"endCursor":"cursor"}},
                "second":{"nodes":[],"pageInfo":{"hasNextPage":false}}
            }
            """))));

        var result = await aliasedTransport.Client.RunAliasedQueryAsync<ConnectionModel<IdOnlyModel>>(AliasedQuery, new { }, ["first", "second"]);

        Assert.Null(result.Fields["first"].Data);
        Assert.Contains("hasNextPage", result.Fields["first"].InvalidData, StringComparison.Ordinal);
        Assert.NotNull(result.Fields["second"].Data);
        Assert.False(result.Fields["second"].Data!.PageInfo.HasNextPage);
        Assert.Null(result.Fields["second"].InvalidData);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("User")]
    [InlineData("Bot")]
    [InlineData("Mannequin")]
    [InlineData("Organization")]
    public async Task ActorIdsTypeIsOptionalForLegacyPayloadsAndUsesTypenameWhenProvided(string? type)
    {
        var actor = new JsonObject { ["login"] = "actor", ["id"] = "NODE", ["databaseId"] = 42 };

        if (type is not null)
        {
            actor["__typename"] = type;
        }

        using var transport = new GraphQLTransport((_, _) => Task.FromResult(Response(actor)));

        var result = await transport.Client.RunQueryAsync<ActorIdsModel>("query { login id databaseId __typename }", new { });

        Assert.Equal("actor", result.Login);
        Assert.Equal("NODE", result.Id);
        Assert.Equal(42, result.DatabaseId);
        Assert.Equal(type, result.Type);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("aliased")]
    [InlineData("users")]
    public async Task CancellationDuringTransportPropagatesForEveryEntryPoint(string entryPoint)
    {
        using var cancellation = new CancellationTokenSource();

        using var transport = new GraphQLTransport((_, ct) =>
        {
            Assert.True(ct.CanBeCanceled);
            cancellation.Cancel();

            return Task.FromCanceled<HttpResponseMessage>(ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (entryPoint)
            {
                case "ordinary":
                    await transport.Client.RunQueryAsync<IdOnlyModel>(AliasedQuery, new { }, cancellation.Token);
                    break;
                case "aliased":
                    await transport.Client.RunAliasedQueryAsync<IdOnlyModel>(AliasedQuery, new { }, ["first"], cancellation.Token);
                    break;
                case "users":
                    await transport.Client.GetUsers(["actor"], cancellation.Token);
                    break;
            }
        });

        Assert.Single(transport.Requests);
    }

    private static JsonObject User(string login, long databaseId) => new()
    {
        ["id"] = $"USER_{databaseId}",
        ["login"] = login,
        ["databaseId"] = databaseId,
        ["name"] = "User name",
        ["url"] = "https://github.com/example",
        ["createdAt"] = "2020-01-02T03:04:05Z",
        ["followers"] = new JsonObject { ["totalCount"] = 12 },
        ["following"] = new JsonObject { ["totalCount"] = 3 },
    };

    private static JsonObject Error(string message, string? type, params object?[] path) =>
        new() { ["message"] = message, ["type"] = type, ["path"] = JsonSerializer.SerializeToNode(path) };

    private static HttpResponseMessage Response(JsonNode? data, JsonArray? errors = null) =>
        RawResponse(new JsonObject { ["data"] = data, ["errors"] = errors ?? [] }.ToJsonString());

    private static HttpResponseMessage RawResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class GraphQLTransport : HttpMessageHandler
    {
        private readonly Func<JsonElement, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly HttpClient _http;
        public GithubGraphQLClient Client { get; }
        public List<JsonElement> Requests { get; } = [];

        public GraphQLTransport(Func<JsonElement, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
            _http = new HttpClient(this, disposeHandler: false);
            Client = new GithubGraphQLClient("tests", ["test-token"], NullLogger.Instance, _http);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.github.com/graphql", request.RequestUri?.AbsoluteUri);
            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Assert.Contains("tests", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            JsonElement body = document.RootElement.Clone();
            string query = Assert.IsType<string>(body.GetProperty("query").GetString());
            Assert.StartsWith("query", query, StringComparison.Ordinal);
            Assert.DoesNotContain("mutation", query, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(JsonValueKind.Object, body.GetProperty("variables").ValueKind);
            Requests.Add(body);

            return await _respond(body, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _http.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
