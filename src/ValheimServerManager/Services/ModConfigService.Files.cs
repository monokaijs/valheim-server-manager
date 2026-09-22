using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.EntityFrameworkCore;

namespace ValheimServerManager.Services;

public sealed record ConfigTreeEntry(string Path, string Name, string Kind, long Size, DateTimeOffset? ModifiedAt);
public sealed record ConfigFileTree(IReadOnlyList<string> Roots, IReadOnlyList<ConfigTreeEntry> Entries);
public sealed record ConfigTextFile(string Path, string Revision, string Content, DateTimeOffset ModifiedAt);
public sealed record ConfigFileMutation(string Operation, string Path, string? Revision = null,
    string? Content = null, string? Destination = null, bool Directory = false);

public sealed partial class ModConfigService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cfg", ".json", ".yaml", ".yml", ".toml", ".ini", ".txt", ".xml" };
    private const int MaxTreeEntries = 1000;
    private sealed record FileScope(HashSet<string> Exact, string[] Roots)
    {
        public bool Owns(string path) => Exact.Contains(path) || Roots.Any(root =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ConfigFileTree> FileTree(Guid modId, CancellationToken ct = default)
    {
        var scope = await ResolveFileScope(modId, ct);
        return BuildFileTree(scope);
    }

    private ConfigFileTree BuildFileTree(FileScope scope)
    {
        var entries = new Dictionary<string, ConfigTreeEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string relative, bool directory)
        {
            var path = ResolveTextPath(_configRoot, relative, directory);
            if (entries.Count >= MaxTreeEntries && !entries.ContainsKey(relative))
                throw new InvalidOperationException("This mod's file tree exceeds 1000 entries. Manage excess files outside the dashboard.");
            if (directory)
                entries[relative] = new(relative, System.IO.Path.GetFileName(relative), "directory", 0,
                    System.IO.Directory.Exists(path) ? System.IO.Directory.GetLastWriteTimeUtc(path) : null);
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                entries[relative] = new(relative, info.Name, "file", info.Length, info.LastWriteTimeUtc);
            }
            var parent = System.IO.Path.GetDirectoryName(relative)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !entries.ContainsKey(parent)) Add(parent, true);
        }
        void Walk(string relative)
        {
            Add(relative, true);
            var path = ResolveTextPath(_configRoot, relative, true);
            if (!System.IO.Directory.Exists(path)) return;
            var inspected = 0;
            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path))
            {
                if (++inspected > MaxTreeEntries) throw new InvalidOperationException("Directory contains too many entries.");
                var name = relative + "/" + System.IO.Path.GetFileName(child);
                // Fail closed on links, including dangling links, before walking any child.
                var directory = System.IO.Directory.Exists(child);
                if (new FileInfo(child).LinkTarget is not null || new DirectoryInfo(child).LinkTarget is not null)
                    continue;
                if (directory) Walk(name);
                else if (TextExtensions.Contains(System.IO.Path.GetExtension(child))) Add(name, false);
            }
        }
        foreach (var root in scope.Roots) Walk(root);
        foreach (var file in scope.Exact) Add(file, false);
        return new(scope.Roots, entries.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<ConfigTextFile> ReadFile(Guid modId, string file, CancellationToken ct = default)
    {
        var scope = await ResolveFileScope(modId, ct);
        var path = OwnedTextPath(scope, file, false);
        var bytes = await ReadBounded(path, ct);
        await audit.Write("mod.file.read", modId.ToString(), detail: "file:" + file);
        return new(file, Hash(bytes), Decode(bytes), File.GetLastWriteTimeUtc(path));
    }

    public async Task MutateFile(Guid modId, ConfigFileMutation request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var scope = await ResolveFileScope(modId, ct);
            var path = OwnedTextPath(scope, request.Path, request.Directory);
            if (scope.Roots.Contains(request.Path, StringComparer.OrdinalIgnoreCase)
                && request.Operation is "delete" or "rename")
                throw new InvalidOperationException("A mod's configuration root cannot be moved or deleted.");
            if (request.Directory)
            {
                if (request.Operation != "mkdir" && request.Operation != "delete")
                    throw new ArgumentException("Directories support creation and empty-directory deletion only.");
                if (request.Operation == "mkdir")
                {
                    CheckTreeCapacity(scope, request.Path);
                    if (File.Exists(path) || System.IO.Directory.Exists(path)) throw new InvalidOperationException("The destination already exists.");
                    System.IO.Directory.CreateDirectory(path);
                }
                else
                {
                    if (!System.IO.Directory.Exists(path)) throw new KeyNotFoundException("Directory was not found.");
                    if (System.IO.Directory.EnumerateFileSystemEntries(path).Any()) throw new InvalidOperationException("Only empty directories can be deleted.");
                    System.IO.Directory.Delete(path, false);
                }
            }
            else if (request.Operation == "create")
            {
                CheckTreeCapacity(scope, request.Path);
                var bytes = ValidateText(request.Path, request.Content);
                if (File.Exists(path) || System.IO.Directory.Exists(path)) throw new InvalidOperationException("The destination already exists.");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await AtomicTextWrite(path, bytes, overwrite: false, ct);
            }
            else if (request.Operation is "save" or "delete" or "rename")
            {
                var original = await ReadBounded(path, ct);
                if (request.Revision is null || !Hash(original).Equals(request.Revision, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The file changed since it was opened. Reload it before continuing; your draft has not been applied.");
                byte[]? changed = request.Operation == "save" ? ValidateText(request.Path, request.Content) : null;
                string? destination = null;
                if (request.Operation == "rename")
                {
                    if (request.Destination is null) throw new ArgumentException("A destination path is required.");
                    destination = OwnedTextPath(scope, request.Destination, false);
                    if (File.Exists(destination) || System.IO.Directory.Exists(destination)) throw new InvalidOperationException("The destination already exists.");
                    ValidateText(request.Destination, Decode(original));
                }
                Backup(modId, request.Path, original);
                // Structured and raw editors share the same gate. Also detect a game/plugin write
                // that happened while validation/backup was running.
                if (!Hash(await ReadBounded(path, ct)).Equals(request.Revision, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The plugin modified this file. Reload it before continuing.");
                if (request.Operation == "save") await AtomicTextWrite(path, changed!, true, ct);
                else if (request.Operation == "delete") File.Delete(path);
                else
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination!)!);
                    File.Move(path, destination!, false);
                }
            }
            else throw new ArgumentException("Unsupported file operation.");
            await MarkPending(ct);
            await audit.Write("mod.file." + request.Operation, modId.ToString(), detail:
                "file:" + request.Path + (request.Destination is null ? "" : "; destination:" + request.Destination));
        }
        finally { _gate.Release(); }
    }

    private void CheckTreeCapacity(FileScope scope, string relative)
    {
        var existing = BuildFileTree(scope).Entries.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = 0;
        var current = relative;
        while (!string.IsNullOrEmpty(current))
        {
            if (!existing.Contains(current)) additions++;
            current = System.IO.Path.GetDirectoryName(current)?.Replace('\\', '/') ?? "";
        }
        if (existing.Count + additions > MaxTreeEntries) throw new InvalidOperationException("The file tree would exceed the 1000-entry limit.");
    }

    private async Task<FileScope> ResolveFileScope(Guid modId, CancellationToken ct)
    {
        await using var databaseScope = scopes.CreateAsyncScope();
        var db = databaseScope.ServiceProvider.GetRequiredService<Data.ManagerDbContext>();
        var mods = await db.InstalledMods.AsNoTracking().ToListAsync(ct);
        var mod = mods.SingleOrDefault(item => item.Id == modId) ?? throw new KeyNotFoundException("Mod was not found.");
        if (mod.Protected) throw new InvalidOperationException("Protected infrastructure is not accessible through the file manager.");
        var files = (JsonSerializer.Deserialize<string[]>(mod.FilesJson, JsonOptions) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plugins = (await registry.Read(ct)).Where(plugin => files.Contains(plugin.Dll)).ToArray();
        var exact = (await ResolveOwnedFiles(modId, ct)).Select(item => item.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(item => item.StartsWith("config/", StringComparison.OrdinalIgnoreCase)))
            if (TextExtensions.Contains(System.IO.Path.GetExtension(file))) exact.Add(file[7..]);
        var fallback = mod.Namespace + "-" + mod.Name;
        var roots = plugins.Select(plugin => plugin.Guid).Append(fallback)
            .Where(root => root.Length > 0 && root.Length <= 200 && root.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_') && root is not "." and not "..")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var others = mods.Where(item => item.Id != modId).SelectMany(item => JsonSerializer.Deserialize<string[]>(item.FilesJson, JsonOptions) ?? [])
            .Where(item => item.StartsWith("config/", StringComparison.OrdinalIgnoreCase)).Select(item => item[7..]).ToArray();
        var otherPlugins = (await registry.Read(ct)).Where(plugin => !files.Contains(plugin.Dll)).ToArray();
        // A package cannot claim a sibling's config namespace, even if its name collides.
        roots = roots.Where(root => !others.Any(file => file.Equals(root, StringComparison.OrdinalIgnoreCase) || file.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            && !otherPlugins.Any(plugin => plugin.Guid.Equals(root, StringComparison.OrdinalIgnoreCase)
                || plugin.ConfigFile.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
        exact.ExceptWith(others);
        foreach (var plugin in otherPlugins) exact.Remove(plugin.ConfigFile);
        return new(exact, roots);
    }

    private string OwnedTextPath(FileScope scope, string relative, bool directory)
    {
        var path = ResolveTextPath(_configRoot, relative, directory);
        if (!scope.Owns(relative)) throw new InvalidOperationException("That path is outside this package's configuration namespace.");
        return path;
    }

    internal static string ResolveTextPath(string root, string relative, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 500 || System.IO.Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl)
            || relative.Split('/').Length > 16 || relative.Split('/').Any(segment => segment is "" or "." or ".." || segment != segment.Trim()))
            throw new InvalidDataException("Use a relative path without empty segments, traversal, or special path characters.");
        if (!directory && !TextExtensions.Contains(System.IO.Path.GetExtension(relative)))
            throw new InvalidDataException("Only cfg, json, yaml, yml, toml, ini, txt and xml text files are editable.");
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Configuration path escaped the allowed directory.");
        var current = fullRoot;
        foreach (var segment in new[] { "" }.Concat(relative.Split('/')))
        {
            if (segment.Length > 0) current = System.IO.Path.Combine(current, segment);
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null
                || ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Symbolic links are not supported in the configuration file manager.");
        }
        return path;
    }

    private static async Task<byte[]> ReadBounded(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new KeyNotFoundException("Configuration file was not found.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        if (stream.Length > MaxConfigBytes) throw new InvalidDataException("Configuration file exceeds the 2 MiB limit.");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (output.Length + read > MaxConfigBytes) throw new InvalidDataException("Configuration file exceeds the 2 MiB limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        var bytes = output.ToArray();
        _ = Decode(bytes);
        if (bytes.Contains((byte)0)) throw new InvalidDataException("Binary files are not supported.");
        return bytes;
    }

    internal static byte[] ValidateText(string path, string? text)
    {
        if (text is null || text.Contains('\0')) throw new InvalidDataException("UTF-8 text content is required.");
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > MaxConfigBytes) throw new InvalidDataException("Configuration file exceeds the 2 MiB limit.");
        if (System.IO.Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { using var document = JsonDocument.Parse(text); }
            catch (JsonException) { throw new InvalidDataException("The file does not contain valid JSON."); }
        }
        if (System.IO.Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                while (reader.Read()) { }
            }
            catch (XmlException) { throw new InvalidDataException("The file does not contain safe, valid XML."); }
        }
        return bytes;
    }

    private static async Task AtomicTextWrite(string path, byte[] bytes, bool overwrite, CancellationToken ct)
    {
        var temporary = path + ".vsm-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, overwrite ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
