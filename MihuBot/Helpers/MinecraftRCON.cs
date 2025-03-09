using System.Collections.Concurrent;
using System.Net.Sockets;

namespace MihuBot.Helpers;

#nullable enable

public sealed class MinecraftRCON
{
    private readonly string _hostname;
    private readonly int _port;
    private readonly string _password;
    private readonly SemaphoreSlim _connectionLock;
    private RconConnection? _currentConnection;

    public MinecraftRCON(string hostname, int port, string password)
    {
        _hostname = hostname;
        _port = port;
        _password = password;
        _connectionLock = new SemaphoreSlim(1, 1);
    }

    public async Task<string> SendCommandAsync(string command)
    {
        Exception? lastEx = null;

        for (int retry = 0; retry < 3; retry++)
        {
            RconConnection? connection = _currentConnection;
            try
            {
                if (connection is null)
                {
                    await _connectionLock.WaitAsync();
                    try
                    {
                        connection = _currentConnection ??= await RconConnection.ConnectAsync(_hostname, _port, _password);
                    }
                    finally
                    {
                        _connectionLock.Release();
                    }
                }

                return await connection.SendRawPacketAsync(command, packetType: 2);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _currentConnection, null, connection);
                lastEx = ex;
            }
        }

        throw lastEx ?? new Exception("RCON failure");
    }

    private sealed class RconConnection
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _asyncLock;
        private readonly Timer _cleanupTimer;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingRequests;

        private int _idCounter;
        private DateTime _lastActiveTime;
        private bool _invalid;

        private RconConnection(Stream stream)
        {
            _stream = stream;
            _asyncLock = new SemaphoreSlim(1, 1);
            _lastActiveTime = DateTime.UtcNow;

            _pendingRequests = new ConcurrentDictionary<int, TaskCompletionSource<string>>();

            _cleanupTimer = new Timer(s =>
            {
                if (DateTime.UtcNow.Subtract(_lastActiveTime) > TimeSpan.FromSeconds(20))
                {
                    Cleanup();
                }
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

            Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];

                while (!_invalid)
                {
                    try
                    {
                        Memory<byte> header = buffer.AsMemory(0, 12);

                        int read = 0;
                        while (read < header.Length)
                        {
                            int newRead = await _stream.ReadAsync(header.Slice(read));
                            if (newRead == 0)
                                throw new Exception("Connection closed");

                            read += newRead;
                        }

                        int length = BitConverter.ToInt32(header.Span) - 8;

                        if (length is < 2 or > (16 * 1024))
                            throw new Exception("Invalid reponse length");

                        int id = BitConverter.ToInt32(header.Span.Slice(4));

                        Memory<byte> response = buffer.AsMemory(0, length);
                        read = 0;
                        while (read < response.Length)
                        {
                            int newRead = await _stream.ReadAsync(response.Slice(read));
                            if (newRead == 0)
                                throw new Exception("Connection closed");

                            read += newRead;
                        }

                        string responseString = Encoding.ASCII.GetString(response.Span.Slice(0, response.Length - 2));

                        if (!_pendingRequests.TryRemove(id, out var tcs))
                            throw new Exception("Invalid response ID");

                        tcs.TrySetResult(responseString);

                        _lastActiveTime = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        Cleanup(ex);
                    }
                }
            });
        }

        public async Task<string> SendRawPacketAsync(string command, int packetType)
        {
            int id = Interlocked.Increment(ref _idCounter);
            byte[] packet = new byte[14 + command.Length];
            BitConverter.TryWriteBytes(packet, 10 + command.Length);
            BitConverter.TryWriteBytes(packet.AsSpan(4), id);
            BitConverter.TryWriteBytes(packet.AsSpan(8), packetType);
            Encoding.ASCII.GetBytes(command, packet.AsSpan(12));

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests.TryAdd(id, tcs);

            await _asyncLock.WaitAsync();

            try
            {
                if (_invalid)
                    throw new Exception("Invalid RCON");

                _lastActiveTime = DateTime.UtcNow;

                await _stream.WriteAsync(packet);
            }
            catch (Exception ex)
            {
                Cleanup(ex);
                throw;
            }
            finally
            {
                _asyncLock.Release();
            }

            return await tcs.Task;
        }

        private void Cleanup(Exception? ex = null)
        {
            _invalid = true;
            try
            {
                _stream.Dispose();
            }
            catch { }
            try
            {
                _cleanupTimer.Dispose();
            }
            catch { }
            try
            {
                ex ??= new Exception("RCON failure");

                foreach (var tcs in _pendingRequests.Values)
                    tcs.TrySetException(ex);
            }
            catch { }
            try
            {
                _cleanupTimer?.Dispose();
            }
            catch { }
        }

        public static async Task<RconConnection> ConnectAsync(string hostname, int port, string password)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var ctr = connectTimeout.Token.Register(s => ((Socket)s!).Dispose(), socket);

            try
            {
                await socket.ConnectAsync(hostname, port);

                var connection = new RconConnection(new NetworkStream(socket, ownsSocket: true));

                await connection.SendRawPacketAsync(password, packetType: 3);

                return connection;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}