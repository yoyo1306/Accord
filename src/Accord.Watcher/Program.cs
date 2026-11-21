using System.Runtime.InteropServices;
using Accord.Core;
using Accord.Core.Audio;
using Accord.Core.Models;
using Accord.Core.Storage;
using Accord.Core.Switching;
using Microsoft.Win32.SafeHandles;

namespace Accord.Watcher;

internal static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    private static int Main(string[] args)
    {
        bool testOnly = false;
        string? dataDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (Eq(args[i], "--test-only") || Eq(args[i], "-TestOnly")) testOnly = true;
            else if (Eq(args[i], "--data-dir") && i + 1 < args.Length) dataDir = args[++i];
        }

        if (!string.IsNullOrWhiteSpace(dataDir)) AppPaths.ConfigureOverride(dataDir);
        AppPaths.EnsureInitialized();

        if (testOnly)
        {
            AttachParentConsole();
            return RunDiagnostic();
        }

        _instanceMutex = new Mutex(false, @"Local\Accord.Watcher");
        bool owned;
        try { owned = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        RunLoop();
        return 0;
    }

    private static int RunDiagnostic()
    {
        var engine = new AudioSwitcherEngine();
        AppSettings settings = SettingsStore.Load();
        HeadsetPresence presence = engine.EvaluatePresence(settings);
        string active = AudioSwitcherEngine.ResolveStartupProfile(presence);

        Console.WriteLine($"DataDir={AppPaths.DataDir} (Portable={AppPaths.IsPortable})");
        Console.WriteLine($"PixelOn={presence.PixelOn} RazerOn={presence.RazerOn} ({presence.RazerRawState}) -> {active}");
        Console.WriteLine($"PixelPlay {FormatEndpoint(presence.PixelPlay)}");
        Console.WriteLine($"PixelRec  {FormatEndpoint(presence.PixelRec)}");
        Console.WriteLine($"RazerPlay {FormatEndpoint(presence.RazerPlay)}");
        Console.WriteLine($"RazerRec  {FormatEndpoint(presence.RazerRec)}");
        Console.WriteLine($"Screen    {FormatEndpoint(presence.Screen)}");
        return 0;
    }

    private static void RunLoop()
    {
        var engine = new AudioSwitcherEngine();
        string activeProfile = "Screen";
        bool pixelWas = false;
        bool razerWas = false;
        bool manualOverride = false;
        long lastManualStamp = 0;
        string lastEvent = "Surveillance démarrée";

        void Log(string msg)
        {
            lastEvent = msg;
            AppPaths.AppendLog(AppPaths.WatcherLogPath, msg);
        }

        try
        {
            AppSettings settings = SettingsStore.Load();
            List<DeviceBackup> backups = BackupStore.Load();
            HeadsetPresence presence = engine.EvaluatePresence(settings, backups);

            lastManualStamp = settings.ManualOverrideStamp;
            if (!string.IsNullOrWhiteSpace(settings.ManualProfileOverride))
            {
                activeProfile = settings.ManualProfileOverride;
                manualOverride = true;
            }
            else
            {
                activeProfile = AudioSwitcherEngine.ResolveStartupProfile(presence);
            }

            Log($"Surveillance démarrée (Pixel={presence.PixelOn}, RazerHID={presence.RazerOn} -> {activeProfile})");
            engine.RepairSavedNames(backups, Log);
            engine.ApplyProfile(activeProfile, presence, Log);

            pixelWas = presence.PixelOn;
            razerWas = presence.RazerOn;

            SettingsStore.SaveWatcherStatus(AudioSwitcherEngine.BuildStatus(
                presence, activeProfile, manualOverride, Environment.ProcessId, lastEvent));
        }
        catch (Exception ex)
        {
            Log("Erreur démarrage : " + ex.Message);
        }

        while (true)
        {
            int pollSeconds = 2;
            try
            {
                AppSettings settings = SettingsStore.Load();
                pollSeconds = Math.Clamp(settings.PollIntervalSeconds, 1, 15);
                List<DeviceBackup> backups = BackupStore.Load();

                engine.RepairSavedNames(backups, Log);
                HeadsetPresence presence = engine.EvaluatePresence(settings, backups);
                bool pixelOn = presence.PixelOn;
                bool razerOn = presence.RazerOn;

                bool hardwareChanged = false;

                // Manual override triggered from UI
                if (settings.ManualOverrideStamp != lastManualStamp)
                {
                    lastManualStamp = settings.ManualOverrideStamp;
                    if (!string.IsNullOrWhiteSpace(settings.ManualProfileOverride))
                    {
                        activeProfile = settings.ManualProfileOverride;
                        manualOverride = true;
                        engine.ApplyProfile(activeProfile, presence, Log);
                    }
                    else
                    {
                        manualOverride = false;
                        activeProfile = AudioSwitcherEngine.ResolveStartupProfile(presence);
                        engine.ApplyProfile(activeProfile, presence, Log);
                    }
                }

                // Hardware transitions always take priority and clear manual override
                if (razerOn && !razerWas)
                {
                    activeProfile = "Razer";
                    manualOverride = false;
                    hardwareChanged = true;
                    engine.ApplyProfile(activeProfile, presence, Log);
                }
                else if (pixelOn && !pixelWas)
                {
                    activeProfile = "Pixel";
                    manualOverride = false;
                    hardwareChanged = true;
                    engine.ApplyProfile(activeProfile, presence, Log);
                }
                else if (activeProfile == "Razer" && !razerOn && !manualOverride)
                {
                    activeProfile = pixelOn ? "Pixel" : "Screen";
                    hardwareChanged = true;
                    engine.ApplyProfile(activeProfile, presence, Log);
                }
                else if (activeProfile == "Pixel" && !pixelOn && !manualOverride)
                {
                    activeProfile = razerOn ? "Razer" : "Screen";
                    hardwareChanged = true;
                    engine.ApplyProfile(activeProfile, presence, Log);
                }
                else
                {
                    engine.EnsureApplied(activeProfile, presence, Log);
                }

                if (hardwareChanged && !string.IsNullOrWhiteSpace(settings.ManualProfileOverride))
                {
                    settings.ManualProfileOverride = string.Empty;
                    settings.ManualOverrideStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    lastManualStamp = settings.ManualOverrideStamp;
                    SettingsStore.Save(settings);
                }

                pixelWas = pixelOn;
                razerWas = razerOn;

                SettingsStore.SaveWatcherStatus(AudioSwitcherEngine.BuildStatus(
                    presence, activeProfile, manualOverride, Environment.ProcessId, lastEvent));
            }
            catch (Exception ex)
            {
                Log("Erreur boucle : " + ex.Message);
            }

            Thread.Sleep(pollSeconds * 1000);
        }
    }

    private static string FormatEndpoint(LiveAudioEndpoint? endpoint) =>
        endpoint is null ? "(absent)" : $"{endpoint.Name} {endpoint.Id}";

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void AttachParentConsole()
    {
        IntPtr existing = GetStdHandle(-11);
        if (existing != IntPtr.Zero && existing != new IntPtr(-1))
        {
            Console.SetOut(new StreamWriter(new FileStream(new SafeFileHandle(existing, false), FileAccess.Write)) { AutoFlush = true });
            return;
        }
        if (!AttachConsole(-1)) return;
        IntPtr handle = CreateFile("CONOUT$", 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return;
        SetStdHandle(-11, handle);
        Console.SetOut(new StreamWriter(new FileStream(new SafeFileHandle(handle, true), FileAccess.Write)) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);
}
