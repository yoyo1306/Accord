using System.Text.RegularExpressions;
using Accord.Core.Audio;
using Accord.Core.Hardware;
using Accord.Core.Models;
using Accord.Core.Storage;

namespace Accord.Core.Switching;

public sealed class HeadsetPresence
{
    public bool PixelOn { get; init; }
    public bool RazerOn { get; init; }
    public RazerHeadsetState RazerRawState { get; init; }

    public LiveAudioEndpoint? PixelPlay { get; init; }
    public LiveAudioEndpoint? PixelRec { get; init; }
    public LiveAudioEndpoint? RazerPlay { get; init; }
    public LiveAudioEndpoint? RazerRec { get; init; }
    public LiveAudioEndpoint? Screen { get; init; }
}

/// <summary>
/// Shared audio profile switcher and continuous mmsys.cpl name repair engine.
/// Honors custom profile overrides from AppSettings while falling back seamlessly
/// to Accord.backup.txt and automatic Pixel Buds / Razer BlackShark / Philips Evnia detection.
/// </summary>
public sealed class AudioSwitcherEngine
{
    private readonly Dictionary<string, DateTime> _nameRepairCooldown = new(StringComparer.OrdinalIgnoreCase);

    public HeadsetPresence EvaluatePresence(AppSettings? settings = null, List<DeviceBackup>? backups = null)
    {
        settings ??= SettingsStore.Load();
        backups ??= BackupStore.Load();

        List<LiveAudioEndpoint> devices = AudioCoreCom.ListActiveEndpoints();
        RoleBackups roles = ExtractRoleBackups(backups);
        ProfileTargets targets = settings.Profiles ?? new ProfileTargets();

        LiveAudioEndpoint? pixelPlay = ResolveEndpoint(
            devices, "Playback", targets.PixelPlaybackId, roles.PixelPlay,
            ["*Pixel Buds*"], ["*Hands-Free*"]);

        LiveAudioEndpoint? pixelRec = ResolveEndpoint(
            devices, "Recording", targets.PixelRecordingId, roles.PixelRec,
            ["*Pixel Buds*"], ["*Hands-Free*"])
            ?? ResolveEndpoint(devices, "Recording", targets.PixelRecordingId, roles.PixelRec, ["*Pixel Buds*"], null);

        LiveAudioEndpoint? razerPlay = ResolveEndpoint(
            devices, "Playback", targets.RazerPlaybackId, roles.RazerPlay,
            ["*BlackShark*"], ["*Chat*", "*Hands-Free*"]);

        LiveAudioEndpoint? razerRec = ResolveEndpoint(
            devices, "Recording", targets.RazerRecordingId, roles.RazerRec,
            ["*BlackShark*"], null);

        LiveAudioEndpoint? screen = ResolveEndpoint(
            devices, "Playback", targets.ScreenPlaybackId, roles.Screen,
            ["*Evnia*"], null)
            ?? FindLive(devices, "Playback", ["*Philips*"], null);

        RazerHeadsetState razerRaw = razerPlay is not null
            ? RazerDongle.GetWirelessState()
            : RazerHeadsetState.Disconnected;

        bool pixelOn = pixelPlay is not null && BluetoothDetector.IsDeviceConnected("Pixel Buds");
        bool razerOn = razerPlay is not null && razerRaw == RazerHeadsetState.Connected;

        return new HeadsetPresence
        {
            PixelOn = pixelOn,
            RazerOn = razerOn,
            RazerRawState = razerRaw,
            PixelPlay = pixelPlay,
            PixelRec = pixelRec,
            RazerPlay = razerPlay,
            RazerRec = razerRec,
            Screen = screen
        };
    }

    public static string ResolveStartupProfile(HeadsetPresence presence)
    {
        if (presence.PixelOn) return "Pixel";
        if (presence.RazerOn) return "Razer";
        return "Screen";
    }

