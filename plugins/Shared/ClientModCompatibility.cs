using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

#nullable enable

namespace ValheimServerManager.ClientSupport;

internal sealed class ClientModPackage
{
    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? InstalledVersion { get; set; }
    public bool Present => string.Equals(Version, InstalledVersion, StringComparison.Ordinal);
}

internal sealed class ClientModGroup
{
    public string Name { get; set; } = "";
    public IReadOnlyList<ClientModPackage> Packages { get; set; } = Array.Empty<ClientModPackage>();
}

internal sealed class ClientModCatalogStatus
{
    public string Revision { get; set; } = "";
    public bool RequiredReceipt { get; set; }
    public IReadOnlyList<ClientModPackage> Required { get; set; } = Array.Empty<ClientModPackage>();
    public IReadOnlyList<ClientModGroup> Optional { get; set; } = Array.Empty<ClientModGroup>();
    public IReadOnlyList<string> Unexpected { get; set; } = Array.Empty<string>();
    public bool RequiredPresent => Required.All(package => package.Present);
    public bool CanAcknowledge => RequiredPresent && Unexpected.Count == 0;
}

/// <summary>Reads package metadata from the active BepInEx profile without changing it.</summary>
internal static class ClientModCompatibility
{
    public static ClientModCatalogStatus Check(string json, string bepinexRoot)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Invalid server mod list size.");
        var catalog = JObject.Parse(json);
        if ((int?)catalog["schemaVersion"] != 2)
            throw new InvalidDataException("Unsupported server mod list.");
        var revision = (string?)catalog["revision"];
        if (revision == null || revision.Length != 64 || !revision.All(IsHex))
            throw new InvalidDataException("Invalid server mod list revision.");
        var required = ReadPackages(catalog["packages"], bepinexRoot);
        var groups = new List<ClientModGroup>();
        var optional = catalog["optionalGroups"] as JArray;
        if (optional != null)
        {
            if (optional.Count > 100) throw new InvalidDataException("Too many optional mod groups.");
            foreach (var group in optional)
            {
                var name = (string?)group["name"];
                if (name == null || name.Length > 200 || string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException("Invalid optional mod group.");
                groups.Add(new ClientModGroup { Name = name, Packages = ReadPackages(group["packages"], bepinexRoot) });
            }
        }
        var allowed = required.Concat(groups.SelectMany(group => group.Packages))
            .GroupBy(package => package.Namespace + "-" + package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new ClientModCatalogStatus
        {
            Revision = revision,
            RequiredReceipt = (bool?)catalog["requiredReceipt"] == true,
            Required = required,
            Optional = groups,
            Unexpected = UnexpectedPackages(bepinexRoot, allowed)
        };
    }

    private static IReadOnlyList<string> UnexpectedPackages(string bepinexRoot, IReadOnlyDictionary<string, ClientModPackage[]> allowed)
    {
        var unexpected = new List<string>();
        foreach (var category in new[] { "plugins", "patchers" })
        {
            var root = Path.Combine(bepinexRoot, category);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateDirectories(root))
            {
                var folder = Path.GetFileName(path);
                if (category == "plugins" && (folder.Equals("ServerManager", StringComparison.OrdinalIgnoreCase)
                    || folder.Equals("Creaton-Server_Manager", StringComparison.OrdinalIgnoreCase))) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    unexpected.Add(folder + " (linked folder)");
                    continue;
                }
                var manifest = Path.Combine(path, "manifest.json");
                if (File.Exists(manifest))
                {
                    allowed.TryGetValue(folder, out var packages);
                    var version = ReadVersion(manifest, packages?.FirstOrDefault()?.Name);
                    if (packages == null || !packages.Any(package => package.Version == version))
                        unexpected.Add(folder + (version == null ? " (unreadable manifest)" : "-" + version));
                }
                else if (Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Any())
                    unexpected.Add(folder + (allowed.ContainsKey(folder) ? " (unverified plugin)" : " (unlisted plugin)"));
            }
            foreach (var file in Directory.EnumerateFiles(root, "*.dll"))
                unexpected.Add(Path.GetFileName(file) + " (unlisted plugin)");
        }
        return unexpected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<ClientModPackage> ReadPackages(JToken? token, string bepinexRoot)
    {
        if (token is not JArray entries || entries.Count > 500)
            throw new InvalidDataException("Invalid server mod packages.");
        var packages = new List<ClientModPackage>();
        foreach (var entry in entries)
        {
            var packageNamespace = (string?)entry["namespace"];
            var name = (string?)entry["packageName"];
            var version = (string?)entry["versionNumber"];
            if (!SafeSegment(packageNamespace) || !SafeSegment(name) || !SafeVersion(version)
                || (string?)entry["coordinate"] != packageNamespace + "-" + name + "-" + version)
                throw new InvalidDataException("Invalid package identity in server mod list.");
            packages.Add(new ClientModPackage
            {
                Namespace = packageNamespace!,
                Name = name!,
                Version = version!,
                InstalledVersion = InstalledVersion(bepinexRoot, packageNamespace!, name!)
            });
        }
        return packages;
    }

    private static string? InstalledVersion(string bepinexRoot, string packageNamespace, string name)
    {
        // r2modman places package metadata in BepInEx/plugins/Author-ModName/manifest.json.
        var path = Path.Combine(bepinexRoot, "plugins", packageNamespace + "-" + name, "manifest.json");
        try
        {
            return ReadVersion(path, name);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ReadVersion(string path, string? expectedName)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
        var manifest = JObject.Parse(File.ReadAllText(path));
        if (expectedName != null && !string.Equals((string?)manifest["name"], expectedName, StringComparison.OrdinalIgnoreCase)) return null;
        return (string?)manifest["version_number"];
    }

    private static bool SafeSegment(string? value) => value != null && value.Length > 0 && value.Length <= 100
        && value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-');
    private static bool SafeVersion(string? value) => value != null && value.Length > 0 && value.Length <= 32
        && value.All(character => char.IsDigit(character) || character == '.');
    private static bool IsHex(char value) => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
}
