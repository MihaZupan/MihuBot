using System.IO.Pipelines;
using System.Runtime.ExceptionServices;

namespace MihuBot.Audio;

internal sealed class ReadAheadStream : Stream
{
    private readonly Stream _innerStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Pipe _pipe;
    private readonly Stream _pipeReaderStream;
    private int _pipeClosed = 0;

    public ReadAheadStream(Stream innerStream, long? bufferCapacity = null)
    {
        _innerStream = innerStream;

        _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: bufferCapacity ?? 0));
        _pipeReaderStream = _pipe.Reader.AsStream(leaveOpen: false);

        using (ExecutionContext.SuppressFlow())
        {
            Task.Run(CopyStreamToPipeAsync);
        }
    }

    private async Task CopyStreamToPipeAsync()
    {
        try
        {
            await _innerStream.CopyToAsync(_pipe.Writer, _cts.Token);

            if (Interlocked.Exchange(ref _pipeClosed, 1) == 0)
            {
                await _pipe.Writer.CompleteAsync();
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _pipeClosed, 1) == 0)
            {
                await _pipe.Writer.CompleteAsync(ex);
            }
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

    public async override ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _pipeClosed, 1) == 0)
        {
            await _pipe.Writer.CompleteAsync(ExceptionDispatchInfo.SetCurrentStackTrace(new ObjectDisposedException(nameof(ReadAheadStream))));
        }

        _cts.Cancel();

        await base.DisposeAsync();

        await _innerStream.DisposeAsync();
        await _pipeReaderStream.DisposeAsync();
    }
}
