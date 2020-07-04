using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public class MinecraftRCON
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly SemaphoreSlim _asyncLock;
        private int _idCounter = new Random().Next(1_000_000, 10_000_000);
        private bool _invalid = false;

        private MinecraftRCON(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = _tcp.GetStream();
            _asyncLock = new SemaphoreSlim(1, 1);
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

            await _asyncLock.WaitAsync();

            try
            {
                if (_invalid)
                    throw new Exception("Invalid RCON");

                await _stream.WriteAsync(packet);

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

                if (BitConverter.ToInt32(header.AsSpan(4)) != id)
                    throw new Exception("Invalid response ID");

                string responseString = Encoding.ASCII.GetString(response.AsSpan(0, response.Length - 2));
                return responseString;
            }
            catch
            {
                _invalid = true;
                throw;
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        public static async Task<MinecraftRCON> ConnectAsync(string hostname, string password, int port = 25575)
        {
            TcpClient tcp = new TcpClient();

            await tcp.ConnectAsync(hostname, port);

            var rcon = new MinecraftRCON(tcp);

            await rcon.SendRawPacketAsync(password, packetType: 3);

            return rcon;
        }
    }
}
