#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ValheimServerManager.Client;

// Discord's local IPC is available only in the player's desktop session. No Discord token is stored.
internal sealed class DiscordActivityClient : IDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _worker;
    private readonly string _pipePrefix;
    private Stream _activeStream;
    private ActivityState _desired;
    private bool _disposed;

    internal DiscordActivityClient(string pipePrefix = "discord-ipc-")
    {
        _pipePrefix = pipePrefix;
        _worker = new Thread(Run) { IsBackground = true, Name = "VSM Discord activity" };
        _worker.Start();
    }

    internal void SetActivity(string applicationId, string serverName, string worldName, int players, int maxPlayers,
        string region, string detailsTemplate, string stateTemplate, string imageUrl)
    {
        var next = applicationId != null && applicationId.Length is >= 17 and <= 20 && applicationId.All(character => character is >= '0' and <= '9')
            && !string.IsNullOrWhiteSpace(serverName)
            ? new ActivityState(applicationId, serverName, worldName, players, maxPlayers, region, detailsTemplate, stateTemplate, imageUrl)
            : null;
        lock (_sync)
        {
            if (_disposed || ActivityState.Same(_desired, next)) return;
            _desired = next;
        }
        _changed.Set();
    }

    private void Run()
    {
        Stream stream = null;
        string connectedId = null;
        ActivityState sent = null;
        DateTime lastSent = DateTime.MinValue;
        while (true)
        {
            ActivityState desired;
            lock (_sync)
            {
                if (_disposed) break;
                desired = _desired;
            }
            try
            {
                if (stream != null && (desired == null || connectedId != desired.ApplicationId))
                {
                    SendActivity(stream, null);
                    Close(stream);
                    stream = null;
                    connectedId = null;
                }
                if (desired == null)
                {
                    _changed.WaitOne(1000);
                    continue;
                }
                if (stream == null)
                {
                    stream = Connect();
                    if (stream == null) { _changed.WaitOne(5000); continue; }
                    lock (_sync)
                    {
                        if (_disposed) break;
                        _activeStream = stream;
                    }
                    SendFrame(stream, 0, new { v = 1, client_id = desired.ApplicationId });
                    var ready = ReadFrame(stream);
                    if (ready.Opcode != 1 || (string)ready.Body["evt"] != "READY") throw new IOException("Discord rejected the IPC handshake.");
                    connectedId = desired.ApplicationId;
                    sent = null;
                }
                if (!ActivityState.Same(sent, desired) || DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(60))
                {
                    SendActivity(stream, desired);
                    sent = desired;
                    lastSent = DateTime.UtcNow;
                }
                _changed.WaitOne(1000);
            }
            catch (Exception)
            {
                Close(stream);
                stream = null;
                connectedId = null;
                lock (_sync) if (_disposed) break;
                _changed.WaitOne(5000);
            }
        }
        if (stream != null)
        {
            try { SendActivity(stream, null); } catch { }
            Close(stream);
        }
    }

    private void Close(Stream stream)
    {
        stream?.Dispose();
        lock (_sync) if (ReferenceEquals(_activeStream, stream)) _activeStream = null;
    }

    private static void SendActivity(Stream stream, ActivityState activity)
    {
        var nonce = Guid.NewGuid().ToString("N");
        JObject presence = null;
        if (activity != null)
        {
            presence = new JObject
            {
                ["details"] = RenderTemplate(activity.DetailsTemplate, activity),
                ["state"] = RenderTemplate(activity.StateTemplate, activity)
            };
            if (!string.IsNullOrEmpty(activity.ImageUrl))
                presence["assets"] = JObject.FromObject(new { large_image = activity.ImageUrl, large_text = activity.ServerName });
        }
        SendFrame(stream, 1, new
        {
            cmd = "SET_ACTIVITY",
            nonce,
            args = new { pid = Process.GetCurrentProcess().Id, activity = presence }
        });
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var reply = ReadFrame(stream);
            if (reply.Opcode == 3) { SendFrame(stream, 4, reply.Body); continue; }
            if (reply.Opcode != 1) throw new IOException("Discord closed the IPC connection.");
            if ((string)reply.Body["nonce"] != nonce) continue;
            if ((string)reply.Body["evt"] == "ERROR") throw new IOException("Discord rejected the activity.");
            return;
        }
        throw new IOException("Discord did not acknowledge the activity.");
    }

    private static string RenderTemplate(string template, ActivityState activity)
    {
        var value = (template ?? "{server}")
            .Replace("{server}", activity.ServerName)
            .Replace("{world}", activity.WorldName)
            .Replace("{players}", activity.Players.ToString())
            .Replace("{maxPlayers}", activity.MaxPlayers.ToString())
            .Replace("{freeSlots}", Math.Max(0, activity.MaxPlayers - activity.Players).ToString())
            .Replace("{region}", string.IsNullOrWhiteSpace(activity.Region) ? "Exploring" : activity.Region)
            .Trim();
        if (value.Length > 128) value = value.Substring(0, 128);
        return value.Length < 2 ? "Valheim" : value;
    }

    private sealed class ActivityState
    {
        internal readonly string ApplicationId, ServerName, WorldName, Region, DetailsTemplate, StateTemplate, ImageUrl;
        internal readonly int Players, MaxPlayers;

        internal ActivityState(string applicationId, string serverName, string worldName, int players, int maxPlayers,
            string region, string detailsTemplate, string stateTemplate, string imageUrl)
        {
            ApplicationId = applicationId;
            ServerName = serverName;
            WorldName = worldName ?? "World";
            Players = Math.Max(0, players);
            MaxPlayers = Math.Max(1, maxPlayers);
            Region = region;
            DetailsTemplate = detailsTemplate ?? "{server}";
            StateTemplate = stateTemplate ?? "{region} · {players} players online";
            ImageUrl = Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && imageUrl.Length <= 300 ? imageUrl : null;
        }

        internal static bool Same(ActivityState left, ActivityState right) => ReferenceEquals(left, right) ||
            left != null && right != null && left.ApplicationId == right.ApplicationId && left.ServerName == right.ServerName
            && left.WorldName == right.WorldName && left.Players == right.Players && left.MaxPlayers == right.MaxPlayers
            && left.Region == right.Region && left.DetailsTemplate == right.DetailsTemplate
            && left.StateTemplate == right.StateTemplate && left.ImageUrl == right.ImageUrl;
    }

    private Stream Connect()
    {
        for (var index = 0; index < 10; index++)
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var pipe = new NamedPipeClientStream(".", _pipePrefix + index, PipeDirection.InOut);
                    try { pipe.Connect(100); return pipe; }
                    catch { pipe.Dispose(); throw; }
                }
                foreach (var directory in new[]
                {
                    Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
                    Environment.GetEnvironmentVariable("TMPDIR"),
                    Environment.GetEnvironmentVariable("TMP"),
                    Environment.GetEnvironmentVariable("TEMP"), "/tmp"
                }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                    { ReceiveTimeout = 3000, SendTimeout = 3000 };
                    try
                    {
                        socket.Connect(new UnixSocketPath(Path.Combine(directory, _pipePrefix + index)));
                        return new NetworkStream(socket, true);
                    }
                    catch { socket.Dispose(); }
                }
            }
            catch { }
        }
        return null;
    }

    private static void SendFrame(Stream stream, int opcode, object payload)
    {
        var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        var frame = new byte[8 + json.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, frame, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(json.Length), 0, frame, 4, 4);
        Buffer.BlockCopy(json, 0, frame, 8, json.Length);
        stream.Write(frame, 0, frame.Length);
    }

    private static (int Opcode, JObject Body) ReadFrame(Stream stream)
    {
        var header = new byte[8];
        ReadExact(stream, header);
        var length = BitConverter.ToInt32(header, 4);
        if (length < 0 || length > 65536) throw new InvalidDataException("Discord IPC frame is too large.");
        var payload = new byte[length];
        ReadExact(stream, payload);
        return (BitConverter.ToInt32(header, 0), JObject.Parse(Encoding.UTF8.GetString(payload)));
    }

    private static void ReadExact(Stream stream, byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public void Dispose()
    {
        Stream active;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            active = _activeStream;
        }
        active?.Dispose();
        _changed.Set();
        if (_worker.Join(3500)) _changed.Dispose();
    }

    private sealed class UnixSocketPath : EndPoint
    {
        private readonly string _path;
        internal UnixSocketPath(string path) => _path = path;
        public override AddressFamily AddressFamily => AddressFamily.Unix;
        public override SocketAddress Serialize()
        {
            var path = Encoding.UTF8.GetBytes(_path);
            if (path.Length >= 108) throw new ArgumentException("Discord IPC path is too long.");
            var address = new SocketAddress(AddressFamily.Unix, path.Length + 3);
            for (var index = 0; index < path.Length; index++) address[index + 2] = path[index];
            return address;
        }
    }
}
