using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using MihuBot.DB.GitHub;
using MihuBot.Discord.Commands;
using MihuBot.RuntimeUtils.AI;
using Octokit;
using Octokit.Internal;
using static MihuBot.Tests.RuntimeUtils.AreaLabelGraphQLTransport;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelBacktestTests
{
    private const string Labeler = "github-actions[bot]";

    [Theory]
    [InlineData("123", "dotnet/runtime", 123)]
    [InlineData("#123", "dotnet/runtime", 123)]
    [InlineData("https://github.com/dotnet/aspnetcore/issues/456", "dotnet/aspnetcore", 456)]
    [InlineData("<https://github.com/dotnet/runtime/issues/123>", "dotnet/runtime", 123)]
    [InlineData("https://github.com/dotnet/runtime/issues/123#issuecomment-456", "dotnet/runtime", 123)]
    public void ParserAcceptsSingleIssue(string argument, string repository, int number)
    {
        Assert.True(TestLabelsCommand.TryParseArguments([argument], out var request));

        Assert.Equal(repository, request.Repository);
        Assert.Equal(number, request.IssueNumber);
        Assert.Equal(1, request.Count);
        Assert.Equal(Labeler, request.LabelerActor);
    }

    [Theory]
    [InlineData("dotnet/runtime", "1", 1)]
    [InlineData("dotnet/runtime", "1000", 1000)]
    [InlineData("dotnet/runtime", "0005", 5)]
    [InlineData("https://github.com/dotnet/aspnetcore", "25", 25)]
    [InlineData("https://github.com/dotnet/aspnetcore/", "25", 25)]
    public void ParserAcceptsBacktestWithinCountCap(string repository, string count, int expected)
    {
        Assert.True(TestLabelsCommand.TryParseArguments(["backtest", repository, count], out var request));

        Assert.Equal(repository.Contains("aspnetcore", StringComparison.Ordinal) ? "dotnet/aspnetcore" : "dotnet/runtime", request.Repository);
        Assert.Null(request.IssueNumber);
        Assert.Equal(expected, request.Count);
        Assert.Equal(Labeler, request.LabelerActor);
    }

    [Theory]
    [InlineData(false, "--labeler-actor")]
    [InlineData(true, "--labeler-actor")]
    [InlineData(false, "--LABELER-ACTOR")]
    [InlineData(true, "--LABELER-ACTOR")]
    public void ParserAcceptsActorOverrideWithoutMutatingArguments(bool backtest, string option)
    {
        string[] arguments = backtest
            ? ["backtest", "dotnet/runtime", "1000", option, "custom-labeler"]
            : ["123", option, "custom-labeler"];

        string[] original = [.. arguments];

        Assert.True(TestLabelsCommand.TryParseArguments(arguments, out var request));

        Assert.Equal("custom-labeler", request.LabelerActor);
        Assert.Equal(backtest ? 1000 : 1, request.Count);
        Assert.Equal(original, arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("2147483648")]
    [InlineData("999999999999999999999")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("1,000")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("ten")]
    public void ParserRejectsInvalidBacktestCount(string count)
    {
        Assert.False(TestLabelsCommand.TryParseArguments(["backtest", "dotnet/runtime", count], out var request));
        Assert.Null(request);
    }

    public static TheoryData<string[]> InvalidArguments =>
    [
        [],
        [""],
        ["0"],
        ["-1"],
        ["2147483648"],
        ["not-an-issue"],
        ["https://example.com/dotnet/runtime/issues/1"],
        ["https://github.com/dotnet/runtime/issues/0"],
        ["https://github.com/dotnet/runtime/issues/not-a-number"],
        ["123", "extra"],
        ["backtest"],
        ["backtest", "dotnet/runtime"],
        ["backtest", "runtime", "10"],
        ["backtest", "/runtime", "10"],
        ["backtest", "dotnet/", "10"],
        ["backtest", "dotnet/runtime/extra", "10"],
        ["backtest", "https://github.com/dotnet/runtime/issues/123", "10"],
        ["backtest", "https://example.com/dotnet/runtime", "10"],
        ["backtest", "dotnet/runtime", "10", "extra"],
        ["123", "--unknown", "actor"],
        ["123", "--labeler-actor"],
        ["123", "--labeler-actor", ""],
        ["123", "--labeler-actor", "   "],
        ["123", "--labeler-actor", "-actor"],
        ["--labeler-actor", "actor", "123"],
        ["123", "--labeler-actor", "actor", "extra"],
        ["123", "--labeler-actor", "one", "--labeler-actor", "two"],
        ["backtest", "dotnet/runtime", "10", "--labeler-actor"],
        ["backtest", "--labeler-actor", "actor", "dotnet/runtime", "10"],
    ];

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void ParserRejectsMalformedCommands(string[] arguments)
    {
        Assert.False(TestLabelsCommand.TryParseArguments(arguments, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void HumanFirstWithoutLabelerIsOnlyAPossibleSkip()
    {
        var history = Analyze([Event(1, "area-Foo", actor: "maintainer", actorType: "User")], ["area-Foo"]);

        Assert.Equal("No observed labeler application; human labeled (possible existing-label skip)", history.Status);
        Assert.False(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
        Assert.Empty(history.OriginalLabels);
        Assert.True(Assert.Single(history.Events).IsHuman);
    }

    [Theory]
    [InlineData(null, "User")]
    [InlineData("other[bot]", "Bot")]
    public void NonLabelerApplicationDoesNotInventAnOriginalOutcome(string? actor, string actorType)
    {
        var history = Analyze([Event(1, "area-Foo", actor: actor, actorType: actorType)], ["area-Foo"]);

        Assert.Equal("No observed labeler application; reason unknown", history.Status);
        Assert.False(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.Empty(history.OriginalLabels);
        Assert.False(Assert.Single(history.Events).IsHuman);
        Assert.Equal(actor ?? "(unknown)", history.Events[0].Actor);
    }

    [Fact]
    public void EmptyTimelineAndNoRelevantLabelsIsUnknownNotAbstention()
    {
        var history = Analyze([], ["bug"]);

        Assert.Equal("No observed labeler application; reason unknown", history.Status);
        Assert.False(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.Empty(history.OriginalLabels);
        Assert.Empty(history.Events);
    }

    [Fact]
    public void FallbackIsAnObservedAbstention()
    {
        var history = Analyze([Event(1, "needs-area-label")], ["needs-area-label", "bug"]);

        Assert.Equal("Applied needs-area-label (abstained)", history.Status);
        Assert.Equal(["needs-area-label"], history.OriginalLabels);
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
    }

    [Theory]
    [InlineData("custom-labeler[bot]", "Bot", "custom-labeler", true)]
    [InlineData("custom-labeler", "Bot", "custom-labeler[bot]", true)]
    [InlineData("CUSTOM-LABELER[BOT]", "Bot", "custom-labeler", true)]
    [InlineData("custom-labeler", "User", "custom-labeler[bot]", false)]
    [InlineData("custom-labeler[bot]", "User", "custom-labeler", false)]
    [InlineData("custom-labeler", "User", "CUSTOM-LABELER", true)]
    public void RestLabelerMatchingNormalizesOnlyConfirmedBotLogins(
        string login, string actorType, string labelerActor, bool expectedObserved)
    {
        var history = AreaLabelHistory.Analyze(
            [Event(1, "needs-area-label", actor: login, actorType: actorType)], ["needs-area-label"], labelerActor);

        Assert.True(history.Consistent);
        Assert.Equal(expectedObserved, history.ObservedLabeler);
        Assert.Equal(login, Assert.Single(history.Events).Actor);
        Assert.Equal(expectedObserved ? ["needs-area-label"] : Array.Empty<string>(), history.OriginalLabels);
    }

    [Fact]
    public void OriginalBotLabelBatchIsRetainedAndUnrelatedEventsAreIgnored()
    {
        var history = Analyze(
            [
                Event(1, "area-Foo"),
                Event(2, "bug", actor: "maintainer", actorType: "User"),
                Event(3, null, kind: "commented", actor: "maintainer", actorType: "User"),
                Event(4, "area-Bar"),
                Event(5, "area-NotALabelEvent", kind: "renamed"),
                Event(6, "area-FutureEvent", kind: "a-new-event-type"),
                Event(7, null),
            ],
            ["area-Bar", "area-Foo", "bug"]);

        Assert.Equal("Original area labels match current labels", history.Status);
        Assert.Equal(["area-Bar", "area-Foo"], history.OriginalLabels);
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
        Assert.Equal(2, history.Events.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterBotSuccessDoesNotMergeIntoOriginalAbstention(bool removeFallback)
    {
        List<TimelineEventInfo> timeline = [Event(1, "needs-area-label"), Event(2, "area-Foo")];

        if (removeFallback)
        {
            timeline.Add(Event(3, "needs-area-label", kind: "unlabeled"));
        }

        var history = Analyze(timeline, removeFallback ? ["area-Foo"] : ["needs-area-label", "area-Foo"]);

        Assert.Equal("Applied needs-area-label (abstained)", history.Status);
        Assert.Equal(["needs-area-label"], history.OriginalLabels);
        Assert.Empty(AreaLabelHistory.Areas(history.OriginalLabels));
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
    }

    [Fact]
    public void MultipleOriginalAreaAdditionsArePreservedButLaterFallbackAndRerunAreNotMerged()
    {
        var history = Analyze(
            [
                Event(1, "area-Foo"),
                Event(2, "area-Bar"),
                Event(3, "needs-area-label"),
                Event(4, "area-Baz"),
                Event(5, "needs-area-label", kind: "unlabeled"),
            ],
            ["area-Foo", "area-Bar", "area-Baz"]);

        Assert.Equal(["area-Bar", "area-Foo"], history.OriginalLabels);
        Assert.Equal("Original area labels differ from current labels", history.Status);
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("removal")]
    [InlineData("addition")]
    [InlineData("restoration")]
    public void LaterHumanChangesPreserveOriginalLabels(string change)
    {
        List<TimelineEventInfo> timeline = [Event(1, "area-Foo")];

        if (change != "addition")
        {
            timeline.Add(Event(2, "area-Foo", kind: "unlabeled", actor: "maintainer", actorType: "User"));
        }

        if (change != "removal")
        {
            timeline.Add(Event(3, change == "restoration" ? "area-Foo" : "area-Bar", actor: "maintainer", actorType: "User"));
        }

        string[] current = change switch
        {
            "removal" => [],
            "addition" => ["area-Foo", "area-Bar"],
            "restoration" => ["area-Foo"],
            _ => ["area-Bar"],
        };

        var history = Analyze(timeline, current);

        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.True(history.HumanChanged);

        Assert.Equal(change == "restoration"
            ? "Original area labels match current labels; later human edits (final area set restored)"
            : "Original area labels differ from current labels; later human label change", history.Status);
    }

    [Fact]
    public void HumanLabelsBeforeFirstLabelerApplicationAreNotOriginalOrLaterEdits()
    {
        var history = Analyze(
            [Event(1, "area-Human", actor: "maintainer", actorType: "User"), Event(2, "area-Foo")],
            ["area-Human", "area-Foo"]);

        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.Equal("Original area labels differ from current labels", history.Status);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
    }

    [Theory]
    [InlineData("maintainer", "User", true)]
    [InlineData("automation", "Bot", false)]
    [InlineData("automation[bot]", "User", false)]
    [InlineData("automation[BOT]", "User", false)]
    [InlineData("dotnet-policy-service", "User", false)]
    [InlineData("DOTNET-POLICY-SERVICE", "User", false)]
    [InlineData("build-agent", "User", false)]
    [InlineData("build-pipeline", "User", false)]
    [InlineData("Copilot", "User", false)]
    [InlineData("dotnet-bot", "User", false)]
    [InlineData("AutomationBot", "User", false)]
    [InlineData("organization", "Organization", false)]
    [InlineData(null, "User", false)]
    [InlineData("", "User", false)]
    [InlineData("   ", "User", false)]
    public void LaterActorsAreClassifiedWithoutTreatingBotsAsHumans(string? actor, string actorType, bool human)
    {
        var history = Analyze(
            [Event(1, "area-Foo"), Event(2, "area-Bar", actor: actor, actorType: actorType)],
            ["area-Foo", "area-Bar"]);

        Assert.Equal(human, history.Events[1].IsHuman);
        Assert.Equal(human, history.HumanChanged);

        Assert.Equal("Original area labels differ from current labels; later " +
            (human ? "human label change" : "non-human/unknown-actor label change"), history.Status);

        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.True(history.Consistent);
    }

    [Fact]
    public void ActorWithMissingLoginIsUnknownAndNotHuman()
    {
        var timelineEvent = new SimpleJsonSerializer().Deserialize<TimelineEventInfo>("""
            {
                "id": 2,
                "event": "labeled",
                "created_at": "2026-09-11T12:02:00Z",
                "actor": {"id": 7, "type": "User"},
                "label": {"name": "area-Bar"}
            }
            """);

        var history = Analyze([Event(1, "area-Foo"), timelineEvent], ["area-Foo", "area-Bar"]);

        Assert.Equal("(unknown)", history.Events[1].Actor);
        Assert.False(history.Events[1].IsHuman);
        Assert.False(history.HumanChanged);
        Assert.True(history.Consistent);
        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.Equal("Original area labels differ from current labels; later non-human/unknown-actor label change", history.Status);
    }

    [Fact]
    public void ActorOverrideIsCaseInsensitiveAndExcludedFromHumanChanges()
    {
        var history = AreaLabelHistory.Analyze(
            [
                Event(1, "area-Foo", actor: "CUSTOM-LABELER", actorType: "User"),
                Event(2, "area-Foo", kind: "unlabeled", actor: "custom-labeler", actorType: "User"),
                Event(3, "area-Bar", actor: "Custom-Labeler", actorType: "User"),
            ],
            ["area-Bar"], "custom-labeler");

        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);
        Assert.All(history.Events, e => Assert.False(e.IsHuman));
        Assert.Equal("Original area labels differ from current labels", history.Status);
    }

    [Fact]
    public void DefaultBotIsNotTheLabelerWhenActorIsOverridden()
    {
        var history = AreaLabelHistory.Analyze([Event(1, "area-Foo")], ["area-Foo"], "custom-labeler");

        Assert.False(history.ObservedLabeler);
        Assert.Empty(history.OriginalLabels);
        Assert.Equal("No observed labeler application; reason unknown", history.Status);
    }

    [Fact]
    public void HumanReplacingFallbackDoesNotEraseObservedAbstention()
    {
        var history = Analyze(
            [
                Event(1, "needs-area-label"),
                Event(2, "needs-area-label", kind: "unlabeled", actor: "maintainer", actorType: "User"),
                Event(3, "area-Foo", actor: "maintainer", actorType: "User"),
            ],
            ["area-Foo"]);

        Assert.Equal(["needs-area-label"], history.OriginalLabels);
        Assert.Equal("Applied needs-area-label (abstained); later human label change", history.Status);
        Assert.True(history.HumanChanged);
        Assert.True(history.Consistent);
    }

    [Fact]
    public void EditingOnlyFallbackLabelDoesNotClaimAHumanAreaChange()
    {
        var history = Analyze(
            [Event(1, "needs-area-label"), Event(2, "needs-area-label", kind: "unlabeled", actor: "maintainer", actorType: "User")],
            []);

        Assert.Equal("Applied needs-area-label (abstained)", history.Status);
        Assert.False(history.HumanChanged);
        Assert.True(history.Consistent);
    }

    [Theory]
    [InlineData("area-Foo")]
    [InlineData("needs-area-label")]
    public void LaterLabelerRerunDoesNotEraseOriginal(string original)
    {
        var history = Analyze(
            [Event(1, original), Event(2, original, kind: "unlabeled"), Event(3, "area-Bar")],
            ["area-Bar"]);

        Assert.Equal([original], history.OriginalLabels);
        Assert.True(history.ObservedLabeler);
        Assert.True(history.Consistent);
        Assert.False(history.HumanChanged);

        Assert.Equal(original == "needs-area-label"
            ? "Applied needs-area-label (abstained)"
            : "Original area labels differ from current labels", history.Status);
    }

    [Fact]
    public void RerunAfterHumanCorrectionDoesNotMergeIntoOriginalBatch()
    {
        var history = Analyze(
            [
                Event(1, "area-Foo"),
                Event(2, "area-Foo", kind: "unlabeled", actor: "maintainer", actorType: "User"),
                Event(3, "area-Bar", actor: "maintainer", actorType: "User"),
                Event(4, "area-Baz"),
            ],
            ["area-Bar", "area-Baz"]);

        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.True(history.HumanChanged);
        Assert.True(history.Consistent);
    }

    [Fact]
    public void EventsAreOrderedByTimestampThenIdBeforeReconstruction()
    {
        var history = Analyze(
            [
                Event(3, "area-Bar", minute: 2),
                Event(2, "area-Foo", kind: "unlabeled", minute: 2),
                Event(99, "area-Foo", minute: 1),
            ],
            ["area-Bar"]);

        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.True(history.Consistent);
        Assert.Equal(["area-Foo", "area-Foo", "area-Bar"], history.Events.Select(e => e.Label));
        Assert.Equal([true, false, true], history.Events.Select(e => e.Added));
        Assert.True(history.Events[0].At < history.Events[1].At);
        Assert.Equal(history.Events[1].At, history.Events[2].At);
    }

    [Fact]
    public void LabelsAndActorUseCaseInsensitiveSets()
    {
        var history = Analyze(
            [
                Event(1, "AREA-Foo", actor: "GITHUB-ACTIONS[BOT]"),
                Event(2, "area-foo", kind: "unlabeled", actor: "maintainer", actorType: "User"),
                Event(3, "Area-FOO", actor: "maintainer", actorType: "User"),
            ],
            ["area-foo", "AREA-FOO", "bug"]);

        Assert.True(history.Consistent);
        Assert.Equal(["AREA-Foo"], history.OriginalLabels);
        Assert.Equal("Original area labels match current labels; later human edits (final area set restored)", history.Status);
        Assert.Equal(["area-a", "area-B"], AreaLabelHistory.Normalize(["area-B", "area-a", "AREA-B"]));
        Assert.Equal(["area-Foo"], AreaLabelHistory.Areas(["needs-area-label", "bug", "area-Foo", "AREA-FOO"]));
        Assert.True(AreaLabelHistory.Equal(["area-Foo", "AREA-FOO"], ["AREA-foo"]));
        Assert.True(AreaLabelHistory.Equal([], []));
        Assert.False(AreaLabelHistory.Equal(["area-Foo"], ["area-Foo", "area-Bar"]));
        Assert.True(AreaLabelHistory.IsRelevant("NEEDS-AREA-LABEL"));
        Assert.False(AreaLabelHistory.IsArea("needs-area-label"));
        Assert.False(AreaLabelHistory.IsRelevant("not-area-Foo"));
    }

    [Fact]
    public void MissingTimelineForCurrentlyLabeledIssueIsInconsistent()
    {
        var history = Analyze([], ["area-Foo"]);

        Assert.Equal("Timeline inconsistent with current labels; original outcome unknown", history.Status);
        Assert.False(history.Consistent);
        Assert.False(history.ObservedLabeler);
        Assert.Empty(history.OriginalLabels);
    }

    [Theory]
    [InlineData("missing-current")]
    [InlineData("extra-current")]
    [InlineData("duplicate-addition")]
    [InlineData("missing-addition")]
    public void InconsistentTimelineRetainsObservedOriginalButMarksItUnscored(string scenario)
    {
        List<TimelineEventInfo> timeline = [Event(1, "area-Foo")];
        string[] current = ["area-Foo"];

        switch (scenario)
        {
            case "missing-current":
                current = [];
                break;
            case "extra-current":
                current = ["area-Foo", "area-Bar"];
                break;
            case "duplicate-addition":
                timeline.Add(Event(2, "AREA-FOO"));
                break;
            case "missing-addition":
                timeline.Add(Event(2, "area-Bar", kind: "unlabeled"));
                break;
        }

        var history = Analyze(timeline, current);

        Assert.True(history.ObservedLabeler);
        Assert.False(history.Consistent);
        Assert.Equal(["area-Foo"], history.OriginalLabels);
        Assert.Contains("timeline inconsistent with current labels (unscored baseline)", history.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmatchedRemovalWithoutApplicationDoesNotInventOriginal()
    {
        var history = Analyze([Event(1, "area-Foo", kind: "unlabeled")], []);

        Assert.False(history.ObservedLabeler);
        Assert.False(history.Consistent);
        Assert.Empty(history.OriginalLabels);
        Assert.Equal("Timeline inconsistent with current labels; original outcome unknown", history.Status);
    }

    [Fact]
    public void ReportDenominatorsExcludeFailuresUnlabeledAndUnknownBaselinesIndependently()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", null, 10, Labeler));
        var known = Analyze([Event(1, "area-Foo")], ["area-Foo"]);
        report.Issues.Add(Evaluation(1, ["area-Foo"], [new("area-Foo", 0.9)], known));
        report.Issues.Add(Evaluation(2, ["area-Foo"], null, known, "Prediction failed: model unavailable"));
        report.Issues.Add(Evaluation(3, ["area-Foo"], [new("area-Foo", 0.9)], null, "Timeline failed: rate limited"));
        report.Issues.Add(Evaluation(4, [], [], Analyze([], [])));
        report.Issues.Add(Evaluation(5, ["needs-area-label"], [], Analyze([Event(1, "needs-area-label")], ["needs-area-label"])));
        report.Issues.Add(Evaluation(6, ["area-Foo"], [new("area-Bar", 0.9)], Analyze([], ["area-Foo"])));

        report.Issues.Add(Evaluation(7, ["area-Foo"], [new("area-Foo", 0.9)],
            Analyze([Event(1, "area-Foo", actor: "maintainer", actorType: "User")], ["area-Foo"])));

        Assert.Equal([1, 3, 4, 5, 6, 7], report.PredictedIssues.Select(i => i.Number));
        Assert.Equal([1, 3, 6, 7], report.CurrentScored.Select(i => i.Number));
        Assert.Equal([1, 5], report.OriginalScored.Select(i => i.Number));
        Assert.Contains("7/10 issues evaluated, 6 predicted, 2 with errors", report.Summary, StringComparison.Ordinal);
        Assert.Contains("current area labels 3/4; original labeler 2/2", report.Summary, StringComparison.Ordinal);
        string text = report.ToText();
        Assert.Contains("Current area labels -> prediction: 3/4 exact matches; 1 differences", text, StringComparison.Ordinal);
        Assert.Contains("Original labeler -> prediction: 2/2 exact matches; 0 differences", text, StringComparison.Ordinal);
        Assert.Contains("Current area labels -> original labeler: 2/2 exact matches; 0 differences", text, StringComparison.Ordinal);
        Assert.Contains("Original labels: (not observed; not an abstention)", text, StringComparison.Ordinal);
        Assert.Contains("Original labels: \"needs-area-label\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedButInconsistentBaselineIsExcludedEvenWhenPredictionMatches()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", null, 1, Labeler));
        var history = Analyze([Event(1, "area-Foo"), Event(2, "area-Foo")], ["area-Foo"]);
        report.Issues.Add(Evaluation(1, ["area-Foo"], [new("area-Foo", 1)], history));

        Assert.Single(report.CurrentScored);
        Assert.Empty(report.OriginalScored);
        Assert.Contains("current area labels 1/1; original labeler 0/0", report.Summary, StringComparison.Ordinal);
        Assert.Contains("Current area labels -> original labeler: 0/0", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Original comparison:", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyReportAndAllFailuresHaveZeroDenominators()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", null, 5, Labeler));

        Assert.Contains("0/5 issues evaluated, 0 predicted, 0 with errors", report.Summary, StringComparison.Ordinal);
        Assert.Contains("current area labels 0/0; original labeler 0/0", report.ToText(), StringComparison.Ordinal);

        report.Issues.Add(Evaluation(1, ["area-Foo"], null, null, "Timeline failed", "Prediction failed"));

        Assert.Contains("1/5 issues evaluated, 0 predicted, 1 with errors", report.Summary, StringComparison.Ordinal);
        Assert.Empty(report.PredictedIssues);
        Assert.Empty(report.CurrentScored);
        Assert.Empty(report.OriginalScored);
        Assert.Contains("Prediction: FAILED", report.ToText(), StringComparison.Ordinal);
        Assert.Contains("Original outcome: Timeline unavailable", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfusionCountsShowTenFooMissesWithEightBarAndTwoBazReplacements()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", null, 10, Labeler));

        for (int i = 1; i <= 10; i++)
        {
            report.Issues.Add(Evaluation(i, ["area-Foo"], [new(i <= 8 ? "area-Bar" : "area-Baz", 0.9)],
                Analyze([Event(1, "area-Foo")], ["area-Foo"])));
        }

        string text = report.ToText();

        Assert.Contains("Current area labels -> prediction: 0/10 exact matches; 10 differences", text, StringComparison.Ordinal);
        Assert.Contains("Original labeler -> prediction: 0/10 exact matches; 10 differences", text, StringComparison.Ordinal);
        Assert.Contains("area-Foo: missed 10 of 10 reference issues", text, StringComparison.Ordinal);
        Assert.Contains("8: \"area-Foo\" => \"area-Bar\"", text, StringComparison.Ordinal);
        Assert.Contains("2: \"area-Foo\" => \"area-Baz\"", text, StringComparison.Ordinal);
        Assert.Contains("8 => \"area-Bar\"", text, StringComparison.Ordinal);
        Assert.Contains("2 => \"area-Baz\"", text, StringComparison.Ordinal);
        Assert.Contains("area-Bar: 8", text, StringComparison.Ordinal);
        Assert.Contains("area-Baz: 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfusionCountsUseIssueSetsAndOnlyUnexpectedReplacementLabels()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", null, 3, Labeler));

        report.Issues.Add(Evaluation(1, ["area-Foo", "area-Keep"],
            [new("area-keep", 1), new("area-Bar", 0.9), new("AREA-BAR", 0.8)]));

        report.Issues.Add(Evaluation(2, ["AREA-FOO"], []));
        report.Issues.Add(Evaluation(3, ["area-Foo"], [new("AREA-FOO", 0.9), new("area-foo", 0.8)]));

        Assert.Equal(["area-Bar", "area-keep"], report.Issues[0].Predicted);
        string text = report.ToText();

        Assert.Contains("Current area labels -> prediction: 1/3 exact matches; 2 differences", text, StringComparison.Ordinal);
        Assert.Contains("area-Foo: missed 2 of 3 reference issues", text, StringComparison.Ordinal);
        Assert.Contains("1 => \"area-Bar\"", text, StringComparison.Ordinal);
        Assert.Contains("1 => (none)", text, StringComparison.Ordinal);
        Assert.Contains("area-Bar: 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("area-Keep: missed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportIncludesEveryIssueLabelsOutcomeErrorsAndTimeline()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", null, 3, Labeler));

        var history = Analyze(
            [Event(1, "area-Foo"), Event(2, "area-Foo", kind: "unlabeled", actor: "maintainer", actorType: "User"),
                Event(3, "area-Bar", actor: "maintainer", actorType: "User")], ["area-Bar"]);

        report.Issues.Add(new(11, "https://github.com/dotnet/runtime/issues/11", "First\r\nissue", "closed", ["area-Bar", "bug"])
        {
            History = history,
            Suggestions = [new("area-Foo", 0.75)],
        });

        report.Issues.Add(Evaluation(12, [], [], Analyze([], [])));
        report.Issues.Add(Evaluation(13, ["area-Baz"], null, null, "Timeline failed:\r\nunavailable", "Prediction failed:\nmodel error"));

        string text = report.ToText();
        string details = text[(text.IndexOf("ALL EVALUATED ISSUES", StringComparison.Ordinal) + "ALL EVALUATED ISSUES".Length)..];
        string[] entries = details.Split("dotnet/runtime#", StringSplitOptions.None);

        Assert.Equal(4, entries.Length);
        Assert.Contains("11 [closed] First issue", entries[1], StringComparison.Ordinal);
        Assert.Contains("https://github.com/dotnet/runtime/issues/11", entries[1], StringComparison.Ordinal);
        Assert.Contains("Current labels: \"area-Bar\", \"bug\"", entries[1], StringComparison.Ordinal);
        Assert.Contains("Current areas: \"area-Bar\"", entries[1], StringComparison.Ordinal);
        Assert.Contains("Prediction: area-Foo (75.0", entries[1], StringComparison.Ordinal);
        Assert.Contains("Current comparison: DIFFERENT", entries[1], StringComparison.Ordinal);
        Assert.Contains("Missing vs current: \"area-Bar\"", entries[1], StringComparison.Ordinal);
        Assert.Contains("Extra vs current: \"area-Foo\"", entries[1], StringComparison.Ordinal);
        Assert.Contains($"Original outcome: {history.Status}", entries[1], StringComparison.Ordinal);
        Assert.Contains("Original labels: \"area-Foo\"", entries[1], StringComparison.Ordinal);
        Assert.Contains("Original comparison: MATCH", entries[1], StringComparison.Ordinal);
        Assert.Contains("github-actions[bot] +area-Foo", entries[1], StringComparison.Ordinal);
        Assert.Contains("maintainer -area-Foo", entries[1], StringComparison.Ordinal);
        Assert.Contains("maintainer +area-Bar", entries[1], StringComparison.Ordinal);
        Assert.Contains("12 [open] Issue 12", entries[2], StringComparison.Ordinal);
        Assert.Contains("Prediction: (abstained)", entries[2], StringComparison.Ordinal);
        Assert.Contains("Current comparison: UNSCORED (no current area labels)", entries[2], StringComparison.Ordinal);
        Assert.Contains("Original labels: (not observed; not an abstention)", entries[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Original comparison:", entries[2], StringComparison.Ordinal);
        Assert.Contains("13 [open] Issue 13", entries[3], StringComparison.Ordinal);
        Assert.Contains("Current labels: \"area-Baz\"", entries[3], StringComparison.Ordinal);
        Assert.Contains("Prediction: FAILED", entries[3], StringComparison.Ordinal);
        Assert.Contains("Original outcome: Timeline unavailable", entries[3], StringComparison.Ordinal);
        Assert.Contains("ERROR: Timeline failed: unavailable", entries[3], StringComparison.Ordinal);
        Assert.Contains("ERROR: Prediction failed: model error", entries[3], StringComparison.Ordinal);
        Assert.DoesNotContain("Current comparison:", entries[3], StringComparison.Ordinal);
        Assert.Contains("NOT an as-of-creation replay", text, StringComparison.Ordinal);
        Assert.Contains("No event cannot prove a skipped or unexecuted prediction", text, StringComparison.Ordinal);
        Assert.Contains("No GitHub labels changed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportEscapesQuotedLabelsAndFlattensMultilineText()
    {
        var report = new AreaLabelBacktestReport(new("dotnet/runtime", 1, 1, Labeler));
        report.Issues.Add(Evaluation(1, ["area-\"Quoted\"\r\nlabel"], []));

        string text = report.ToText();

        Assert.Contains("Current labels: \"area-\"\"Quoted\"\" label\"", text, StringComparison.Ordinal);
        Assert.Contains("Missing vs current: \"area-\"\"Quoted\"\" label\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open", null)]
    [InlineData("closed", "2026-09-12T12:00:00Z")]
    public void PredictionInputKeepsSubmissionButStripsLabelsCommentsAssigneesMilestoneAndClosure(string state, string? closedAt)
    {
        var repository = new RepositoryInfo
        {
            Id = 42,
            FullName = "dotnet/runtime",
            Labels = [new() { Name = "area-Foo" }],
        };

        var issue = new SimpleJsonSerializer().Deserialize<Issue>($$"""
            {
                "id": 1234,
                "node_id": "I_123",
                "number": 123,
                "html_url": "https://github.com/dotnet/runtime/issues/123",
                "title": "Submission title",
                "body": "Submission body",
                "state": "{{state}}",
                "state_reason": "completed",
                "created_at": "2026-09-11T12:00:00Z",
                "updated_at": "2026-09-13T12:00:00Z",
                "closed_at": {{JsonSerializer.Serialize(closedAt)}},
                "closed_by": {"id": 9, "login": "closer"},
                "user": {"id": 7, "login": "author", "type": "User"},
                "labels": [{"name": "area-Foo"}, {"name": "bug"}],
                "comments": 17,
                "assignee": {"id": 8, "login": "maintainer"},
                "assignees": [{"id": 8, "login": "maintainer"}],
                "milestone": {"id": 99, "number": 1, "title": "Answer milestone"},
                "reactions": {}
            }
            """);

        IssueInfo input = AreaLabelBacktestService.CreatePredictionInput(repository, issue);

        Assert.Same(repository, input.Repository);
        Assert.Equal(42, input.RepositoryId);
        Assert.Equal("I_123", input.Id);
        Assert.Equal(123, input.Number);
        Assert.Equal(issue.HtmlUrl, input.HtmlUrl);
        Assert.Equal("Submission title", input.Title);
        Assert.Equal("Submission body", input.Body);
        Assert.Equal(issue.CreatedAt.UtcDateTime, input.CreatedAt);
        Assert.Equal(7, input.UserId);
        Assert.Equal(7, input.User.Id);
        Assert.Equal("author", input.User.Login);
        Assert.Equal(IssueType.Issue, input.IssueType);
        Assert.Null(input.PullRequest);
        Assert.Empty(input.Labels);
        Assert.Empty(input.Comments);
        Assert.Empty(input.Assignees);
        Assert.Null(input.Milestone);
        Assert.Null(input.MilestoneId);
        Assert.Null(input.ClosedAt);
        Assert.Equal(2, issue.Labels.Count);
        Assert.Equal(17, issue.Comments);
        Assert.Single(issue.Assignees);
        Assert.NotNull(issue.Milestone);
        Assert.Equal(closedAt is not null, issue.ClosedAt.HasValue);
        Assert.Equal("area-Foo", Assert.Single(repository.Labels).Name);
    }

    [Fact]
    public async Task RunAsyncPaginatesRestIssuesAndGraphQLTimelinesAndPredictsExactlyNUniqueIssues()
    {
        string firstPage = "[" + string.Join(",", new[] { IssueJson(10, closed: true, label: "area-Bar") }
            .Concat(Enumerable.Range(100, 99).Select(number => IssueJson(number, pullRequest: true)))) + "]";

        List<IssueInfo> predictions = [];
        List<string> logs = [];
        using var cancellation = new CancellationTokenSource();

        using var handler = new GitHubHandler(uri =>
        {
            switch (uri.AbsolutePath)
            {
                case "/repositories/1/issues":
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    Assert.Equal("all", query["state"]);
                    Assert.Equal("created", query["sort"]);
                    Assert.Equal("desc", query["direction"]);
                    Assert.Equal("100", query["per_page"]);

                    return query["page"] switch
                    {
                        "1" => JsonResponse(firstPage,
                            next: "https://api.github.com/repositories/1/issues?state=all&sort=created&direction=desc&per_page=100&page=2"),
                        "2" => JsonResponse($"[{IssueJson(10)},{IssueJson(11)},{IssueJson(12, closed: true)},{IssueJson(13)}]",
                            next: "https://api.github.com/repositories/1/issues?state=all&sort=created&direction=desc&per_page=100&page=3"),
                        _ => throw new InvalidOperationException($"Unexpected issue page: {uri}"),
                    };
                default:
                    throw new InvalidOperationException($"Unexpected request: {uri}");
            }
        });

        using var graphQL = new AreaLabelGraphQLTransport();

        graphQL.Respond = (request, _) =>
        {
            if (graphQL.Requests.Count == 1)
            {
                AssertVariables(request, ("issue0", "I_10", null), ("issue1", "I_11", null), ("issue2", "I_12", null));

                return Task.FromResult(GraphResponse(GraphData(
                    ("issue0", TimelineNode("I_10", [GraphEvent("original", "area-Foo")], true, "next-page")),
                    ("issue1", TimelineNode("I_11", [GraphEvent("original", "area-Foo")])),
                    ("issue2", TimelineNode("I_12", [GraphEvent("original", "area-Foo")])))));
            }

            AssertVariables(request, ("issue0", "I_10", "next-page"));

            return Task.FromResult(GraphResponse(GraphData(("issue0", TimelineNode("I_10",
                [GraphEvent("removal", "area-Foo", added: false, actor: "maintainer", actorType: "User"),
                    GraphEvent("replacement", "area-Bar", actor: "maintainer", actorType: "User")])))));
        };

        RepositoryInfo storedRepository = CreateRepository();
        int repositoryReads = 0;

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client, (name, ct) =>
        {
            Assert.Equal("o/r", name);
            Assert.Equal(cancellation.Token, ct);
            repositoryReads++;

            return Task.FromResult(storedRepository);
        }, (repository, issue, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            Assert.Same(storedRepository, repository);
            Assert.Same(repository, issue.Repository);
            Assert.Equal("o/r", repository.FullName);
            Assert.Equal("o", repository.Owner.Login);
            Assert.Equal(["area-Foo", "area-Bar", "bug"], repository.Labels.Select(label => label.Name));
            AssertStrippedPredictionInput(issue);
            predictions.Add(issue);

            return Task.FromResult<AreaLabelSuggestion[]>([new("area-Foo", 0.9)]);
        }, logs.Add);

        var report = await service.RunAsync(new("o/r", null, 3, Labeler), cancellation.Token);

        Assert.Equal([10, 11, 12], report.Issues.Select(issue => issue.Number));
        Assert.Equal([10, 11, 12], predictions.Select(issue => issue.Number));
        Assert.Equal(["closed", "open", "closed"], report.Issues.Select(issue => issue.State));
        Assert.Equal(["area-Bar", "bug"], report.Issues[0].CurrentLabels);
        Assert.Equal(["area-Foo"], report.Issues[0].History.OriginalLabels);
        Assert.Equal("Original area labels differ from current labels; later human label change", report.Issues[0].History.Status);
        Assert.Equal(3, report.Issues[0].History.Events.Length);

        Assert.All(report.Issues, issue =>
        {
            Assert.True(issue.History.Consistent);
            Assert.Empty(issue.Errors);
        });

        Assert.Equal(2, logs.Count);
        Assert.All(logs, message => Assert.StartsWith("Label timeline GraphQL request", message, StringComparison.Ordinal));
        Assert.False(report.Cancelled);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath == "/repositories/1/issues"));
        Assert.Equal(2, graphQL.Requests.Count);
        Assert.Equal(1, repositoryReads);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task RunAsyncSingleIssueUsesDirectEndpointAndRequestedLabelerActor()
    {
        int predictions = 0;

        using var handler = new GitHubHandler(uri => uri.AbsolutePath switch
        {
            "/repositories/1/issues/10" => JsonResponse(IssueJson(10, closed: true)),
            _ => throw new InvalidOperationException($"Unexpected request: {uri}"),
        });

        using var graphQL = new AreaLabelGraphQLTransport
        {
            Respond = (request, _) =>
            {
                AssertVariables(request, ("issue0", "I_10", null));

                return Task.FromResult(GraphResponse(GraphData(("issue0", TimelineNode("I_10",
                    [GraphEvent("original", "area-Foo", actor: "custom-labeler", actorType: "User")])))));
            },
        };

        List<string> logs = [];

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client, GetRepositoryAsync, (_, issue, _) =>
        {
            predictions++;
            AssertStrippedPredictionInput(issue);

            return Task.FromResult<AreaLabelSuggestion[]>([new("area-Foo", 0.9)]);
        }, logs.Add);

        var report = await service.RunAsync(new("o/r", 10, 1, "custom-labeler"), CancellationToken.None);

        Assert.Equal(1, predictions);
        var result = Assert.Single(report.Issues);
        Assert.Equal(10, result.Number);
        Assert.True(result.History.ObservedLabeler);
        Assert.True(result.History.Consistent);
        Assert.Equal(["area-Foo"], result.History.OriginalLabels);
        Assert.False(Assert.Single(result.History.Events).IsHuman);
        Assert.Empty(result.Errors);
        Assert.Single(handler.Requests);
        Assert.Single(graphQL.Requests);
        Assert.StartsWith("Label timeline GraphQL request", Assert.Single(logs), StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/repositories/1/issues");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RunAsyncStopsAtShortIssuePageWhenFewerThanNExist(int count)
    {
        using var handler = new GitHubHandler(uri => uri.AbsolutePath switch
        {
            "/repositories/1/issues" when HttpUtility.ParseQueryString(uri.Query)["page"] == "1" =>
                JsonResponse("[" + string.Join(",", Enumerable.Range(10, count).Select(number => IssueJson(number))) + "]"),
            _ => throw new InvalidOperationException($"Unexpected request: {uri}"),
        });

        using var graphQL = new AreaLabelGraphQLTransport();
        List<string> logs = [];
        int predictions = 0;

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client, GetRepositoryAsync, (_, _, _) =>
        {
            predictions++;

            return Task.FromResult<AreaLabelSuggestion[]>([]);
        }, logs.Add);

        var report = await service.RunAsync(new("o/r", null, 10, Labeler), CancellationToken.None);

        Assert.Equal(count, report.Issues.Count);
        Assert.Equal(count, predictions);
        Assert.Contains($"{count}/10 issues evaluated", report.Summary, StringComparison.Ordinal);
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath == "/repositories/1/issues");
        Assert.Equal(count == 0 ? 0 : 1, graphQL.Requests.Count);
        Assert.All(logs, message => Assert.StartsWith("Label timeline GraphQL request", message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsyncContinuesAfterTimelineAndPredictionFailuresAndLogsEachFailure()
    {
        List<int> predictions = [];
        List<string> logs = [];

        using var handler = new GitHubHandler(uri => uri.AbsolutePath switch
        {
            "/repositories/1/issues" => JsonResponse($"[{IssueJson(10)},{IssueJson(11)},{IssueJson(12)},{IssueJson(13)}]"),
            _ => throw new InvalidOperationException($"Unexpected request: {uri}"),
        });

        using var graphQL = new AreaLabelGraphQLTransport();

        graphQL.Respond = (request, _) =>
        {
            if (graphQL.Requests.Count == 1)
            {
                AssertVariables(request, ("issue0", "I_10", null), ("issue1", "I_11", null), ("issue2", "I_12", null), ("issue3", "I_13", null));

                return Task.FromResult(GraphResponse(GraphData(
                    ("issue0", TimelineNode("I_10", [GraphEvent("original", "area-Foo")], true, "partial-history")),
                    ("issue1", TimelineNode("I_11", [GraphEvent("original", "area-Foo")])),
                    ("issue2", null),
                    ("issue3", TimelineNode("I_13", [GraphEvent("original", "area-Foo")]))),
                    new JsonArray { GraphError("Timeline not found", "issue2", "timelineItems") }));
            }

            AssertVariables(request, ("issue0", "I_10", "partial-history"));

            return Task.FromResult(GraphResponse(GraphData(("issue0", null)),
                new JsonArray { GraphError("Later timeline page unavailable", "issue0", "timelineItems") }));
        };

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client, GetRepositoryAsync, (_, issue, _) =>
        {
            AssertStrippedPredictionInput(issue);
            predictions.Add(issue.Number);

            return issue.Number is 11 or 12
                ? Task.FromException<AreaLabelSuggestion[]>(new InvalidOperationException($"Model failed for {issue.Number}"))
                : Task.FromResult<AreaLabelSuggestion[]>([new("area-Foo", 0.9)]);
        }, logs.Add);

        var report = await service.RunAsync(new("o/r", null, 4, Labeler), CancellationToken.None);

        Assert.Equal([10, 11, 12, 13], predictions);
        Assert.Equal([10, 11, 12, 13], report.Issues.Select(issue => issue.Number));
        Assert.Null(report.Issues[0].History);
        Assert.NotNull(report.Issues[0].Suggestions);
        Assert.StartsWith("Timeline failed:", Assert.Single(report.Issues[0].Errors), StringComparison.Ordinal);
        Assert.NotNull(report.Issues[1].History);
        Assert.Null(report.Issues[1].Suggestions);
        Assert.Equal("Prediction failed: Model failed for 11", Assert.Single(report.Issues[1].Errors));
        Assert.Null(report.Issues[2].History);
        Assert.Null(report.Issues[2].Suggestions);
        Assert.Equal(2, report.Issues[2].Errors.Count);
        Assert.Contains("Prediction failed: Model failed for 12", report.Issues[2].Errors);
        Assert.Empty(report.Issues[3].Errors);
        Assert.True(report.Issues[3].History.Consistent);
        Assert.NotNull(report.Issues[3].Suggestions);
        Assert.Equal([10, 13], report.PredictedIssues.Select(issue => issue.Number));
        Assert.Equal(13, Assert.Single(report.OriginalScored).Number);
        Assert.Contains("4/4 issues evaluated, 2 predicted, 3 with errors", report.Summary, StringComparison.Ordinal);
        Assert.Equal(6, logs.Count);
        Assert.Equal(2, logs.Count(message => message.StartsWith("Label timeline GraphQL request", StringComparison.Ordinal)));
        Assert.Equal(2, graphQL.Requests.Count);
        Assert.Contains(logs, message => message.Contains("Failed to fetch label timeline for https://github.com/o/r/issues/10:", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Failed to fetch label timeline for https://github.com/o/r/issues/12:", StringComparison.Ordinal));

        Assert.Contains(logs, message => message.Contains("Failed to predict labels for https://github.com/o/r/issues/11:", StringComparison.Ordinal) &&
            message.Contains("Model failed for 11", StringComparison.Ordinal));

        Assert.Contains(logs, message => message.Contains("Failed to predict labels for https://github.com/o/r/issues/12:", StringComparison.Ordinal) &&
            message.Contains("Model failed for 12", StringComparison.Ordinal));

        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("missing", "Repository 'o/r' is not tracked in the GitHub database.", 0)]
    [InlineData("private", "Label evaluation is only available for public repositories.", 0)]
    [InlineData("no-areas", "The repository has no active area-* labels to predict.", 0)]
    [InlineData("legacy-areas", "The repository has no active area-* labels to predict.", 0)]
    [InlineData("pull-request", "Please select an issue, not a pull request.", 1)]
    public async Task RunAsyncRejectsUnsupportedInputsBeforePrediction(string scenario, string error, int requests)
    {
        using var handler = new GitHubHandler(uri => uri.AbsolutePath switch
        {
            "/repositories/1/issues/10" => JsonResponse(IssueJson(10, pullRequest: true)),
            _ => throw new InvalidOperationException($"Unexpected request: {uri}"),
        });

        using var graphQL = new AreaLabelGraphQLTransport();
        RepositoryInfo repository = CreateRepository();
        repository.Private = scenario == "private";

        repository.Labels = scenario switch
        {
            "no-areas" => [new() { Name = "bug" }, new() { Name = "needs-area-label" }],
            "legacy-areas" => [new() { Name = "area-Legacy", Description = "Deprecated label; do not use." }],
            _ => repository.Labels,
        };

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client,
            (_, _) => Task.FromResult(scenario == "missing" ? null! : repository),
            (_, _, _) => throw new InvalidOperationException("Prediction must not run"), message => Assert.Fail(message));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync(new("o/r", 10, 1, Labeler), CancellationToken.None));

        Assert.Equal(error, exception.Message);
        Assert.Equal(requests, handler.Requests.Count);
        Assert.Empty(graphQL.Requests);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncReturnsPartialReportOnCancellationWithoutStartingNextIssue(bool predictionCompleted)
    {
        using var cancellation = new CancellationTokenSource();
        List<string> logs = [];
        List<int> predictions = [];

        using var handler = new GitHubHandler(uri => uri.AbsolutePath switch
        {
            "/repositories/1/issues" => JsonResponse($"[{IssueJson(10)},{IssueJson(11)}]"),
            _ => throw new InvalidOperationException($"Unexpected request after cancellation: {uri}"),
        });

        using var graphQL = new AreaLabelGraphQLTransport();

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client, GetRepositoryAsync, (_, issue, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            predictions.Add(issue.Number);
            cancellation.Cancel();

            return predictionCompleted
                ? Task.FromResult<AreaLabelSuggestion[]>([new("area-Foo", 0.9)])
                : Task.FromCanceled<AreaLabelSuggestion[]>(ct);
        }, logs.Add);

        var report = await service.RunAsync(new("o/r", null, 2, Labeler), cancellation.Token);

        Assert.True(report.Cancelled);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(10, issue.Number);

        if (predictionCompleted)
        {
            Assert.NotNull(issue.Suggestions);
            Assert.Empty(issue.Errors);
            Assert.Single(report.PredictedIssues);
        }
        else
        {
            Assert.Null(issue.Suggestions);
            Assert.Equal("Cancelled before evaluation completed.", Assert.Single(issue.Errors));
            Assert.Empty(report.PredictedIssues);
        }

        Assert.Contains("CANCELLED (partial report)", report.Summary, StringComparison.Ordinal);
        Assert.Contains("1/2 issues evaluated", report.Summary, StringComparison.Ordinal);
        Assert.Equal([10], predictions);
        Assert.StartsWith("Label timeline GraphQL request", Assert.Single(logs), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Single(graphQL.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunAsyncBatches100Then1IssuesAndRetainsFirstBatchIfSecondFetchIsCancelled(
        bool cancelSecondBatch, bool lastPredictionInFirstBatchFails)
    {
        using var cancellation = new CancellationTokenSource();
        List<int> predictions = [];
        List<string> logs = [];

        using var handler = new GitHubHandler(uri => uri.AbsolutePath switch
        {
            "/repositories/1/issues" => HttpUtility.ParseQueryString(uri.Query)["page"] switch
            {
                "1" => JsonResponse("[" + string.Join(",", Enumerable.Range(10, 100).Select(number => IssueJson(number))) + "]"),
                "2" => JsonResponse($"[{IssueJson(110)}]"),
                _ => throw new InvalidOperationException($"Unexpected issue page: {uri}"),
            },
            _ => throw new InvalidOperationException($"Unexpected request: {uri}"),
        });

        using var graphQL = new AreaLabelGraphQLTransport();

        graphQL.Respond = (request, ct) =>
        {
            if (graphQL.Requests.Count == 1)
            {
                Assert.Empty(predictions);
                AssertVariables(request, [.. Enumerable.Range(0, 100).Select(i => ($"issue{i}", $"I_{i + 10}", (string?)null))]);
            }
            else
            {
                AssertVariables(request, ("issue0", "I_110", null));
                Assert.Equal(100, predictions.Count);

                if (cancelSecondBatch)
                {
                    cancellation.Cancel();

                    return Task.FromCanceled<HttpResponseMessage>(ct);
                }
            }

            return Task.FromResult(DefaultGraphResponse(request));
        };

        var service = new AreaLabelBacktestService(handler.CreateClient(), graphQL.Client, GetRepositoryAsync, (_, issue, _) =>
        {
            predictions.Add(issue.Number);

            return lastPredictionInFirstBatchFails && issue.Number == 109
                ? Task.FromException<AreaLabelSuggestion[]>(new InvalidOperationException("Last prediction in first batch failed"))
                : Task.FromResult<AreaLabelSuggestion[]>([new("area-Foo", 0.9)]);
        }, logs.Add);

        var report = await service.RunAsync(new("o/r", null, 101, Labeler), cancellation.Token);

        int expected = cancelSecondBatch ? 100 : 101;
        Assert.Equal(cancelSecondBatch, report.Cancelled);
        Assert.Equal(Enumerable.Range(10, expected), report.Issues.Select(issue => issue.Number));
        Assert.Equal(Enumerable.Range(10, expected), predictions);

        Assert.All(report.Issues, issue =>
        {
            if (lastPredictionInFirstBatchFails && issue.Number == 109)
            {
                Assert.Equal("Prediction failed: Last prediction in first batch failed", Assert.Single(issue.Errors));
                Assert.Null(issue.Suggestions);
            }
            else
            {
                Assert.Empty(issue.Errors);
                Assert.NotNull(issue.Suggestions);
            }

            Assert.True(issue.History.Consistent);
        });

        int failed = lastPredictionInFirstBatchFails ? 1 : 0;
        Assert.Equal(expected - failed, report.PredictedIssues.Length);
        Assert.Contains($"{expected}/101 issues evaluated, {expected - failed} predicted, {failed} with errors", report.Summary, StringComparison.Ordinal);
        Assert.Equal((cancelSecondBatch ? 1 : 2) + failed, logs.Count);
        Assert.Equal(cancelSecondBatch ? 1 : 2, logs.Count(message => message.StartsWith("Label timeline GraphQL request", StringComparison.Ordinal)));
        Assert.Equal(failed, logs.Count(message => message.Contains("Failed to predict labels for https://github.com/o/r/issues/109:", StringComparison.Ordinal)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, graphQL.Requests.Count);
    }

    private static Task<RepositoryInfo> GetRepositoryAsync(string name, CancellationToken cancellationToken)
    {
        Assert.Equal("o/r", name);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(CreateRepository());
    }

    private static RepositoryInfo CreateRepository() => new()
    {
        Id = 1,
        Name = "r",
        FullName = "o/r",
        Owner = new() { Id = 7, Login = "o" },
        Labels = [new() { Name = "area-Foo" }, new() { Name = "area-Bar" }, new() { Name = "bug" }],
    };

    private static string IssueJson(int number, bool closed = false, bool pullRequest = false, string label = "area-Foo") => $$"""
        {
            "node_id":"I_{{number}}",
            "number":{{number}},
            "html_url":"https://github.com/o/r/issues/{{number}}",
            "title":"Title {{number}}",
            "body":"Body {{number}}",
            "state":"{{(closed ? "closed" : "open")}}",
            "created_at":"2026-09-11T12:00:00Z",
            "closed_at":{{(closed ? "\"2026-09-12T12:00:00Z\"" : "null")}},
            "user":{"id":8,"login":"author","type":"User"},
            "labels":[{"name":{{JsonSerializer.Serialize(label)}}},{"name":"bug"}],
            "comments":10,
            "assignees":[{"id":9,"login":"maintainer"}],
            "milestone":{"id":99,"number":1,"title":"Answer milestone"},
            "pull_request":{{(pullRequest ? "{}" : "null")}},
            "reactions":{}
        }
        """;

    private static void AssertStrippedPredictionInput(IssueInfo issue)
    {
        Assert.Equal(1, issue.RepositoryId);
        Assert.Equal($"I_{issue.Number}", issue.Id);
        Assert.Equal($"Title {issue.Number}", issue.Title);
        Assert.Equal($"Body {issue.Number}", issue.Body);
        Assert.Equal("author", issue.User.Login);
        Assert.Equal(8, issue.UserId);
        Assert.Equal(IssueType.Issue, issue.IssueType);
        Assert.Empty(issue.Labels);
        Assert.Empty(issue.Comments);
        Assert.Empty(issue.Assignees);
        Assert.Null(issue.Milestone);
        Assert.Null(issue.MilestoneId);
        Assert.Null(issue.ClosedAt);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK, string? next = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        if (next is not null)
        {
            response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
        }

        return response;
    }

    private sealed class GitHubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        public GitHubClient CreateClient() =>
            new(new Connection(new ProductHeaderValue("tests"), new HttpClientAdapter(() => this)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Requests.Add((request.Method, request.RequestUri));
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri.Host);

            return Task.FromResult(respond(request.RequestUri));
        }
    }

    private static AreaLabelHistory Analyze(IEnumerable<TimelineEventInfo> timeline, string[] current) =>
        AreaLabelHistory.Analyze(timeline, current, Labeler);

    private static TimelineEventInfo Event(
        long id, string? label, string kind = "labeled", string? actor = Labeler, string actorType = "Bot", int? minute = null) =>
        new SimpleJsonSerializer().Deserialize<TimelineEventInfo>(TimelineJson(id, label, kind, actor, actorType, minute));

    private static string TimelineJson(
        long id, string? label, string kind = "labeled", string? actor = Labeler, string actorType = "Bot", int? minute = null) =>
        JsonSerializer.Serialize(new
        {
            id,
            @event = kind,
            created_at = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute ?? id),
            actor = actor is null ? null : new { login = actor, type = actorType },
            label = label is null ? null : new { name = label },
        });

    private static AreaLabelEvaluation Evaluation(
        int number, string[] current, AreaLabelSuggestion[]? suggestions, AreaLabelHistory? history = null, params string[] errors)
    {
        var evaluation = new AreaLabelEvaluation(number, $"https://github.com/dotnet/runtime/issues/{number}", $"Issue {number}", "open", current)
        {
            Suggestions = suggestions,
            History = history,
        };

        evaluation.Errors.AddRange(errors);

        return evaluation;
    }
}
