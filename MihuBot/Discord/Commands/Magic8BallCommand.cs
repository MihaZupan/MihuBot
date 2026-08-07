using Microsoft.Extensions.AI;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.Helpers.AI;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace MihuBot.Discord.Commands;

public sealed class Magic8BallCommand : CommandBase
{
    public override string Command => "magic8ball";
    public override string[] Aliases => new[] { "8ball", "8-ball", "eightball", "eight-ball", "magicconch" };

    protected override int CooldownToleranceCount => 10;
    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(10);

    private enum ResponseType
    {
        Positive,
        Negative,
        Neutral,
        Delay,
        Special,
    }

    private static readonly string[] s_positiveResponses = new[]
    {
        "Yes", "Nah yah", "For sure", "Oh yea, for sure! 100%", "I guess",
    };
    private static readonly string[] s_negativeResponses = new[]
    {
        "Yea nah", "No", "No way", "HELL NO!", "Sorry, that's a no",
    };
    private static readonly string[] s_neutralResponses = new[]
    {
        "Maybe…", "Maybe?", "Unsure"
    };
    private static readonly string[] s_delayResponses = new[]
    {
        "Ask me later", "Not right now", "Ask me later, I’m playing league", "Not right now, I’m playing fortnite",
    };
    private static readonly string[] s_specialResponses = new[]
    {
        "Meow", "Meow meow", "Meeeeoooow",
    };

    private static readonly Dictionary<ulong, UserState> s_userStates = new();
    private readonly IConfigurationService _configurationService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly string[] _commandAndAliases;

    public Magic8BallCommand(IConfigurationService configurationService, IEnumerable<OpenAIService> openAI)
    {
        _configurationService = configurationService;
        // Optional - without it we can only match prompts by prefix.
        _embeddingGenerator = openAI.FirstOrDefault()?.GetEmbeddingGenerator(GitHubDbContext.Defaults.EmbeddingModel);
        _commandAndAliases = Enumerable.Concat(Aliases, new string[] { Command }).ToArray();
    }

