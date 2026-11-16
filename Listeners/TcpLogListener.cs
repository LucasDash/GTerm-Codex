using System.Drawing;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace GTerm.Listeners
{
    // Connects to the module using the configured local transport. TCP remains
    // the compatibility default; the Windows named-pipe mode also checks the
    // pipe server PID against the selected GMod installation before sending commands.
    internal class TcpLogListener : ILogListener
    {
        private const string HOST = "127.0.0.1";
        private const int RECONNECT_DELAY_MS = 1000;
        private const int MAX_COMMAND_FRAME_BYTES = 1024 * 1024;
        private const uint MAX_LOG_FRAME_BYTES = 8 * 1024 * 1024;
        private readonly int Port;
        private readonly ModuleTransportKind Transport;
        private readonly Func<uint, bool> IsExpectedPipeServerProcess;
        private readonly SemaphoreSlim WriteLock = new(1, 1);

        internal TcpLogListener(int port = 27514)
            : this(port, ModuleTransportKind.Tcp, null)
        {
        }

        internal TcpLogListener(int port, ModuleTransportKind transport, Func<uint, bool>? isExpectedPipeServerProcess)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (transport == ModuleTransportKind.WindowsNamedPipe && !OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The Windows named-pipe module transport is available only on Windows.");
            this.Port = port;
            this.Transport = transport;
            this.IsExpectedPipeServerProcess = isExpectedPipeServerProcess ?? (_ => false);
        }

        private TcpClient? Client;
        private NamedPipeClientStream? PipeClient;
        private Stream? Stream;

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event ErrorEventHandler? OnError;
        public event LogEventHandler? OnLog;

        public bool IsConnected { get; private set; } = false;

        private void SetConnectionStatus(bool connected)
        {
            if (connected == this.IsConnected) return;

            this.IsConnected = connected;
            if (connected)
                OnConnected?.Invoke(this, EventArgs.Empty);
            else
                OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        private static string ReadString(BinaryReader reader)
        {
            List<byte> list = [];
            byte ch;
            while ((ch = reader.ReadByte()) != 0)
                list.Add(ch);

            return Encoding.UTF8.GetString(list.ToArray());
        }

        private void ProcessMessage(byte[] payload)
        {
            using BinaryReader reader = new(new MemoryStream(payload, 0, payload.Length));

            int type = reader.ReadInt32();
            int level = reader.ReadInt32();
            string group = ReadString(reader);
            int r = reader.ReadByte();
            int g = reader.ReadByte();
            int b = reader.ReadByte();
            int a = reader.ReadByte();

            Color color = Color.FromArgb(a, r, g, b);
            string fullMsg = ReadString(reader);

            OnLog?.Invoke(this, new LogEventArgs(type, level, group, color, fullMsg));
        }

        private async Task ProcessMessages()
        {
            byte[] header = new byte[4];

            while (true)
            {
                try
                {
                    if (this.Transport == ModuleTransportKind.WindowsNamedPipe)
                    {
                        this.PipeClient = new NamedPipeClientStream(".", $"GTerm.Module.{this.Port}", PipeDirection.InOut, PipeOptions.Asynchronous);
                        await this.PipeClient.ConnectAsync(1000);
                        if (!GetNamedPipeServerProcessId(this.PipeClient.SafePipeHandle, out uint serverPid))
                            throw new IOException($"Could not verify the named-pipe server process (Win32 error {Marshal.GetLastWin32Error()}).");
                        if (!this.IsExpectedPipeServerProcess(serverPid))
                            throw new UnauthorizedAccessException("The named-pipe server does not match the selected Garry's Mod process; connection rejected.");
                        this.Stream = this.PipeClient;
                    }
                    else
                    {
                        this.Client = new TcpClient();
                        await this.Client.ConnectAsync(HOST, this.Port);
                        this.Stream = this.Client.GetStream();
                    }
                    this.SetConnectionStatus(true);

                    while (true)
                    {
                        await this.Stream.ReadExactlyAsync(header.AsMemory(0, 4));
                        uint length = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
                        if (length == 0) continue;
                        if (length > MAX_LOG_FRAME_BYTES)
                            throw new InvalidDataException("GTerm module log frame exceeds the maximum size.");

                        byte[] payload = new byte[length];
                        await this.Stream.ReadExactlyAsync(payload.AsMemory(0, (int)length));

                        this.ProcessMessage(payload);
                    }
                }
                catch (EndOfStreamException) { } // remote closed the connection
                // gmod isn't running yet (connect refused) or the module dropped
                // the socket: both are normal while waiting for/between sessions,
                // so retry silently instead of spamming the console.
                catch (SocketException) { }
                catch (IOException) { }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, new ErrorEventArgs(ex));
                }
                finally
                {
                    this.SetConnectionStatus(false);
                    this.Stream?.Dispose();
                    this.Client?.Dispose();
                    this.PipeClient?.Dispose();
                    this.Stream = null;
                    this.Client = null;
                    this.PipeClient = null;
                }

                await Task.Delay(RECONNECT_DELAY_MS);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        public void Start() => Task.Run(this.ProcessMessages);

        public async Task WriteMessage(string input)
        {
            await this.WriteLock.WaitAsync();
            try
            {
                Stream? stream = this.Stream;
                if (stream == null || !this.IsConnected) return;

                int encodedLength = Encoding.UTF8.GetByteCount(input);
                int nullCount = 0;
                foreach (char character in input)
                    if (character == '\0') nullCount++;
                if (encodedLength - nullCount > MAX_COMMAND_FRAME_BYTES)
                    throw new InvalidDataException($"GTerm module command frame exceeds the {MAX_COMMAND_FRAME_BYTES}-byte maximum.");

                input = input.Replace("\0", "");
                byte[] body = Encoding.UTF8.GetBytes(input);
                byte[] frame = new byte[4 + body.Length];
                uint length = (uint)body.Length;
                frame[0] = (byte)(length & 0xFF);
                frame[1] = (byte)((length >> 8) & 0xFF);
                frame[2] = (byte)((length >> 16) & 0xFF);
                frame[3] = (byte)((length >> 24) & 0xFF);
                Buffer.BlockCopy(body, 0, frame, 4, body.Length);

                await stream.WriteAsync(frame);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new ErrorEventArgs(ex));
            }
            finally
            {
                this.WriteLock.Release();
            }
        }
    }

    internal enum ModuleTransportKind
    {
        Tcp,
        WindowsNamedPipe,
    }
}
