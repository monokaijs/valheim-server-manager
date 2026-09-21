using System.Text.Json;
using System.Text;

namespace ValheimServerManager.Services;

public sealed class SafeConsoleService(AgentGateway agent, ProcessSupervisor supervisor, ServerState state, AccessListService access, AuditService audit)
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase) { "help", "status", "players", "save", "broadcast", "kick", "ban", "unban", "whitelist", "mods", "restart" };

    public async Task<object> Execute(string input, CancellationToken cancellationToken = default)
    {
        var parts = Tokenize(input);
        if (parts.Count == 0) throw new ArgumentException("Command is empty.");
        var command = parts[0].ToLowerInvariant();
        if (!IsAllowedCommand(command)) throw new ArgumentException("Command is not in the safe-command allowlist.");
        object result;
        switch (command)
        {
            case "help": result = new { message = "help, status, players, save, broadcast <text>, kick <peer>, ban <id>, unban <id>, whitelist list|add|remove <id>, mods list, restart [seconds]" }; break;
            case "status": result = state.Snapshot(); break;
            case "players": result = state.Players; break;
            case "save": result = await Agent("world.save", new { }); break;
            case "broadcast": Require(parts, 2); result = await Agent("broadcast", new { message = string.Join(' ', parts.Skip(1)) }); break;
            case "kick": Require(parts, 2); result = await Agent("player.kick", new { target = parts[1] }); break;
            case "ban": Require(parts, 2); await access.Add("banned", parts[1]); result = new { message = $"Banned {parts[1]}" }; break;
            case "unban": Require(parts, 2); await access.Remove("banned", parts[1]); result = new { message = $"Unbanned {parts[1]}" }; break;
            case "whitelist":
                Require(parts, 2);
                if (parts[1] == "list") result = await access.Read("permitted");
                else { Require(parts, 3); if (parts[1] == "add") await access.Add("permitted", parts[2]); else if (parts[1] == "remove") await access.Remove("permitted", parts[2]); else throw new ArgumentException("Use whitelist list, add, or remove."); result = new { message = "Whitelist updated." }; }
                break;
            case "mods": result = new { message = "Use the Mods page for package operations." }; break;
            case "restart":
                var seconds = parts.Count > 1 && int.TryParse(parts[1], out var value) ? Math.Clamp(value, 0, 3600) : 0;
                _ = Task.Run(async () =>
                {
                    if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds));
                    if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60));
                    await supervisor.Restart(TimeSpan.Zero);
                });
                result = new { message = $"Restart scheduled in {seconds} seconds." };
                break;
            default: throw new ArgumentException("Command is not in the safe-command allowlist.");
        }
        await audit.Write("console.command", command, detail: input);
        return result;
    }

    private async Task<object> Agent(string command, object data)
    {
        var result = await agent.Command(command, data, TimeSpan.FromSeconds(15));
        return JsonSerializer.Deserialize<object>(result.GetRawText()) ?? new { };
    }

    private static void Require(IReadOnlyCollection<string> parts, int count) { if (parts.Count < count) throw new ArgumentException("Missing command argument."); }
    internal static List<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in input)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); } }
            else current.Append(c);
        }
        if (quoted) throw new ArgumentException("Unterminated quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
    internal static bool IsAllowedCommand(string command) => AllowedCommands.Contains(command);
}
