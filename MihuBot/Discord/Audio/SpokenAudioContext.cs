#nullable enable

namespace MihuBot.Discord.Audio;

internal sealed class SpokenAudioContext(Func<string, CancellationToken, Task<SpokenAudioCommand?>> parse)
{
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromSeconds(8);
    private string _text = "";
    private long _lastSpeechEnd;
    private long _addressedAt;
    private readonly List<SpeechSegment> _audio = [];
    private bool _audioTruncated;

    public async Task<SpokenAudioCommand?> ParseAsync(SpeechSegment segment, string transcript, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? addressed = Append(segment, transcript);

        if (addressed is null)
        {
            return null;
        }

        SpokenAudioCommand? command = await parse(addressed, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (command is not null)
        {
            if (command.Action == SpokenAudioAction.Queue && !_audioTruncated)
            {
                command = command with { AudioClip = SpokenAudioClip.Create(_audio, addressed) };
            }

            Reset();
        }

        return command;
    }

    private string? Append(SpeechSegment segment, string transcript)
    {
        if ((_lastSpeechEnd != 0 && Stopwatch.GetElapsedTime(_lastSpeechEnd, segment.SpeechStartTimestamp) > FollowUpWindow) ||
            (_addressedAt != 0 && Stopwatch.GetElapsedTime(_addressedAt, segment.SpeechStartTimestamp) > FollowUpWindow))
        {
            Reset();
        }

        _lastSpeechEnd = segment.SpeechEndTimestamp;
        string? addressed = SpokenAudioCommand.GetAddressedText(transcript);

        if (addressed is not null)
        {
            _addressedAt = segment.SpeechEndTimestamp;
            _audio.Clear();
            _audioTruncated = false;
        }
        else
        {
            string combined = _text.Length == 0 ? transcript : _text + "\n" + transcript;
            addressed = SpokenAudioCommand.GetAddressedText(combined);

            if (addressed is null)
            {
                _text = combined.Length <= 200 ? combined : combined.Substring(combined.Length - 200);
                RetainAudio(segment);
                return null;
            }

            if (_addressedAt == 0)
            {
                _addressedAt = segment.SpeechEndTimestamp;
            }
        }

        if (addressed.Length > 1000)
        {
            Reset();
            throw new InvalidOperationException("The bot-addressed speech context is too long. Please repeat a shorter request.");
        }

        _text = addressed;
        RetainAudio(segment);
        return addressed;
    }

    private void RetainAudio(SpeechSegment segment)
    {
        _audio.Add(segment);

        while (_audio.Count > 1 && (segment.EndSample - _audio[0].StartSample > SpokenAudioClip.MaxSamples ||
            _audio.Sum(part => part.Samples.Length) > SpokenAudioClip.MaxSamples))
        {
            _audio.RemoveAt(0);
            _audioTruncated = true;
        }
    }

    private void Reset()
    {
        _text = "";
        _lastSpeechEnd = 0;
        _addressedAt = 0;
        _audio.Clear();
        _audioTruncated = false;
    }
}
