using Octokit.GraphQL;
using Octokit.GraphQL.Model;

namespace MihuBot.Helpers;

public static class GitHubGraphQLHelper
{
    public static async Task EnableIssueNotifiactionsAsync(this Connection connection, Octokit.Issue issue)
    {
        var mutation = new Mutation()
            .UpdateSubscription(new UpdateSubscriptionInput
            {
                SubscribableId = new ID(issue.NodeId),
                State = SubscriptionState.Subscribed
            })
            .Select(x => new
            {
                x.ClientMutationId
            });

        await connection.Run(mutation);
    }
}
