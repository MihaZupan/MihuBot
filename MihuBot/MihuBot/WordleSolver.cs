using System.Net.Http.Json;
using System.Runtime.CompilerServices;

#nullable enable

namespace MihuBot
{
    public sealed class WordleSolver : IHostedService
    {
        private readonly Logger _logger;
        private readonly InitializedDiscordClient _discord;
        private readonly HttpClient _http;
        private readonly SynchronizedLocalJsonStore<Box<int>> _lastDay = new("WordleSolver.json");

        public WordleSolver(Logger logger, InitializedDiscordClient discord, HttpClient http)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _discord = discord ?? throw new ArgumentNullException(nameof(discord));
            _http = http ?? throw new ArgumentNullException(nameof(http));

            Task.Run(async () =>
            {
                try
                {
                    await discord.WaitUntilInitializedAsync();

                    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                    while (await timer.WaitForNextTickAsync())
                    {
                        var now = DateTime.UtcNow;
                        Box<int> lastDay = await _lastDay.EnterAsync();
                        try
                        {
                            if (now.TimeOfDay.TotalMinutes < 1 || now.DayOfYear == lastDay.Value)
                            {
                                continue;
                            }

                            lastDay.Value = now.DayOfYear;
                        }
                        finally
                        {
                            _lastDay.Exit();
                        }

                        try
                        {
                            WordleConfig config = await _http.GetFromJsonAsync<WordleConfig>("https://gist.githubusercontent.com/MihaZupan/34809d516b7d68238d25b91730c99abb/raw/a0a91a49ee147ad2087b933fa6e22fc72e09cf5a/Wordle.json")
                                ?? throw new Exception("Failed to fetch config");

                            string? solution = SolveWordle(config);

                            if (solution is null)
                            {
                                throw new Exception("No solution was found");
                            }

                            await _discord.GetTextChannel(Channels.TheBoysWordle).TrySendMessageAsync(solution);
                        }
                        catch (Exception ex)
                        {
                            await _logger.DebugAsync($"Failed to solve the wordle: {ex}");
                        }
                    }
                }
                catch { }
            });
        }

        private record WordleConfig(string[] Solutions, string[] PossibleGuesses);

        private string? SolveWordle(WordleConfig config)
        {
            string[] possibleGuesses = config.PossibleGuesses.Concat(config.Solutions).ToHashSet().ToArray();

            Span<string> words = config.Solutions;
            int currentLevel = (int)DateTime.Now.Date.Subtract(new DateTime(2021, 6, 19)).TotalDays;
            string solution = words[currentLevel];

            string? optimalGuess = "roate";
            var knownLetters = new List<char>();
            List<bool?[]> results = new();

            const int MaxRounds = 6;

            for (int round = 1; round <= MaxRounds; round++)
            {
                optimalGuess ??= EvaulateOptimalGuess(possibleGuesses, words, GetKnownLettersString(knownLetters));

                _logger.DebugLog($"[{nameof(WordleSolver)}] Guessing {optimalGuess}");

                var resultFlags = new bool?[5];
                Evaluate(solution, optimalGuess, resultFlags);
                results.Add(resultFlags);

                if (resultFlags.All(f => f == true))
                {
                    _logger.DebugLog($"[{nameof(WordleSolver)}] Found the solution in {round} guesses");

                    return $"Wordle {currentLevel} {round}/{MaxRounds}\n\n" + string.Join('\n', results
                        .Select(resultFlags => string.Concat(resultFlags.Select(flag => flag switch
                        {
                            true => "🟩", // :green_square:
                            false => "⬛", // :black_large_square:
                            null => "🟨" // :yellow_square:
                        }))));
                }

                for (int i = 0; i < resultFlags.Length; i++)
                {
                    if (resultFlags[i] == true && !knownLetters.Contains(optimalGuess[i]))
                    {
                        knownLetters.Add(optimalGuess[i]);
                    }
                }

                words = Reduce(words, optimalGuess, resultFlags, GetKnownLettersString(knownLetters));

                optimalGuess = null;
            }

            return null;

            static string GetKnownLettersString(List<char> knownLetters)
            {
                return new string(knownLetters.ToArray()) + new string('\0', 5 - knownLetters.Count);
            }
        }

