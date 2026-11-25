using System.Text;
using Accord.Core.Models;

namespace Accord.Core.Storage;

/// <summary>
/// Reads and writes Accord.backup.txt in 100% backward-compatible format
/// (plus optional Hw / Wave hardware keys for resilient GUID resolution on boot restore).
/// </summary>
public static class BackupStore
{
    public static string FilePath => AppPaths.BackupPath;

    public static List<DeviceBackup> Load(string? customPath = null)
    {
        string path = customPath ?? FilePath;
        var list = new List<DeviceBackup>();
        if (!File.Exists(path)) return list;

        DeviceBackup? cur = null;
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        foreach (string line in lines)
        {
            if (line == "---")
            {
                if (cur is not null && !string.IsNullOrEmpty(cur.Id))
                {
                    list.Add(cur);
                }
                cur = null;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            cur ??= new DeviceBackup();
            string key = line[..eq];
            string val = Unescape(line[(eq + 1)..]);

            switch (key)
            {
                case "Flow": cur.Flow = val; break;
                case "Id": cur.Id = val; break;
                case "Name": cur.Name = val; break;
                case "Description": cur.Description = val; break;
                case "HasAlias": cur.HasAlias = val == "1"; break;
                case "Alias": cur.Alias = val; break;
                case "HasInterface": cur.HasInterface = val == "1"; break;
                case "Interface": cur.InterfaceName = val; break;
                case "HasFriendly": cur.HasFriendly = val == "1"; break;
                case "FriendlyName": cur.FriendlyName = val; break;
                case "HasDeviceDesc": cur.HasDeviceDesc = val == "1"; break;
                case "DeviceDesc": cur.DeviceDesc = val; break;
                case "Hw": cur.Hw = val; break;
                case "Wave": cur.Wave = val; break;
            }
        }

        if (cur is not null && !string.IsNullOrEmpty(cur.Id))
        {
            list.Add(cur);
        }

        return list;
    }

    public static DeviceBackup? Find(string flow, string id, string? wave = null, string? customPath = null)
    {
        List<DeviceBackup> all = Load(customPath);

        // 1. Exact Flow + GUID match (prefer latest if duplicates exist)
        for (int i = all.Count - 1; i >= 0; i--)
        {
            DeviceBackup item = all[i];
            if (string.Equals(item.Flow, flow, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        // 2. Fallback: exact Flow + Wave hardware pin match (survives NVIDIA/HDMI/DP GUID regeneration)
        if (!string.IsNullOrWhiteSpace(wave))
        {
            for (int i = all.Count - 1; i >= 0; i--)
            {
                DeviceBackup item = all[i];
                if (string.Equals(item.Flow, flow, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.Wave) &&
                    string.Equals(item.Wave, wave, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
        }

        return null;
    }

    public static bool Exists(string flow, string id, string? wave = null, string? customPath = null) =>
        Find(flow, id, wave, customPath) is not null;

    public static void Save(DeviceBackup backup, string? customPath = null)
    {
        string path = customPath ?? FilePath;
        List<DeviceBackup> all = Load(path);

        // Remove any prior entries for the same (Flow, Id) or same physical (Flow, Wave) pin
        // so GUID regeneration never accumulates stale duplicates.
        int firstMatch = -1;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            DeviceBackup existing = all[i];
            bool sameFlow = string.Equals(existing.Flow, backup.Flow, StringComparison.OrdinalIgnoreCase);
            bool sameId = sameFlow && string.Equals(existing.Id, backup.Id, StringComparison.OrdinalIgnoreCase);
            bool sameWave = sameFlow &&
                            !string.IsNullOrWhiteSpace(backup.Wave) &&
                            !string.IsNullOrWhiteSpace(existing.Wave) &&
                            string.Equals(existing.Wave, backup.Wave, StringComparison.OrdinalIgnoreCase);

            if (sameId || sameWave)
            {
                firstMatch = i;
                all.RemoveAt(i);
            }
        }

        if (firstMatch >= 0 && firstMatch <= all.Count) all.Insert(firstMatch, backup);
        else all.Add(backup);

        SaveAll(all, path);
    }

    public static void SaveAll(IEnumerable<DeviceBackup> backups, string? customPath = null)
    {
        string path = customPath ?? FilePath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        foreach (DeviceBackup b in backups)
        {
            sb.Append("Flow=").AppendLine(Escape(b.Flow));
            sb.Append("Id=").AppendLine(Escape(b.Id));
            sb.Append("Name=").AppendLine(Escape(b.Name));
            sb.Append("Description=").AppendLine(Escape(b.Description));
            sb.Append("HasAlias=").AppendLine(b.HasAlias ? "1" : "0");
            sb.Append("Alias=").AppendLine(Escape(b.Alias));
            sb.Append("HasInterface=").AppendLine(b.HasInterface ? "1" : "0");
            sb.Append("Interface=").AppendLine(Escape(b.InterfaceName));
            sb.Append("HasFriendly=").AppendLine(b.HasFriendly ? "1" : "0");
            sb.Append("FriendlyName=").AppendLine(Escape(b.FriendlyName));
            sb.Append("HasDeviceDesc=").AppendLine(b.HasDeviceDesc ? "1" : "0");
            sb.Append("DeviceDesc=").AppendLine(Escape(b.DeviceDesc));
            if (!string.IsNullOrEmpty(b.Hw)) sb.Append("Hw=").AppendLine(Escape(b.Hw));
            if (!string.IsNullOrEmpty(b.Wave)) sb.Append("Wave=").AppendLine(Escape(b.Wave));
            sb.AppendLine("---");
        }

        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public static string Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char n = value[i + 1];
                if (n == '\\') { sb.Append('\\'); i += 2; continue; }
                if (n == 'r') { sb.Append('\r'); i += 2; continue; }
                if (n == 'n') { sb.Append('\n'); i += 2; continue; }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
