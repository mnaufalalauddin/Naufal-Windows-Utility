using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed class DebloatRegistry : IDebloatRegistry
{
    public DebloatValue? Read(DebloatTarget target)
    {
        using var root = RegistryKey.OpenBaseKey(target.Hive, target.View);
        using var key = root.OpenSubKey(target.Path);
        object? value = key?.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        var kind = key!.GetValueKind(target.Name);
        return new(kind, kind switch
        {
            RegistryValueKind.MultiString => (string[])value,
            RegistryValueKind.Binary or RegistryValueKind.None => [Convert.ToBase64String((byte[])value)],
            _ => [Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""]
        });
    }
    internal static object Decode(DebloatValue value) => value.Kind switch
    {
        RegistryValueKind.MultiString => value.Data,
        RegistryValueKind.DWord => int.Parse(value.Data.Single(), CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(value.Data.Single(), CultureInfo.InvariantCulture),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value.Data.Single()),
        RegistryValueKind.String or RegistryValueKind.ExpandString => value.Data.Single(),
        _ => throw new InvalidDataException("Unsupported registry data in recovery record.")
    };
    public void Write(DebloatEdit edit)
    {
        using var root = RegistryKey.OpenBaseKey(edit.Target.Hive, edit.Target.View);
        if (edit.Value is null)
        {
            using var key = root.OpenSubKey(edit.Target.Path, true);
            key?.DeleteValue(edit.Target.Name, false);
        }
        else
        {
            using var key = root.CreateSubKey(edit.Target.Path, true);
            key.SetValue(edit.Target.Name, Decode(edit.Value), edit.Value.Kind);
            key.Flush();
        }
    }
}

internal sealed class DebloatJournal : IDebloatJournal
{
    internal static string DirectoryPath => Path.Combine(AppDataPaths.LocalRoot, "Backups", "DebloatWorkspace");
    internal static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private static string FilePath(string id) => Path.Combine(DirectoryPath, Key(id) + ".json");
    public DebloatSnapshot? Load(string id)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize(File.ReadAllText(FilePath(id)), DebloatWorkspaceJson.Default.DebloatSnapshot)
                ?? throw new InvalidDataException("Empty recovery record.");
            if (snapshot.Id != id || snapshot.Original is null || snapshot.Applied is null)
                throw new InvalidDataException("Invalid recovery identity.");
            foreach (var edit in snapshot.Original.Concat(snapshot.Applied))
                if (edit.Value is not null) _ = DebloatRegistry.Decode(edit.Value);
            return snapshot;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal DebloatSnapshot[] List()
    {
        string[] files;
        try { files = Directory.GetFiles(DirectoryPath, "*.json"); }
        catch (DirectoryNotFoundException) { return []; }
        // Access denied is not proof that no other control owns a snapshot.
        return files.Select(path =>
        {
            var item = JsonSerializer.Deserialize(File.ReadAllText(path), DebloatWorkspaceJson.Default.DebloatSnapshot)
                ?? throw new InvalidDataException("Unreadable recovery record.");
            if (Path.GetFileName(path) != Key(item.Id) + ".json") throw new InvalidDataException("Recovery record identity mismatch.");
            return Load(item.Id)!;
        }).ToArray();
    }
    public void Save(DebloatSnapshot snapshot) => AtomicWrite(FilePath(snapshot.Id),
        JsonSerializer.Serialize(snapshot, DebloatWorkspaceJson.Default.DebloatSnapshot));
    public void Delete(string id) => File.Delete(FilePath(id));
    internal static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            stream.Write(data);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    internal static void RequireNoGranularAi()
    {
        if (new DebloatJournal().List().Any(s => s.Id.StartsWith("ai-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Restore the individual AI controls in De-bloat workspace before using the combined Windows AI / Paint AI control. Original snapshots were retained.");
    }
}

internal static class DebloatRemovalHistory
{
    private static readonly object Sync = new();
    private static string HistoryPath => Path.Combine(AppDataPaths.LocalRoot, "Logs", "BuiltInApps", "verified-history.json");
    internal static DebloatRemovalEvent[] Read()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(HistoryPath), DebloatWorkspaceJson.Default.DebloatRemovalEventArray)
                ?? throw new InvalidDataException("Removal history is unreadable.");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }
    internal static void Record(string id, bool restored)
    {
        // Only called after the orchestration verified the exact app family.
        _ = BuiltInAppsCatalog.ResolveSelection([id]);
        lock (Sync)
            DebloatJournal.AtomicWrite(HistoryPath, JsonSerializer.Serialize(
                Read().Append(new(id, restored, DateTimeOffset.UtcNow)).ToArray(),
                DebloatWorkspaceJson.Default.DebloatRemovalEventArray));
    }
}
