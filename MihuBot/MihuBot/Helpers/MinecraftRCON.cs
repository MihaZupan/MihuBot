using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Helpers
{
    public class MinecraftRCON
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly SemaphoreSlim _asyncLock;
        private int _idCounter = new Random().Next(1_000_000, 10_000_000);
        private DateTime _lastCommandTime;
        private readonly Timer _cleanupTimer;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingRequests;

        public bool Invalid { get; private set; } = false;

        private MinecraftRCON(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = _tcp.GetStream();
            _asyncLock = new SemaphoreSlim(1, 1);
            _lastCommandTime = DateTime.UtcNow;

            _pendingRequests = new ConcurrentDictionary<int, TaskCompletionSource<string>>();

            _cleanupTimer = new Timer(s =>
            {
                _asyncLock.Wait();
                if (DateTime.UtcNow.Subtract(_lastCommandTime) > TimeSpan.FromMinutes(2))
                {
                    Cleanup();
                }
                _asyncLock.Release();
            }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

            Task.Run(async () =>
            {
                while (!Invalid)
                {
                    try
                    {
                        (int id, string response) = await ReceiveResponseAsync();

                        if (!_pendingRequests.TryRemove(id, out var tcs))
                            throw new Exception("Invalid response ID");

                        tcs.TrySetResult(response);
                    }
                    catch (Exception ex)
                    {
                        Cleanup(ex);
                    }
                }
            });
        }

        public async Task<string> SendCommandAsync(string command)
        {
            return await SendRawPacketAsync(command, packetType: 2);
        }

        private async Task<string> SendRawPacketAsync(string command, int packetType)
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
                if (Invalid)
                    throw new Exception("Invalid RCON");

                _lastCommandTime = DateTime.UtcNow;

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

        private async Task<(int, string)> ReceiveResponseAsync()
        {
            byte[] header = new byte[12];
            int read = 0;
            while (read < header.Length)
            {
                int newRead = await _stream.ReadAsync(header.AsMemory(read));
                if (newRead == 0)
                    throw new Exception("Connection closed");

                read += newRead;
            }

            int length = BitConverter.ToInt32(header) - 8;

            if (length < 2 || length > 16 * 1024)
                throw new Exception("Invalid reponse length");

            byte[] response = new byte[length];
            read = 0;
            while (read < response.Length)
            {
                int newRead = await _stream.ReadAsync(response.AsMemory(read));
                if (newRead == 0)
                    throw new Exception("Connection closed");

                read += newRead;
            }

            return (BitConverter.ToInt32(header.AsSpan(4)), Encoding.ASCII.GetString(response.AsSpan(0, response.Length - 2)));
        }

        private void Cleanup(Exception ex = null)
        {
            Invalid = true;
            try
            {
                _tcp.Close();
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
        }

        public static async Task<MinecraftRCON> ConnectAsync(string hostname, string password, int port = 25575)
        {
            TcpClient tcp = new TcpClient();

            tcp.SendTimeout = Math.Min(tcp.SendTimeout, 10_000);
            tcp.ReceiveTimeout = Math.Min(tcp.ReceiveTimeout, 10_000);

            await tcp.ConnectAsync(hostname, port);

            var rcon = new MinecraftRCON(tcp);

            await rcon.SendRawPacketAsync(password, packetType: 3);

            return rcon;
        }
    }
}
