using Discord.WebSocket;
using MihuBot.Helpers;

namespace MihuBot
{
    public sealed class LoggerOptions
    {
        public readonly InitializedDiscordClient Discord;
        public readonly string LogsRoot, FilesRoot, LogPrefix;

        private readonly ulong
            _debugGuildId, _debugChannelId,
            _logsTextGuildId, _logsTextChannelId,
            _logsFilesGuildId, _logsFilesChannelId;

        public SocketTextChannel DebugTextChannel => Discord.GetTextChannel(_debugGuildId, _debugChannelId);
        public SocketTextChannel LogsTextChannel => Discord.GetTextChannel(_logsTextGuildId, _logsTextChannelId);
        public SocketTextChannel LogsFilesTextChannel => Discord.GetTextChannel(_logsFilesGuildId, _logsFilesChannelId);

        public LoggerOptions(InitializedDiscordClient discord, string logsRoot, string logPrefix,
            ulong debugGuildId, ulong debugChannelId,
            ulong logsTextGuildId, ulong logsTextChannelId,
            ulong logsFilesGuildId, ulong logsFilesChannelId)
        {
            Discord = discord;
            LogsRoot = logsRoot.EndsWith('/') ? logsRoot : (logsRoot + '/');
            FilesRoot = LogsRoot + "files/";
            LogPrefix = logPrefix;

            _debugGuildId = debugGuildId;
            _debugChannelId = debugChannelId;
            _logsTextGuildId = logsTextGuildId;
            _logsTextChannelId = logsTextChannelId;
            _logsFilesGuildId = logsFilesGuildId;
            _logsFilesChannelId = logsFilesChannelId;
        }
    }
}