        private static string EvaulateOptimalGuess(string[] possibleGuesses, Span<string> words, string knownLetters)
        {
            int min = int.MaxValue;
            List<string> minWords = new();
            List<(string, int)> results = new();

            foreach (string word in possibleGuesses)
            {
                int sum = EvaluateGuess(words, word, knownLetters);

                if (sum <= min)
                {
                    if (sum < min)
                    {
                        min = sum;
                        minWords.Clear();
                    }
                    minWords.Add(word);
                }

                results.Add((word, sum));
            }

            results.Sort((a, b) => a.Item2.CompareTo(b.Item2));

            foreach (string minWord in minWords)
            {
                if (words.Contains(minWord))
                {
                    return minWord;
                }
            }

            return minWords[0];
        }

        private static int EvaluateGuess(ReadOnlySpan<string> words, string word, string knownLetters)
        {
            var resultBuffer = new bool?[5];
            int sum = 0;
            foreach (string correctWord in words)
            {
                Evaluate(correctWord, word, resultBuffer);
                int reduced = ReduceCount(words, word, resultBuffer, knownLetters);
                sum += reduced;
            }
            return sum;
        }

        private Span<string> Reduce(Span<string> words, string guessWord, bool?[] results, string knownLetters)
        {
            guessWord = guessWord.ToLowerInvariant();

            _logger.DebugLog($"[{nameof(WordleSolver)}] Evaluating {words.Length} words for {guessWord}");

            int index = 0;
            foreach (var (guess, result) in guessWord.Zip(results))
            {
                switch (result)
                {
                    case true: // green
                        {
                            int newCount = 0;
                            foreach (var word in words)
                            {
                                if (word[index] == guess)
                                {
                                    words[newCount++] = word;
                                }
                            }
                            words = words.Slice(0, newCount);
                        }
                        break;

                    case false: // black
                        {
                            int newCount = 0;
                            if (knownLetters.Contains(guess))
                            {
                                foreach (var word in words)
                                {
                                    if (word[index] != guess)
                                    {
                                        words[newCount++] = word;
                                    }
                                }
                            }
                            else
                            {
                                foreach (var word in words)
                                {
                                    if (!word.Contains(guess))
                                    {
                                        words[newCount++] = word;
                                    }
                                }
                            }
                            words = words.Slice(0, newCount);
                        }
                        break;

                    case null: // yellow
                        {
                            int newCount = 0;
                            foreach (var word in words)
                            {
                                if (word.Contains(guess) && word[index] != guess)
                                {
                                    words[newCount++] = word;
                                }
                            }
                            words = words.Slice(0, newCount);
                        }
                        break;
                }

                index++;
            }

            _logger.DebugLog($"[{nameof(WordleSolver)}] {words.Length} words remain");

            return words;
        }

        private static void Evaluate(string correctResult, string guessWord, bool?[] results)
        {
            for (int i = 0; i < correctResult.Length; i++)
            {
                char c = guessWord[i];

                if (correctResult[i] == c)
                {
                    results[i] = true;
                }
                else if (FastContains(correctResult, c))
                {
                    results[i] = null;
                }
                else
                {
                    results[i] = false;
                }
            }
        }

        private static int ReduceCount(ReadOnlySpan<string> words, string guessWord, bool?[] results, string knownLetters)
        {
            int count = 0;
            foreach (string word in words)
            {
                for (int i = 0; i < guessWord.Length; i++)
                {
                    char c = guessWord[i];

                    switch (results[i])
                    {
                        case true:
                            if (word[i] != c)
                            {
                                goto NextWord;
                            }
                            break;

                        case false:
                            if (FastContains(knownLetters, c))
                            {
                                if (word[i] == c)
                                {
                                    goto NextWord;
                                }
                            }
                            else if (FastContains(word, c))
                            {
                                goto NextWord;
                            }
                            break;

                        case null:
                            if (!FastContains(word, c) || word[i] == c)
                            {
                                goto NextWord;
                            }
                            break;
                    }
                }

                count++;

            NextWord:;
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool FastContains(string word, char c)
        {
            return 4u < (uint)word.Length &&
                (word[0] == c || word[1] == c || word[2] == c || word[3] == c || word[4] == c);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
