using Discord;
using Discord.WebSocket;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public sealed class InitializedDiscordClient : DiscordSocketClient
    {
        private readonly TokenType _tokenType;
        private readonly string _token;

        private Task _tcsTask;

        public InitializedDiscordClient(DiscordSocketConfig config, TokenType tokenType, string token)
            : base(config)
        {
            _tokenType = tokenType;
            _token = token;
        }

        public async Task EnsureInitializedAsync()
        {
            if (_tcsTask is null)
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.CompareExchange(ref _tcsTask, tcs.Task, null) is null)
                {
                    try
                    {
                        await InitializeAsync();
                        tcs.SetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }
            }

            await _tcsTask;
        }

        private async Task InitializeAsync()
        {
            var onConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Connected += () => { onConnectedTcs.TrySetResult(); return Task.CompletedTask; };

            var onReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ready += () => { onReadyTcs.TrySetResult(); return Task.CompletedTask; };

            await LoginAsync(_tokenType, _token);
            await StartAsync();

            await onConnectedTcs.Task;
            await onReadyTcs.Task;
        }
    }
}
