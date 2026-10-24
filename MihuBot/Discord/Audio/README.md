# Audio playback

YouTube track details use yt-dlp, the same extractor used for audio playback.
Loading audio refreshes the title, uploader, duration, and thumbnail from that
response so playback reporting does not rely on unrelated watch-page player data.

## Spoken music commands

With Azure OpenAI configured, startup prepares one local Whisper `small.en` model,
automatically downloading it into `State/Whisper/ggml-small.en.bin` if missing.
Downloads are integrity-checked and installed atomically; failed downloads do not
leave a cached partial model. Preparation runs in the background, and failures
disable spoken commands without preventing startup.

The player posts a listening notice in the voice channel's text chat before
consuming incoming audio. If the notice cannot be posted or the local model
cannot load, listening stays disabled and the player reports the failure;
music playback and text commands remain available.
Frames buffered during model preparation or before listening starts are discarded,
so old speech cannot be processed as a newly captured command.

Say "Hey MihuBot, queue song <artist and title>", "Hey MihuBot, pause",
"Hey MihuBot, resume", "Hey MihuBot, skip", or "Hey MihuBot, stop".
For intentional multi-track skips, say "Hey MihuBot, skip 3 songs" or
"Hey MihuBot, skip until the track by Darude". Counts include the current track.
Skip-until uses the queued titles and uploader names to select an existing track
and stops before it. A missing or removed destination leaves the queue unchanged.
Stop disconnects the player and ends listening. Successful commands are
acknowledged in the player's text channel and queued songs retain the speaker
as their requester. Failed commands mention only the requester; successful
acknowledgements do not ping them. Only current, non-bot voice participants can issue commands.
Natural phrasing is supported: local mention detection gates a structured LLM
request that extracts a single intent. Only text from the bot mention onward is
sent, including a short same-speaker follow-up after the mention; other speech
remains local unless the song-specific audio fallback below is needed.
Unsupported, ambiguous, quoted, or negated requests should produce no action.
The current queue's titles and uploader names are supplied as untrusted text
metadata so the LLM can resolve skip-until requests.

One process-wide Whisper model and processor handle all speakers and guilds
sequentially. Speakers have independent audio buffers, not independent inference
contexts. Global runtime `AudioSpeech.Threads` controls CPU threads (default:
up to 8). Changes apply on the next transcription, replacing only the processor,
not reloading or duplicating the model. For example:
`!config set global AudioSpeech.Threads 16`.
Whisper receives vocabulary hints for MihuBot, Mihu-Bot, and Miha Bot.

PCM is downsampled to 16 kHz mono; an energy gate removes silence,
with 200 ms of pre-roll and 1.2 seconds of silence ending an utterance. Continuous
speech is split into at most 12-second windows with 2 seconds of overlap.
Identical commands repeated by overlapping continuous-speech windows are
suppressed; pre-roll overlap between separate utterances does not trigger this.
Skip requests captured before a previous skip changed playback are ignored
across speakers, rather than advancing more tracks from a backlog of repeated
requests. Newly spoken requests after that action are accepted. Repeated
"skip" is not interpreted as a count; ask for an explicit number instead.

Each speaker also has a small, local transcript context. Split or stuttered bot
names can be joined across chunks before invoking AI. After a complete mention,
a follow-up starting within 8 seconds can complete the request, even if it lacks
another mention. Capture timestamps are used, so recognition backlog does not
change this window. Context expires, is capped at 1000 characters, and clears
after a recognized command or when the speaker's stream ends.

Up to 256 speech windows can wait for recognition (about 188 MiB of audio at
maximum window size). Windows older than 20 seconds
are discarded, and overload/failure notices are rate-limited. Disconnecting or
replacing a speaker's stream cancels its queued recognition. Transcription
history inside Whisper is reset between windows; the explicit short-lived text
context is private to each speaker.

For queue requests, search results are checked against the requested song rather
than accepting the first result. A convincing match needs no audio upload.
When matching fails or the names appear garbled, one complete same-speaker clip
of at most 20 seconds can be sent to `gpt-audio-1.5` for written name clarification.
The existing text model interprets that evidence, searches once more, and checks
the new results. Uncertain audio, failed matching, incomplete/oversized clips,
or an expired request ask the requester to repeat instead of guessing.
This uses the same 20-second request deadline as other voice commands.

The audio deployment uses the personal `AzureOpenAI:Key3` endpoint, never a work
endpoint. Input is mono 16 kHz PCM WAV; only text output is requested and response
storage is disabled. Audio calls bypass normal chat payload logging and SDK
message-content logging. The provider's data-retention policies still apply.

Primary speech recognition is local. Audio is held in bounded memory buffers,
not recorded to disk. Clips follow chunk boundaries, so clarification can include
lead-in speech from that speaker, but never a mix of participants' streams.
Non-queue commands and ordinary conversation do not trigger audio uploads.
Unaddressed transcript tails and request audio stay only in bounded local memory
while checking for a split name or follow-up.
Bot-addressed text uses the guild's existing AI model settings and AI logging.
This includes the written evidence returned by the audio model, but not audio bytes.
Each cloud AI send also produces a debug log prefixed with `[AudioCloudAI]`, with the guild, purpose, and transcript
text, including song-matching retries and written audio evidence. Audio-send logs
include the WAV byte count and duration, never the audio bytes. These logs do not
require `LoggerTrace` and are not suppressed by `LoggingChatClient.LogUsageOnly`.
Recognized song queries are sent to YouTube search as with text music commands.
The energy gate is not a trained speech/noise classifier, and Whisper can
misrecognize names or hallucinate commands. Noise, accents, music leaking into
microphones, and speech near window boundaries can affect reliability.

See [deployment configuration](../../../deploy/README.md#optional-integrations)
for model installation and native runtime requirements.
