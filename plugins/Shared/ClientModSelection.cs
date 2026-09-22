using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using ValheimServerManager.Bootstrap;

namespace ValheimServerManager.ClientSupport;

// Linked into the runtime updater, not the stable preloader. The effective payload
// remains a schema-v1 BootstrapManifest, so existing preloader installations work.
[DataContract]
internal sealed class ClientModCatalog
{
    [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; }
    [DataMember(Name = "manifestId")] public string ManifestId { get; set; } = "";
    [DataMember(Name = "serverId")] public string ServerId { get; set; } = "";
    [DataMember(Name = "revision", IsRequired = true)] public string Revision { get; set; } = "";
    [DataMember(Name = "generatedAt", IsRequired = true)] public string GeneratedAt { get; set; } = "";
    [DataMember(Name = "packages", IsRequired = true)] public List<ManifestPackage> Packages { get; set; } = new();
    [DataMember(Name = "configs", IsRequired = true)] public List<ManifestConfig> Configs { get; set; } = new();
    [DataMember(Name = "optionalGroups")] public List<OptionalModChoice> OptionalGroups { get; set; } = new();
    [DataMember(Name = "requiredReceipt")] public bool RequiredReceipt { get; set; }
}

[DataContract]
internal sealed class OptionalModChoice
{
    [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; } = "";
    [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; } = "";
    [DataMember(Name = "packages", IsRequired = true)] public List<ManifestPackage> Packages { get; set; } = new();
}

[DataContract]
internal sealed class ClientModPreferences
{
    [DataMember(Name = "selected", IsRequired = true)] public List<string> Selected { get; set; } = new();
}

internal static class ClientModSelection
{
    public static ClientModCatalog Parse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Client mod catalog exceeds 8 MiB.");
        var catalog = Json.Read<ClientModCatalog>(bytes);
        if (string.IsNullOrWhiteSpace(catalog.ManifestId)) catalog.ManifestId = catalog.ServerId;
        catalog.OptionalGroups ??= new List<OptionalModChoice>();
        if (catalog.SchemaVersion != 1 || string.IsNullOrWhiteSpace(catalog.ManifestId) || catalog.ManifestId.Length > 200
            || catalog.Packages == null || catalog.Configs == null || catalog.Packages.Count > 500 || catalog.OptionalGroups.Count > 100)
            throw new InvalidDataException("Invalid client mod catalog.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in catalog.OptionalGroups)
            if (group == null || string.IsNullOrWhiteSpace(group.Id) || group.Id.Length > 200 || !ids.Add(group.Id)
                || group.Name == null || group.Name.Length > 200 || group.Packages == null || group.Packages.Count > 500)
                throw new InvalidDataException("Invalid optional mod group.");
        if (catalog.OptionalGroups.Sum(group => group.Packages.Count) > 2000) throw new InvalidDataException("Optional catalog is too large.");
        return catalog;
    }

    public static string EffectiveManifest(ClientModCatalog catalog, IEnumerable<string> selection)
    {
        var selected = new HashSet<string>(selection, StringComparer.Ordinal);
        var packages = new Dictionary<string, ManifestPackage>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(ManifestPackage package)
        {
            var identity = package.Namespace + "-" + package.PackageName;
            if (identities.TryGetValue(identity, out var coordinate) && coordinate != package.Coordinate)
                throw new InvalidDataException("Optional mods conflict with a pinned package version: " + identity);
            identities[identity] = package.Coordinate;
            if (packages.TryGetValue(package.Coordinate, out var previous) && previous.Sha256 != package.Sha256)
                throw new InvalidDataException("Optional mods cannot replace a required package digest.");
            packages[package.Coordinate] = package;
        }
        foreach (var package in catalog.Packages) Add(package);
        foreach (var group in catalog.OptionalGroups.Where(group => selected.Contains(group.Id)))
            foreach (var package in group.Packages) Add(package);
        if (packages.Count > 500) throw new InvalidDataException("Too many selected client packages.");
        var ordered = packages.Values.OrderBy(package => package.Coordinate, StringComparer.Ordinal).ToList();
        var revision = ordered.Count == catalog.Packages.Count ? catalog.Revision
            : Hash(catalog.Revision + "\n" + string.Join("\n", ordered.Select(package => package.Coordinate + ":" + package.Sha256)));
        return Encoding.UTF8.GetString(Json.Write(new BootstrapManifest
        {
            SchemaVersion = catalog.SchemaVersion, ManifestId = catalog.ManifestId, Revision = revision,
            GeneratedAt = catalog.GeneratedAt, Packages = ordered, Configs = catalog.Configs
        }));
    }

    public static string PreferencesPath(string bepinexRoot, string manifestId) => Path.Combine(bepinexRoot,
        "config", "dev.creaton.servermanager.optional-mods", Hash(manifestId) + ".json");

    public static HashSet<string> Load(string bepinexRoot, ClientModCatalog catalog)
    {
        var path = PreferencesPath(bepinexRoot, catalog.ManifestId);
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal); // Never opt in implicitly.
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Optional-mod preferences are too large.");
        var saved = Json.ReadFile<ClientModPreferences>(path);
        var valid = new HashSet<string>(catalog.OptionalGroups.Select(group => group.Id), StringComparer.Ordinal);
        return new HashSet<string>((saved.Selected ?? new List<string>()).Where(valid.Contains), StringComparer.Ordinal);
    }

    public static void Save(string bepinexRoot, ClientModCatalog catalog, IEnumerable<string> selection)
    {
        var valid = new HashSet<string>(catalog.OptionalGroups.Select(group => group.Id), StringComparer.Ordinal);
        Json.WriteFile(PreferencesPath(bepinexRoot, catalog.ManifestId), new ClientModPreferences
        { Selected = selection.Where(valid.Contains).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList() });
    }

    private static string Hash(string text) => BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
}