    public bool ApplyProfile(string profileName, HeadsetPresence presence, Action<string>? log = null)
    {
        if (string.Equals(profileName, "Razer", StringComparison.OrdinalIgnoreCase))
        {
            if (presence.RazerPlay is null) return false;
            AudioCoreCom.SetDefaultAllRoles(presence.RazerPlay.Id);
            if (presence.RazerRec is not null) AudioCoreCom.SetDefaultAllRoles(presence.RazerRec.Id);
            log?.Invoke($"Profil Razer | sortie={presence.RazerPlay.Name} | micro={presence.RazerRec?.Name ?? "(aucun)"}");
            return true;
        }

        if (string.Equals(profileName, "Pixel", StringComparison.OrdinalIgnoreCase))
        {
            if (presence.PixelPlay is null) return false;
            AudioCoreCom.SetDefaultAllRoles(presence.PixelPlay.Id);
            if (presence.PixelRec is not null) AudioCoreCom.SetDefaultAllRoles(presence.PixelRec.Id);
            log?.Invoke($"Profil Pixel | sortie={presence.PixelPlay.Name} | micro={presence.PixelRec?.Name ?? "(aucun)"}");
            return true;
        }

        if (presence.Screen is null) return false;
        AudioCoreCom.SetDefaultAllRoles(presence.Screen.Id);
        log?.Invoke($"Profil Écran | sortie={presence.Screen.Name}");
        return true;
    }

    public bool EnsureApplied(string profileName, HeadsetPresence presence, Action<string>? log = null)
    {
        LiveAudioEndpoint? play = AudioCoreCom.GetDefaultEndpoint(0, 1);
        LiveAudioEndpoint? rec = AudioCoreCom.GetDefaultEndpoint(1, 1);
        bool need = false;

        if (string.Equals(profileName, "Razer", StringComparison.OrdinalIgnoreCase))
        {
            if (presence.RazerPlay is not null && !string.Equals(play?.Id, presence.RazerPlay.Id, StringComparison.OrdinalIgnoreCase)) need = true;
            if (presence.RazerRec is not null && !string.Equals(rec?.Id, presence.RazerRec.Id, StringComparison.OrdinalIgnoreCase)) need = true;
        }
        else if (string.Equals(profileName, "Pixel", StringComparison.OrdinalIgnoreCase))
        {
            if (presence.PixelPlay is not null && !string.Equals(play?.Id, presence.PixelPlay.Id, StringComparison.OrdinalIgnoreCase)) need = true;
            if (presence.PixelRec is not null && !string.Equals(rec?.Id, presence.PixelRec.Id, StringComparison.OrdinalIgnoreCase)) need = true;
        }
        else if (presence.Screen is not null && !string.Equals(play?.Id, presence.Screen.Id, StringComparison.OrdinalIgnoreCase))
        {
            need = true;
        }

        return need && ApplyProfile(profileName, presence, log);
    }

