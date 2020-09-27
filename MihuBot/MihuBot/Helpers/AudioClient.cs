﻿using Discord.Audio;
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

        private Task AudioClient_DisconnectedAsync(Exception arg)
        {
            lock (_audioClients)
            {
                _audioClients.Remove(_guild.Id);
            }
            return Task.CompletedTask;
        }

        private Task AudioClient_StreamCreatedAsync(ulong id, AudioInStream stream)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var config = SpeechConfig.FromSubscription(Secrets.AzureSpeech.SubscriptionKey, Secrets.AzureSpeech.Region);

                    using var pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetCompressedFormat(AudioStreamContainerFormat.OGG_OPUS));
                    using var audioInput = AudioConfig.FromStreamInput(pushStream);
                    using var recognizer = new SpeechRecognizer(config, audioInput);

                    recognizer.Recognized += (s, e) =>
                    {
                        if (e.Result.Reason == ResultReason.RecognizedSpeech)
                        {
                            Logger.Instance.DebugAsync($"RECOGNIZED: Text={e.Result.Text}", logOnly: true).GetAwaiter().GetResult();
                        }
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
                        await recognizer.StopContinuousRecognitionAsync();
                    }
                }
                catch (Exception ex)
                {
                    await Logger.Instance.DebugAsync(ex.ToString());
                }
            });

            return Task.CompletedTask;
        }
    }
}
