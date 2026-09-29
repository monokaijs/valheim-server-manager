using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed partial class ManagerRoleService(ManagerDbContext db, AccessListService access)
{
    public const string Admin = "admin";
    public const string Mod = "mod";

    [GeneratedRegex("^[0-9]{17}$")]
    private static partial Regex SteamIdPattern();

    public static bool ValidSteamId(string steamId) => SteamIdPattern().IsMatch(steamId);

    public async Task<string?> GetRole(string steamId, CancellationToken ct = default)
    {
        if (!ValidSteamId(steamId)) return null;
        var assigned = await db.ManagerUserRoles.AsNoTracking().FirstOrDefaultAsync(x => x.SteamId == steamId, ct);
        if (assigned is not null) return assigned.Role;
        return await access.IsSteamAdmin(steamId) ? Admin : null;
    }

    public async Task<IReadOnlyList<ManagerUserRole>> List(CancellationToken ct = default)
    {
        var assigned = await db.ManagerUserRoles.AsNoTracking().ToListAsync(ct);
        var result = assigned.ToDictionary(x => x.SteamId, StringComparer.Ordinal);
        foreach (var entry in await access.Read("admin"))
        {
            var steamId = entry.StartsWith("Steam_", StringComparison.Ordinal) ? entry[6..] : entry;
            if (ValidSteamId(steamId) && !result.ContainsKey(steamId))
                result[steamId] = new ManagerUserRole { SteamId = steamId, Role = Admin };
        }
        return result.Values.OrderBy(x => x.SteamId, StringComparer.Ordinal).ToArray();
    }

    public async Task SetRole(string steamId, string role, CancellationToken ct = default)
    {
        if (!ValidSteamId(steamId)) throw new ArgumentException("Use a 17-digit Steam ID.");
        if (role is not (Admin or Mod)) throw new ArgumentException("Role must be admin or mod.");
        var assigned = await db.ManagerUserRoles.FindAsync([steamId], ct);
        if (assigned is null)
            db.ManagerUserRoles.Add(new ManagerUserRole { SteamId = steamId, Role = role });
        else
        {
            assigned.Role = role;
            assigned.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
