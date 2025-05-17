using Octokit.GraphQL;
using Octokit.GraphQL.Model;

namespace MihuBot.Helpers;

public static class GitHubGraphQLHelper
{
    public static async Task EnableIssueNotifiactionsAsync(this Connection connection, string nodeId)
    {
        var mutation = new Mutation()
            .UpdateSubscription(new UpdateSubscriptionInput
            {
                SubscribableId = new ID(nodeId),
                State = SubscriptionState.Subscribed
            })
            .Select(x => new
            {
                x.ClientMutationId
            });

        await connection.Run(mutation);
    }
}
