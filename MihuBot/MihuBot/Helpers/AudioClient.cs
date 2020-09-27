using Discord.Audio;
using Discord.WebSocket;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MihuBot.Helpers
{
    public class AudioClient
    {
        private static readonly Dictionary<ulong, Task<AudioClient>> _audioClients = new Dictionary<ulong, Task<AudioClient>>();

        public static async Task<AudioClient> TryGetOrJoinAsync(SocketGuild guild, SocketVoiceChannel channelToJoin)
        {
            TaskCompletionSource<AudioClient> tcs = null;
            Task<AudioClient> audioClientTask;

            lock (_audioClients)
            {
                if (!_audioClients.TryGetValue(guild.Id, out audioClientTask))
                {
                    tcs = new TaskCompletionSource<AudioClient>(TaskCreationOptions.RunContinuationsAsynchronously);
                    audioClientTask = tcs.Task;

                    _audioClients.Add(guild.Id, audioClientTask);
                }
            }

            if (tcs != null)
            {
                try
                {
                    await channelToJoin.ConnectAsync(selfDeaf: false);
                    tcs.SetResult(new AudioClient(guild));
                }
                catch (Exception ex)
                {
                    lock (_audioClients)
                    {
                        _audioClients.Remove(guild.Id);
                    }

                    tcs.SetException(ex);
                }
            }

            return await audioClientTask;
        }

        private readonly SocketGuild _guild;

        private AudioClient(SocketGuild guild)
        {
            _guild = guild;
            _guild.AudioClient.StreamCreated += AudioClient_StreamCreatedAsync;
            _guild.AudioClient.Disconnected += AudioClient_DisconnectedAsync;
        }

        private async Task AudioClient_DisconnectedAsync(Exception arg)
        {
            await Logger.Instance.DebugAsync(nameof(AudioClient_DisconnectedAsync), logOnly: true);

            lock (_audioClients)
            {
                _audioClients.Remove(_guild.Id);
            }
        }

        private Task AudioClient_StreamCreatedAsync(ulong id, AudioInStream stream)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Logger.Instance.DebugAsync($"Enter: {nameof(AudioClient_StreamCreatedAsync)}", logOnly: true);

                    var config = SpeechConfig.FromSubscription(Secrets.AzureSpeech.SubscriptionKey, Secrets.AzureSpeech.Region);

                    using var pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetCompressedFormat(AudioStreamContainerFormat.OGG_OPUS));
                    using var audioInput = AudioConfig.FromStreamInput(pushStream);
                    using var recognizer = new SpeechRecognizer(config, audioInput);

                    recognizer.SessionStarted += (s, e) =>
                    {
                        Logger.Instance.DebugAsync($"SessionStarted", logOnly: true).GetAwaiter().GetResult();
                    };

                    recognizer.SessionStopped += (s, e) =>
                    {
                        Logger.Instance.DebugAsync($"SessionStopped", logOnly: true).GetAwaiter().GetResult();
                    };

                    recognizer.Recognizing += (s, e) =>
                    {
                        Logger.Instance.DebugAsync($"Recognizing: Text={e.Result.Text}", logOnly: true).GetAwaiter().GetResult();
                    };

                    recognizer.Recognized += (s, e) =>
                    {
                        if (e.Result.Reason == ResultReason.RecognizedSpeech)
                        {
                            Logger.Instance.DebugAsync($"Recognized: Text={e.Result.Text}", logOnly: true).GetAwaiter().GetResult();
                        }
                    };

                    recognizer.Canceled += (s, e) =>
                    {
                        string reason = e.Reason.ToString();

                        if (e.Reason == CancellationReason.Error)
                        {
                            reason += "\nErrorCode: " + e.ErrorCode;
                            reason += "\nErrorDetails: " + e.ErrorDetails;
                        }

                        Logger.Instance.DebugAsync($"Canceled: {reason}", logOnly: true).GetAwaiter().GetResult();
                    };

                    await recognizer.StartContinuousRecognitionAsync();
                    try
                    {
                        byte[] buffer = new byte[4096];

                        int read;
                        while ((read = await stream.ReadAsync(buffer)) > 0)
                        {
                            pushStream.Write(buffer, read);
                        }
                    }
                    finally
                    {
                        await Logger.Instance.DebugAsync(nameof(recognizer.StopContinuousRecognitionAsync), logOnly: true);
                        await recognizer.StopContinuousRecognitionAsync();
                    }
                }
                catch (Exception ex)
                {
                    await Logger.Instance.DebugAsync(ex.ToString());
                }

                await Logger.Instance.DebugAsync($"Exit: {nameof(AudioClient_StreamCreatedAsync)}", logOnly: true);
            });

            return Task.CompletedTask;
        }
    }
}
