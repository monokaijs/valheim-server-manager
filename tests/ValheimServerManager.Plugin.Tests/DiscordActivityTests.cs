#nullable disable
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimServerManager.Client;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class DiscordActivityTests
{
    [Fact]
    public async Task PublishesServerActivityAndClearsItOnOptOut()
    {
        var name = "vsm-discord-test-" + Guid.NewGuid().ToString("N")[..12] + "-";
        if (OperatingSystem.IsWindows())
        {
            using var pipe = new NamedPipeServerStream(name + "0", PipeDirection.InOut, 1);
            await Exercise(name, () => { pipe.WaitForConnection(); return pipe; });
        }
        else
        {
            var prefix = Path.Combine(Path.GetTempPath(), name);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(prefix + "0"));
            socket.Listen(1);
            try { await Exercise(prefix, () => new NetworkStream(socket.Accept(), true)); }
            finally { File.Delete(prefix + "0"); }
        }
    }

    private static async Task Exercise(string prefix, Func<Stream> accept)
    {
        using var client = new DiscordActivityClient(prefix);
        client.SetActivity("123456789012345678", "My Valheim Realm", "Dedicated", 3, 10, "Meadows",
            "{server} | {world}", "{region} · {players}/{maxPlayers} ({freeSlots} free)", "https://example.com/valheim.png");

        var exchange = Task.Run(() =>
        {
            using var stream = accept();
            var handshake = Read(stream);
            Assert.Equal(0, handshake.Opcode);
            Assert.Equal("123456789012345678", (string)handshake.Body["client_id"]!);
            Write(stream, 1, new { cmd = "DISPATCH", evt = "READY", data = new { } });

            var activity = Read(stream);
            Assert.Equal(1, activity.Opcode);
            Assert.Equal("SET_ACTIVITY", (string)activity.Body["cmd"]!);
            Assert.Equal("My Valheim Realm | Dedicated", (string)activity.Body["args"]!["activity"]!["details"]!);
            Assert.Equal("Meadows · 3/10 (7 free)", (string)activity.Body["args"]!["activity"]!["state"]!);
            Assert.Equal("https://example.com/valheim.png", (string)activity.Body["args"]!["activity"]!["assets"]!["large_image"]!);
            Write(stream, 1, new { cmd = "SET_ACTIVITY", nonce = (string)activity.Body["nonce"]! });

            client.SetActivity("123456789012345678", "My Valheim Realm", "Dedicated", 3, 10, "Black Forest",
                "{server} | {world}", "{region} · {players}/{maxPlayers} ({freeSlots} free)", "https://example.com/valheim.png");
            var regionUpdate = Read(stream);
            Assert.Equal("Black Forest · 3/10 (7 free)", (string)regionUpdate.Body["args"]!["activity"]!["state"]!);
            Write(stream, 1, new { cmd = "SET_ACTIVITY", nonce = (string)regionUpdate.Body["nonce"]! });

            client.SetActivity(null, null, null, 0, 0, null, null, null, null);
            var clear = Read(stream);
            Assert.Equal(JTokenType.Null, clear.Body["args"]!["activity"]!.Type);
            Write(stream, 1, new { cmd = "SET_ACTIVITY", nonce = (string)clear.Body["nonce"]! });
        });
        await exchange.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static (int Opcode, JObject Body) Read(Stream stream)
    {
        var header = ReadBytes(stream, 8);
        var length = BitConverter.ToInt32(header, 4);
        return (BitConverter.ToInt32(header, 0), JObject.Parse(Encoding.UTF8.GetString(ReadBytes(stream, length))));
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var bytes = new byte[count];
        for (var offset = 0; offset < count;)
        {
            var read = stream.Read(bytes, offset, count - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return bytes;
    }

    private static void Write(Stream stream, int opcode, object payload)
    {
        var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        var frame = new byte[json.Length + 8];
        Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, frame, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(json.Length), 0, frame, 4, 4);
        Buffer.BlockCopy(json, 0, frame, 8, json.Length);
        stream.Write(frame);
    }
}
