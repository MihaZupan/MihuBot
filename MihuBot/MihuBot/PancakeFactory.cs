using Discord.WebSocket;
using Markdig.Helpers;
using Microsoft.Extensions.Hosting;
using MihuBot.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MihuBot
{
    public sealed class PancakeFactory : IHostedService
    {
        private readonly InitializedDiscordClient _discord;
        private readonly HttpClient _http;
        private readonly SynchronizedLocalJsonStore<Dictionary<string, string>> _triviaAnswers = new("TriviaAnswers.json");
        private Timer _workTimer;

        private int _counter;
        private TaskCompletionSource<SocketMessage> _expectedMessageTcs;

        public PancakeFactory(InitializedDiscordClient discord, HttpClient httpClient)
        {
            _discord = discord;
            _http = httpClient;
        }

        private async Task<string> GetNewSessionToken( CancellationToken cancellationToken)
        {
            string newTokenJson = await _http.GetStringAsync("https://opentdb.com/api_token.php?command=request", cancellationToken);
            return JToken.Parse(newTokenJson)["token"].ToObject<string>();
        }

        private async Task UpdateTriviaAnswersAsync(CancellationToken cancellationToken)
        {
            const string SessionTokenKey = "opentdb-session-token";

            Dictionary<string, string> triviaAnswers = await _triviaAnswers.EnterAsync();
            try
            {
                bool requestedNewToken = false;

                if (!triviaAnswers.TryGetValue(SessionTokenKey, out string sessionToken))
                {
                    triviaAnswers.Clear();
                    sessionToken = triviaAnswers[SessionTokenKey] = await GetNewSessionToken(cancellationToken);
                    requestedNewToken = true;
                }

                for (int i = 0; i < 1_000; i++)
                {
                    string responseJson = await _http.GetStringAsync($"https://opentdb.com/api.php?amount=50&token={sessionToken}", cancellationToken);
                    var response = JToken.Parse(responseJson);
                    int responseCode = response["response_code"].ToObject<int>();

                    if (responseCode == 3 && !requestedNewToken)
                    {
                        triviaAnswers.Clear();
                        sessionToken = triviaAnswers[SessionTokenKey] = await GetNewSessionToken(cancellationToken);
                        requestedNewToken = true;
                        continue;
                    }

                    if (responseCode != 0)
                    {
                        break;
                    }

                    foreach (var result in response["results"].AsJEnumerable())
                    {
                        string key = result["question"].ToObject<string>();
                        string value = result["correct_answer"].ToObject<string>();
                        triviaAnswers.TryAdd(HttpUtility.HtmlDecode(key), HttpUtility.HtmlDecode(value));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                _triviaAnswers.Exit();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await UpdateTriviaAnswersAsync(cancellationToken);
            await _discord.EnsureInitializedAsync();
            _discord.MessageReceived += OnMessageAsync;
            _workTimer = new Timer(_ => Task.Run(OnWorkTimerAsync), null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _workTimer.DisposeAsync();
        }

        private async Task OnWorkTimerAsync()
        {
            try
            {
                var channel = _discord.GetTextChannel(Channels.DDsPancake);
                if (channel is null)
                    return;

                if (!channel.GetUser(_discord.CurrentUser.Id).GetPermissions(channel).SendMessages)
                    return;

                _counter++;

                _expectedMessageTcs = new TaskCompletionSource<SocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

                await channel.SendMessageAsync("p!work");
                await Task.WhenAny(_expectedMessageTcs.Task, Task.Delay(5000));
                _expectedMessageTcs = null;

                if (_counter % 2 == 0 && _triviaAnswers.DangerousGetValue().Count != 0)
                {
                    // Trivia
                    _expectedMessageTcs = new TaskCompletionSource<SocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

                    await channel.SendMessageAsync("p!trivia hard");
                    Task triviaResponse = await Task.WhenAny(_expectedMessageTcs.Task, Task.Delay(5000));
                    SocketMessage triviaResponseMessage = triviaResponse == _expectedMessageTcs.Task ? _expectedMessageTcs.Task.Result : null;
                    _expectedMessageTcs = null;

                    if (triviaResponseMessage is not null &&
                        triviaResponseMessage.Content.Contains("answer this question") &&
                        triviaResponseMessage.Embeds.Any())
                    {
                        var description = triviaResponseMessage.Embeds.First().Description.SplitLines(removeEmpty: true);
                        var question = description[0];
                        var answers = description.Skip(1).Where(l => l.StartsWith('[')).ToArray();

                        if (_triviaAnswers.DangerousGetValue().TryGetValue(question, out string answer))
                        {
                            var answerOption = answers.FirstOrDefault(a => a.Contains(answer));
                            if (answerOption is not null && answerOption[1].IsDigit())
                            {
                                int answerNumber = answerOption[1] - '0';

                                _expectedMessageTcs = new TaskCompletionSource<SocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

                                await channel.SendMessageAsync($"{answerNumber}");
                                await Task.WhenAny(_expectedMessageTcs.Task, Task.Delay(5000));
                                _expectedMessageTcs = null;
                            }
                        }
                    }
                }

                if (_counter % 8 == 0)
                {
                    await Task.Delay(2000);

                    _expectedMessageTcs = new TaskCompletionSource<SocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

                    await channel.SendMessageAsync("p!deposit all");
                    await Task.WhenAny(_expectedMessageTcs.Task, Task.Delay(5000));
                    _expectedMessageTcs = null;
                }

                _workTimer.Change(TimeSpan.FromSeconds(303), Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private Task OnMessageAsync(SocketMessage message)
        {
            if (message.Author.Id == 239631525350604801ul && message.Channel.Id == Channels.DDsPancake)
            {
                _expectedMessageTcs?.TrySetResult(message);
            }

            return Task.CompletedTask;
        }
    }
}
