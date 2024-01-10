
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace MihuBot.Audio;

internal sealed class ReadAheadStream : Stream
{
    private readonly Stream _innerStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Memory<byte>> _bufferChannel = Channel.CreateUnbounded<Memory<byte>>(new UnboundedChannelOptions
    {
        SingleWriter = true
    });
    private Memory<byte> _leftoverBuffer;
    private int _leftoverOffset;

    public ReadAheadStream(Stream innerStream)
    {
        _innerStream = innerStream;

        using (ExecutionContext.SuppressFlow())
        {
            Task.Run(ReadLoopAsync);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            int read = 4096;

            while (!_cts.IsCancellationRequested)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(read * 1.1d, 16, 64 * 1024));

                read = await _innerStream.ReadAsync(buffer, _cts.Token);

                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                _bufferChannel.Writer.TryWrite(buffer.AsMemory(0, read));
            }
        }
        catch { }
        finally
        {
            _bufferChannel.Writer.TryComplete();
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_leftoverBuffer.IsEmpty)
        {
            if (!await _bufferChannel.Reader.WaitToReadAsync(cancellationToken) ||
                !_bufferChannel.Reader.TryRead(out _leftoverBuffer))
            {
                return 0;
            }
        }

        int toCopy = Math.Min(buffer.Length, _leftoverBuffer.Length - _leftoverOffset);
        _leftoverBuffer.Span.Slice(_leftoverOffset, toCopy).CopyTo(buffer.Span);
        _leftoverOffset += toCopy;

        if (_leftoverOffset == _leftoverBuffer.Length)
        {
            bool success = MemoryMarshal.TryGetArray(_leftoverBuffer, out ArraySegment<byte> segment);
            Debug.Assert(success);

            _leftoverBuffer = default;
            _leftoverOffset = 0;

            ArrayPool<byte>.Shared.Return(segment.Array);
        }

        return toCopy;
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public async override ValueTask DisposeAsync()
    {
        _bufferChannel.Writer.TryComplete();

        _cts.Cancel();

        await base.DisposeAsync();

        await _innerStream.DisposeAsync();

        while (_bufferChannel.Reader.TryRead(out var buffer))
        {
            bool success = MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment);
            Debug.Assert(success);

            ArrayPool<byte>.Shared.Return(segment.Array);
        }
    }
}
