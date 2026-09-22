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
    public bool RequiredPresent => Required.All(package => package.Present);
}

/// <summary>Reads package metadata from the active BepInEx profile without changing it.</summary>
internal static class ClientModCompatibility
{
    public static ClientModCatalogStatus Check(string json, string bepinexRoot)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Invalid server mod list size.");
        var catalog = JObject.Parse(json);
        if ((int?)catalog["schemaVersion"] != 1)
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
        return new ClientModCatalogStatus
        {
            Revision = revision,
            RequiredReceipt = (bool?)catalog["requiredReceipt"] == true,
            Required = required,
            Optional = groups
        };
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
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            var manifest = JObject.Parse(File.ReadAllText(path));
            if (!string.Equals((string?)manifest["name"], name, StringComparison.OrdinalIgnoreCase)) return null;
            return (string?)manifest["version_number"];
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static bool SafeSegment(string? value) => value != null && value.Length > 0 && value.Length <= 100
        && value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-');
    private static bool SafeVersion(string? value) => value != null && value.Length > 0 && value.Length <= 32
        && value.All(character => char.IsDigit(character) || character == '.');
    private static bool IsHex(char value) => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
}