    private async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text)
    {
        if (_embeddingGenerator is null)
        {
            return default;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            return await _embeddingGenerator.GenerateVectorAsync(text, cancellationToken: cts.Token);
        }
        catch
        {
            // Fall back to matching prompts by prefix only.
            return default;
        }
    }

    private static double GetSimilarity(ReadOnlyMemory<float> left, ReadOnlyMemory<float> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return 0;
        }

        return TensorPrimitives.CosineSimilarity(left.Span, right.Span);
    }

    private double GetMinimumTextSimilarity()
    {
        if (_configurationService.TryGet(null, "magic8ball.similarity", out string valueString) &&
            uint.TryParse(valueString, out uint value) &&
            value > 0 && value < 100)
        {
            return value / 100d;
        }

        return 0.8d;
    }

    private sealed class UserState
    {
        private const int PromptHistoryLimit = 10;

        private readonly Magic8BallCommand _parent;
        private readonly SemaphoreSlim _lock = new(1);
        private long? _delayStartTimestamp;

        private readonly List<(string Prompt, ReadOnlyMemory<float> Embedding, ResponseType Response, DateTime TimeStamp)> _previousPrompts = new();

        public UserState(Magic8BallCommand parent)
        {
            _parent = parent;
        }

        public async Task<string> GetResponseAsync(string prompt)
        {
            await _lock.WaitAsync();
            try
            {
                ResponseType response = await GetResponseAsyncCore(prompt);

                string[] responseGroup = response switch
                {
                    ResponseType.Positive => s_positiveResponses,
                    ResponseType.Neutral => s_neutralResponses,
                    ResponseType.Negative => s_negativeResponses,
                    ResponseType.Delay => s_delayResponses,
                    ResponseType.Special => s_specialResponses,
                    _ => throw new UnreachableException(response.ToString())
                };

                return responseGroup.Random();
            }
            catch
            {
                return s_neutralResponses.Random();
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<ResponseType> GetResponseAsyncCore(string prompt)
        {
            _previousPrompts.RemoveAll(p => DateTime.UtcNow - p.TimeStamp >= TimeSpan.FromDays(7));

            prompt = prompt.ToLowerInvariant();

            if (prompt.Length < 3)
            {
                return ResponseType.Neutral;
            }

            if (_delayStartTimestamp.HasValue)
            {
                if (Stopwatch.GetElapsedTime(_delayStartTimestamp.Value) < TimeSpan.FromMinutes(1))
                {
                    return ResponseType.Delay;
                }

                _delayStartTimestamp = null;
            }

            ReadOnlyMemory<float> promptEmbedding = default;
            bool hasEmbedding = false;

            async Task<ReadOnlyMemory<float>> GetPromptEmbeddingAsync()
            {
                if (!hasEmbedding)
                {
                    promptEmbedding = await _parent.GetEmbeddingAsync(prompt);
                    hasEmbedding = true;
                }

                return promptEmbedding;
            }

            if (_previousPrompts.Count > 0)
            {
                int matchingPrompt = -1;

                for (int i = 0; i < _previousPrompts.Count; i++)
                {
                    var previous = _previousPrompts[i].Prompt;

                    if (previous.StartsWith(prompt, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingPrompt = i;
                        break;
                    }
                }

                if (matchingPrompt < 0)
                {
                    ReadOnlyMemory<float> embedding = await GetPromptEmbeddingAsync();

                    (double similarity, int index) = _previousPrompts
                        .Select((previous, i) => (Similarity: GetSimilarity(embedding, previous.Embedding), i))
                        .MaxBy(r => r.Similarity);

                    if (similarity > _parent.GetMinimumTextSimilarity())
                    {
                        matchingPrompt = index;
                    }
                }

                if (matchingPrompt >= 0)
                {
                    var previous = _previousPrompts[matchingPrompt];
                    _previousPrompts[matchingPrompt] = previous with { TimeStamp = DateTime.UtcNow };
                    _previousPrompts.Sort(static (a, b) => b.TimeStamp.CompareTo(a.TimeStamp));
                    return previous.Response;
                }
            }

            ResponseType response = GetRandomResponse();

            if (response == ResponseType.Delay)
            {
                _delayStartTimestamp = Stopwatch.GetTimestamp();
            }
            else
            {
                if (_previousPrompts.Count == PromptHistoryLimit)
                {
                    _previousPrompts.RemoveAt(_previousPrompts.Count - 1);
                }

                _previousPrompts.Add((prompt, await GetPromptEmbeddingAsync(), response, DateTime.UtcNow));
            }

            return response;
        }

        private static ResponseType GetRandomResponse()
        {
            return Rng.Next(100) switch
            {
                // 30% yes, 30% no, 25% neutral, 10% delay, 5% special
                < 30 => ResponseType.Positive,
                < 60 => ResponseType.Negative,
                < 85 => ResponseType.Neutral,
                < 95 => ResponseType.Delay,
                _ => ResponseType.Special
            };
        }
    }

    public override Task ExecuteAsync(CommandContext ctx)
    {
        return HandleAsync(ctx.Channel, ctx.AuthorId, ctx.ArgumentStringTrimmed);
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        string content = ctx.Content;

        foreach (string command in _commandAndAliases)
        {
            if (content.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsync(ctx.Channel, ctx.AuthorId, content.Substring(command.Length).Trim());
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketTextChannel channel, ulong authorId, string prompt)
    {
        UserState userState;
        lock (s_userStates)
        {
            userState = CollectionsMarshal.GetValueRefOrAddDefault(s_userStates, authorId, out _) ??= new(this);
        }

        string response = await userState.GetResponseAsync(prompt);

        await channel.SendMessageAsync(response);
    }
}
