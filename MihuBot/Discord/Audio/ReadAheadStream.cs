using System.IO.Pipelines;

namespace MihuBot.Discord.Audio;

internal sealed class ReadAheadStream : Stream
{
    private readonly Stream _innerStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Pipe _pipe;
    private readonly Stream _pipeReaderStream;
    private readonly Task _copyTask;
    private readonly Lock _disposeLock = new();
    private Task _disposeTask;

    public ReadAheadStream(Stream innerStream, long? bufferCapacity = null)
    {
        _innerStream = innerStream;

        _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: bufferCapacity ?? 0));
        _pipeReaderStream = _pipe.Reader.AsStream(leaveOpen: false);

        using (ExecutionContext.SuppressFlow())
        {
            _copyTask = Task.Run(CopyStreamToPipeAsync);
        }
    }

    private async Task CopyStreamToPipeAsync()
    {
        Exception error = null;

        try
        {
            await _innerStream.CopyToAsync(_pipe.Writer, _cts.Token);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            await _pipe.Writer.CompleteAsync(error);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _pipeReaderStream.ReadAsync(buffer, cancellationToken);

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var cleanup = new AsyncCleanup();
        cleanup.Run(_cts.Cancel);
        cleanup.Run(_pipe.Writer.CancelPendingFlush);
        cleanup.Run(_pipe.Reader.CancelPendingRead);
        await cleanup.RunAsync(() => _innerStream.DisposeAsync().AsTask());
        await cleanup.RunAsync(() => _copyTask);
        await cleanup.RunAsync(() => _pipeReaderStream.DisposeAsync().AsTask());
        cleanup.Run(_cts.Dispose);
        cleanup.ThrowIfFailed();
    }
}