    public void RepairSavedNames(List<DeviceBackup>? backups = null, Action<string>? log = null)
    {
        backups ??= BackupStore.Load();
        if (backups.Count == 0) return;

        List<LiveAudioEndpoint> devices = AudioCoreCom.ListActiveEndpoints();
        Dictionary<string, AudioRegistryService.ActiveHardwareInfo>? hwMap = null;
        DateTime now = DateTime.UtcNow;
        bool backupSelfHealed = false;
        var handledEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Iterate newest-first so latest entry wins if legacy duplicates exist for the same Wave pin
        for (int i = backups.Count - 1; i >= 0; i--)
        {
            DeviceBackup rec = backups[i];
            if (string.IsNullOrEmpty(rec.Id) || rec.IsHandsFree) continue;

            string expectedFlowLabel = string.Equals(rec.Flow, "Render", StringComparison.OrdinalIgnoreCase) ? "Playback" : "Recording";
            string fullId = rec.EndpointId;

            LiveAudioEndpoint? live = devices.FirstOrDefault(d =>
                string.Equals(d.Id, fullId, StringComparison.OrdinalIgnoreCase));

            bool resolvedViaHardware = false;
            string? resolvedRawId = null;

            // Fallback when Windows/NVIDIA regenerates the endpoint GUID (e.g. DisplayPort/HDMI wave08)
            if (live is null && (!string.IsNullOrEmpty(rec.Wave) || !string.IsNullOrEmpty(rec.Hw)))
            {
                hwMap ??= AudioRegistryService.ReadActiveHardwareMap();

                AudioRegistryService.ActiveHardwareInfo? hwHit = null;
                if (!string.IsNullOrEmpty(rec.Wave))
                {
                    hwHit = hwMap.Values.FirstOrDefault(h =>
                        string.Equals(h.Flow, rec.Flow, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(h.Wave, rec.Wave, StringComparison.OrdinalIgnoreCase));
                }

                if (hwHit is null && !string.IsNullOrEmpty(rec.Hw))
                {
                    var sameHw = hwMap.Values.Where(h =>
                        string.Equals(h.Flow, rec.Flow, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(h.Hw, rec.Hw, StringComparison.OrdinalIgnoreCase)).ToList();

                    hwHit = sameHw.Count == 1
                        ? sameHw[0]
                        : sameHw.FirstOrDefault(h =>
                            !string.IsNullOrEmpty(rec.Name) &&
                            string.Equals(h.Name, rec.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (hwHit is not null)
                {
                    live = devices.FirstOrDefault(d =>
                        d.Flow == expectedFlowLabel &&
                        string.Equals(d.Id, hwHit.EndpointId, StringComparison.OrdinalIgnoreCase));
                    if (live is not null)
                    {
                        resolvedViaHardware = true;
                        resolvedRawId = hwHit.RawId;
                    }
                }
            }

            if (live is null) continue;

            if (!handledEndpoints.Add(live.Id))
            {
                // Remove stale duplicate backup that maps to the same live endpoint
                backups.RemoveAt(i);
                backupSelfHealed = true;
                continue;
            }

            if (resolvedViaHardware && !string.IsNullOrEmpty(resolvedRawId) &&
                !string.Equals(rec.Id, resolvedRawId, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"GUID mis à jour ({rec.Flow} {rec.Name}) : {rec.Id} -> {resolvedRawId}");
                rec.Id = resolvedRawId;
                backupSelfHealed = true;
            }

            string want = rec.SavedDisplay;
            if (string.IsNullOrEmpty(want) || string.Equals(live.Name, want, StringComparison.Ordinal))
            {
                _nameRepairCooldown.Remove(live.Id);
                continue;
            }

            if (_nameRepairCooldown.TryGetValue(live.Id, out DateTime until) && now < until)
            {
                continue;
            }

            string label = rec.SavedInterfaceLabel;
            if (string.IsNullOrEmpty(label)) continue;

            try
            {
                AudioCoreCom.SetInterfaceName(live.Id, label);
                log?.Invoke($"Nom réparé ({rec.Flow}) {live.Name} -> {want}");
                _nameRepairCooldown.Remove(live.Id);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Échec réparation nom ({rec.Flow} {rec.Id}) : {ex.Message}");
                _nameRepairCooldown[live.Id] = now.AddSeconds(30);
            }
        }

        if (backupSelfHealed)
        {
            try { BackupStore.SaveAll(backups); } catch { }
        }
    }

    public static WatcherStatus BuildStatus(
        HeadsetPresence presence,
        string activeProfile,
        bool manualOverride,
        int watcherPid,
        string lastEvent)
    {
        LiveAudioEndpoint? defPlay = null;
        LiveAudioEndpoint? defRec = null;
        try
        {
            defPlay = AudioCoreCom.GetDefaultEndpoint(0, 1);
            defRec = AudioCoreCom.GetDefaultEndpoint(1, 1);
        }
        catch { }

        return new WatcherStatus
        {
            UpdatedUtc = DateTime.UtcNow,
            WatcherPid = watcherPid,
            ActiveProfile = activeProfile,
            ManualOverride = manualOverride,
            PixelConnected = presence.PixelOn,
            RazerConnected = presence.RazerOn,
            RazerStateLabel = presence.RazerRawState switch
            {
                RazerHeadsetState.Connected => "Connecté (2,4 GHz)",
                RazerHeadsetState.Disconnected => "Éteint / déconnecté",
                _ => "Indéterminé"
            },
            PixelPlaybackName = presence.PixelPlay?.Name ?? string.Empty,
            PixelRecordingName = presence.PixelRec?.Name ?? string.Empty,
            RazerPlaybackName = presence.RazerPlay?.Name ?? string.Empty,
            RazerRecordingName = presence.RazerRec?.Name ?? string.Empty,
            ScreenPlaybackName = presence.Screen?.Name ?? string.Empty,
            DefaultPlaybackName = defPlay?.Name ?? string.Empty,
            DefaultRecordingName = defRec?.Name ?? string.Empty,
            LastEvent = lastEvent
        };
    }

    private sealed class RoleBackups
    {
        public DeviceBackup? PixelPlay;
        public DeviceBackup? PixelRec;
        public DeviceBackup? RazerPlay;
        public DeviceBackup? RazerRec;
        public DeviceBackup? Screen;
    }

    private static RoleBackups ExtractRoleBackups(List<DeviceBackup> records)
    {
        var roles = new RoleBackups();
        foreach (DeviceBackup rec in records)
        {
            bool render = string.Equals(rec.Flow, "Render", StringComparison.OrdinalIgnoreCase);
            if (render && roles.PixelPlay is null && Like(rec.Description, "*Pixel Buds*") && !Like(rec.Description, "*Hands-Free*"))
                roles.PixelPlay = rec;
            if (!render && roles.PixelRec is null && Like(rec.Description, "*Pixel Buds*"))
                roles.PixelRec = rec;
            if (render && roles.RazerPlay is null && (Eq(rec.Name, "Razer") || (Like(rec.Description, "*BlackShark*") && !Like(rec.Description, "*Chat*"))))
                roles.RazerPlay = rec;
            if (!render && roles.RazerRec is null && (Eq(rec.Name, "Razer") || Like(rec.Description, "*BlackShark*")))
                roles.RazerRec = rec;
            if (render && roles.Screen is null && (Eq(rec.Name, "Philips") || Like(rec.Description, "*Evnia*")))
                roles.Screen = rec;
        }
        return roles;
    }

    private static LiveAudioEndpoint? ResolveEndpoint(
        List<LiveAudioEndpoint> devices,
        string flowLabel,
        string? explicitEndpointId,
        DeviceBackup? backup,
        string[] includePatterns,
        string[]? excludePatterns)
    {
        if (!string.IsNullOrWhiteSpace(explicitEndpointId))
        {
            LiveAudioEndpoint? explicitHit = devices.FirstOrDefault(d =>
                d.Flow == flowLabel &&
                string.Equals(d.Id, explicitEndpointId, StringComparison.OrdinalIgnoreCase));
            if (explicitHit is not null) return explicitHit;
        }

        if (backup is not null)
        {
            string id = backup.EndpointId;
            LiveAudioEndpoint? byId = devices.FirstOrDefault(d => d.Flow == flowLabel && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;

            string friendly = string.IsNullOrEmpty(backup.Description) ? backup.Name : $"{backup.Name} ({backup.Description})";
            LiveAudioEndpoint? byName = devices.FirstOrDefault(d => d.Flow == flowLabel && string.Equals(d.Name, friendly, StringComparison.Ordinal));
            if (byName is not null) return byName;
        }

        return FindLive(devices, flowLabel, includePatterns, excludePatterns);
    }

    private static LiveAudioEndpoint? FindLive(List<LiveAudioEndpoint> devices, string flowLabel, string[] include, string[]? exclude)
    {
        foreach (LiveAudioEndpoint d in devices)
        {
            if (d.Flow != flowLabel) continue;
            bool ok = true;
            foreach (string inc in include)
            {
                if (!Like(d.Name, inc)) { ok = false; break; }
            }
            if (!ok) continue;

            if (exclude is not null)
            {
                foreach (string exc in exclude)
                {
                    if (Like(d.Name, exc)) { ok = false; break; }
                }
            }

            if (ok) return d;
        }
        return null;
    }

    private static bool Like(string? text, string pattern)
    {
        string body = Regex.Escape(pattern ?? string.Empty).Replace("\\*", ".*").Replace("\\?", ".");
        return Regex.IsMatch(text ?? string.Empty, $"^{body}$", RegexOptions.IgnoreCase);
    }

    private static bool Eq(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
