namespace MihuBot;

public sealed class LoggerOptions
{
    public readonly InitializedDiscordClient Discord;
    public readonly string LogsRoot, FilesRoot, LogPrefix;

    private readonly ulong _debugChannelId, _logsTextChannelId, _logsFilesChannelId;

    public SocketTextChannel DebugTextChannel => Discord.GetTextChannel(_debugChannelId);
    public SocketTextChannel LogsTextChannel => Discord.GetTextChannel(_logsTextChannelId);
    public SocketTextChannel LogsFilesTextChannel => Discord.GetTextChannel(_logsFilesChannelId);

    public Predicate<SocketUserMessage> ShouldLogAttachments { get; set; } = _ => true;

    public LoggerOptions(InitializedDiscordClient discord, string logsRoot, string logPrefix,
        ulong debugChannelId, ulong logsTextChannelId, ulong logsFilesChannelId)
    {
        Discord = discord;
        LogsRoot = logsRoot.EndsWith('/') ? logsRoot : (logsRoot + '/');
        FilesRoot = LogsRoot + "files/";
        LogPrefix = logPrefix;

        _debugChannelId = debugChannelId;
        _logsTextChannelId = logsTextChannelId;
        _logsFilesChannelId = logsFilesChannelId;
    }
}
