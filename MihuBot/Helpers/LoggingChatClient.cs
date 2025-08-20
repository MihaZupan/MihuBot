using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MihuBot.Configuration;

#nullable enable

namespace MihuBot.Helpers;

// Based on https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/LoggingChatClient.cs
public sealed class LoggingChatClient : DelegatingChatClient
{
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;

    private bool LogUsageOnly => _configuration.GetOrDefault(null, $"{nameof(LoggingChatClient)}.{nameof(LogUsageOnly)}", false);

    public LoggingChatClient(IChatClient innerClient, Logger logger, IConfigurationService configuration) : base(innerClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LogInvokedSensitive(nameof(GetResponseAsync), AsJson(messages), AsJson(options), AsJson(this.GetService<ChatClientMetadata>()));

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);

            LogUsage(response.Usage);

            LogCompletedSensitive(nameof(GetResponseAsync), AsJson(response));

            return response;
        }
        catch (OperationCanceledException)
        {
            LogInvocationCanceled(nameof(GetResponseAsync));
            throw;
        }
        catch (Exception ex)
        {
            LogInvocationFailed(nameof(GetResponseAsync), ex);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LogInvokedSensitive(nameof(GetStreamingResponseAsync), AsJson(messages), AsJson(options), AsJson(this.GetService<ChatClientMetadata>()));

        IAsyncEnumerator<ChatResponseUpdate> e;
        try
        {
            e = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogInvocationCanceled(nameof(GetStreamingResponseAsync));
            throw;
        }
        catch (Exception ex)
        {
            LogInvocationFailed(nameof(GetStreamingResponseAsync), ex);
            throw;
        }

        try
        {
            ChatResponseUpdate? update = null;
            while (true)
            {
                try
                {
                    if (!await e.MoveNextAsync())
                    {
                        break;
                    }

                    update = e.Current;
                }
                catch (OperationCanceledException)
                {
                    LogInvocationCanceled(nameof(GetStreamingResponseAsync));
                    throw;
                }
                catch (Exception ex)
                {
                    LogInvocationFailed(nameof(GetStreamingResponseAsync), ex);
                    throw;
                }

                foreach (AIContent content in update.Contents)
                {
                    if (content is UsageContent uc)
                    {
                        LogUsage(uc.Details);
                    }
                }

                LogStreamingUpdateSensitive(AsJson(update));

                yield return update;
            }

            LogCompleted(nameof(GetStreamingResponseAsync));
        }
        finally
        {
            await e.DisposeAsync();
        }
    }

    private static string AsJson<T>(T value) => JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);

    private void LogInvokedSensitive(string methodName, string messages, string chatOptions, string chatClientMetadata) => Log($"{methodName} invoked: {messages}. Options: {chatOptions}. Metadata: {chatClientMetadata}");

    private void LogCompleted(string methodName) => Log($"{methodName} completed");

    private void LogCompletedSensitive(string methodName, string chatResponse) => Log($"{methodName} completed: {chatResponse}");

    private void LogStreamingUpdateSensitive(string chatResponseUpdate) => Log($"GetStreamingResponseAsync received update: {chatResponseUpdate}");

    private void LogInvocationCanceled(string methodName) => Log($"{methodName} canceled");

    private void LogInvocationFailed(string methodName, Exception error) => Log($"{methodName} failed", error);

    private void LogUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        Log($"Usage: {AsJson(usage)}", isUsage: true);
    }

    private void Log(string message, Exception? ex = null, bool isUsage = false)
    {
        if (!isUsage && LogUsageOnly)
        {
            return;
        }

        _logger.DebugLog($"[{nameof(LoggingChatClient)}] {message}.{(ex is null ? "" : $" Ex: {ex}.")}");
    }
}
