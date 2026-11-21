using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: AssemblyTitle("Accord")]
[assembly: AssemblyProduct("Accord")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyVersion("1.2.0.0")]

static class Program
{
    const int PollSeconds = 2;
    static readonly string Root = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Program).Assembly.Location));
    static readonly string BackupPath = Path.Combine(Root, "Accord.backup.txt");
    static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoAudioSwitch");
    static readonly string LogPath = Path.Combine(AppDir, "log.txt");
    static readonly string StatePath = Path.Combine(AppDir, "state.txt");
    static Mutex InstanceMutex;
    static Dictionary<string, DateTime> NameRepairFail = new Dictionary<string, DateTime>();

    static int Main(string[] args)
    {
        bool testOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "-TestOnly", StringComparison.OrdinalIgnoreCase)) testOnly = true;
        }

        if (testOnly) AttachParentConsole();
        AudioCore.Initialize();

        if (!testOnly)
        {
            InstanceMutex = new Mutex(false, @"Local\AutoAudioSwitch");
            bool owned;
            try { owned = InstanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 0;
        }

        Directory.CreateDirectory(AppDir);
        RotateLog();

        if (testOnly)
        {
            Presence presence = GetHeadsetPresence();
            string active = ResolveStartupProfile(presence);
            Console.WriteLine("PixelOn=" + presence.PixelOn + " RazerOn=" + presence.RazerOn + " -> " + active);
            Console.WriteLine("PixelPlay " + FormatDevice(presence.PixelPlay));
            Console.WriteLine("PixelRec " + FormatDevice(presence.PixelRec));
            Console.WriteLine("RazerPlay " + FormatDevice(presence.RazerPlay));
            Console.WriteLine("RazerRec " + FormatDevice(presence.RazerRec));
            Console.WriteLine("Screen " + FormatDevice(presence.Screen));
            return 0;
        }

        string activeProfile = "Screen";
        bool pixelWas = false;
        bool razerWas = false;
        try
        {
            Presence presence = GetHeadsetPresence();
            activeProfile = ResolveStartupProfile(presence);
            Log("Watcher started (Accord names + Pixel BT + Razer HID)");
            Log("Startup: Pixel=" + presence.PixelOn + " RazerHID=" + presence.RazerOn + " -> " + activeProfile);
            RepairSavedNames();
            ApplyProfile(activeProfile, presence);
            pixelWas = presence.PixelOn;
            razerWas = presence.RazerOn;
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
        }

        while (true)
        {
            try
            {
                RepairSavedNames();
                Presence presence = GetHeadsetPresence();
                bool pixelOn = presence.PixelOn;
                bool razerOn = presence.RazerOn;
                if (razerOn && !razerWas)
                {
                    activeProfile = "Razer";
                    ApplyProfile(activeProfile, presence);
                }
                else if (pixelOn && !pixelWas)
                {
                    activeProfile = "Pixel";
                    ApplyProfile(activeProfile, presence);
                }
                else if (activeProfile == "Razer" && !razerOn)
                {
                    activeProfile = pixelOn ? "Pixel" : "Screen";
                    ApplyProfile(activeProfile, presence);
                }
                else if (activeProfile == "Pixel" && !pixelOn)
                {
                    activeProfile = razerOn ? "Razer" : "Screen";
                    ApplyProfile(activeProfile, presence);
                }
                else
                {
                    EnsureApplied(activeProfile, presence);
                }
                pixelWas = pixelOn;
                razerWas = razerOn;
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
            }
            Thread.Sleep(PollSeconds * 1000);
        }
    }

    static void AttachParentConsole()
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

    static void RotateLog()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length <= 512 * 1024) return;
        string old = LogPath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(LogPath, old);
    }

    static void Log(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
        File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
    }

    static List<BackupRecord> LoadBackup()
    {
        List<BackupRecord> list = new List<BackupRecord>();
        if (!File.Exists(BackupPath)) return list;
        BackupRecord cur = null;
        string[] lines = File.ReadAllLines(BackupPath, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == "---")
            {
                if (cur != null && cur.Id != "") list.Add(cur);
                cur = null;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (cur == null) cur = new BackupRecord();
            string key = line.Substring(0, eq);
            string val = line.Substring(eq + 1);
            if (key == "Flow") cur.Flow = val;
            else if (key == "Id") cur.Id = val;
            else if (key == "Name") cur.Name = val;
            else if (key == "Description") cur.Description = val;
            else if (key == "FriendlyName") cur.FriendlyName = val;
        }
        return list;
    }

    static string SavedDisplay(BackupRecord rec)
    {
        if (!string.IsNullOrEmpty(rec.FriendlyName)) return rec.FriendlyName;
        if (!string.IsNullOrEmpty(rec.Description)) return rec.Name + " (" + rec.Description + ")";
        return rec.Name ?? "";
    }

    static string SavedLabel(BackupRecord rec)
    {
        string display = SavedDisplay(rec);
        string prefix = (rec.Name ?? "") + " (";
        if (display.StartsWith(prefix) && display.EndsWith(")"))
            return display.Substring(prefix.Length, display.Length - prefix.Length - 1);
        if (!string.IsNullOrEmpty(rec.Description)) return rec.Description;
        return rec.Name ?? "";
    }

    static string FullId(string flow, string id)
    {
        return (flow == "Render" ? "{0.0.0.00000000}." : "{0.0.1.00000000}.") + id;
    }

    static void RepairSavedNames()
    {
        List<AudioDevice> devices = AudioCore.ListDevices();
        DateTime now = DateTime.UtcNow;
        foreach (BackupRecord rec in LoadBackup())
        {
            if (string.IsNullOrEmpty(rec.Id)) continue;
            string fullId = FullId(rec.Flow, rec.Id);
            AudioDevice live = null;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].Id == fullId) { live = devices[i]; break; }
            }
            if (live == null) continue;
            string want = SavedDisplay(rec);
            if (string.IsNullOrEmpty(want) || live.Name == want)
            {
                NameRepairFail.Remove(fullId);
                continue;
            }
            DateTime until;
            if (NameRepairFail.TryGetValue(fullId, out until) && now < until) continue;
            string label = SavedLabel(rec);
            if (string.IsNullOrEmpty(label)) continue;
            try
            {
                AudioCore.SetInterfaceName(fullId, label);
                Log("Name restored " + rec.Flow + " " + live.Name + " -> " + want);
                NameRepairFail.Remove(fullId);
            }
            catch (Exception ex)
            {
                Log("Name restore failed " + rec.Flow + " " + rec.Id + " " + ex.Message);
                NameRepairFail[fullId] = now.AddSeconds(30);
            }
        }
    }

    static AudioDevice FindLive(List<AudioDevice> devices, string type, string[] include, string[] exclude)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            AudioDevice d = devices[i];
            if (d.Type != type) continue;
            bool ok = true;
            for (int n = 0; n < include.Length; n++)
            {
                if (!Like(d.Name, include[n])) { ok = false; break; }
            }
            if (!ok) continue;
            if (exclude != null)
            {
                for (int n = 0; n < exclude.Length; n++)
                {
                    if (Like(d.Name, exclude[n])) { ok = false; break; }
                }
            }
            if (ok) return d;
        }
        return null;
    }

    static AudioDevice FindByRecord(List<AudioDevice> devices, string type, BackupRecord rec)
    {
        if (rec == null) return null;
        string id = FullId(rec.Flow, rec.Id);
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Type == type && devices[i].Id == id) return devices[i];
        }
        string friendly = rec.Name ?? "";
        if (!string.IsNullOrEmpty(rec.Description)) friendly = rec.Name + " (" + rec.Description + ")";
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Type == type && devices[i].Name == friendly) return devices[i];
        }
        return null;
    }

    static Roles GetRoleRecords(List<BackupRecord> records)
    {
        Roles roles = new Roles();
        for (int i = 0; i < records.Count; i++)
        {
            BackupRecord rec = records[i];
            bool render = rec.Flow == "Render";
            if (render && roles.PixelPlay == null && Like(rec.Description, "*Pixel Buds*") && !Like(rec.Description, "*Hands-Free*"))
                roles.PixelPlay = rec;
            if (!render && roles.PixelRec == null && Like(rec.Description, "*Pixel Buds*"))
                roles.PixelRec = rec;
            if (render && roles.RazerPlay == null && (Eq(rec.Name, "Razer") || (Like(rec.Description, "*BlackShark*") && !Like(rec.Description, "*Chat*"))))
                roles.RazerPlay = rec;
            if (!render && roles.RazerRec == null && (Eq(rec.Name, "Razer") || Like(rec.Description, "*BlackShark*")))
                roles.RazerRec = rec;
            if (render && roles.Screen == null && (Eq(rec.Name, "Philips") || Like(rec.Description, "*Evnia*")))
                roles.Screen = rec;
        }
        return roles;
    }

    static AudioDevice ResolveRole(List<AudioDevice> devices, string type, BackupRecord rec, string[] include, string[] exclude)
    {
        AudioDevice hit = FindByRecord(devices, type, rec);
        if (hit != null) return hit;
        return FindLive(devices, type, include, exclude);
    }

    static Presence GetHeadsetPresence()
    {
        List<AudioDevice> devices = AudioCore.ListDevices();
        Roles roles = GetRoleRecords(LoadBackup());
        AudioDevice pixelRec = ResolveRole(devices, "Recording", roles.PixelRec, new[] { "*Pixel Buds*" }, new[] { "*Hands-Free*" });
        if (pixelRec == null)
            pixelRec = ResolveRole(devices, "Recording", roles.PixelRec, new[] { "*Pixel Buds*" }, null);
        AudioDevice screen = ResolveRole(devices, "Playback", roles.Screen, new[] { "*Evnia*" }, null);
        if (screen == null) screen = FindLive(devices, "Playback", new[] { "*Philips*" }, null);
        AudioDevice pixelPlay = ResolveRole(devices, "Playback", roles.PixelPlay, new[] { "*Pixel Buds*" }, new[] { "*Hands-Free*" });
        AudioDevice razerPlay = ResolveRole(devices, "Playback", roles.RazerPlay, new[] { "*BlackShark*" }, new[] { "*Chat*", "*Hands-Free*" });
        Presence presence = new Presence();
        presence.PixelPlay = pixelPlay;
        presence.PixelRec = pixelRec;
        presence.RazerPlay = razerPlay;
        presence.RazerRec = ResolveRole(devices, "Recording", roles.RazerRec, new[] { "*BlackShark*" }, null);
        presence.Screen = screen;
        presence.PixelOn = pixelPlay != null && Bluetooth.PixelBudsConnected();
        presence.RazerOn = razerPlay != null && AutoAudioSwitch.RazerDongle.GetWirelessState() == AutoAudioSwitch.RazerHeadsetState.Connected;
        return presence;
    }

    static void SetDefaultBoth(AudioDevice device)
    {
        if (device == null) return;
        AudioCore.SetDefault(device.Id, 1);
        AudioCore.SetDefault(device.Id, 2);
    }

    static void ApplyProfile(string name, Presence presence)
    {
        if (name == "Razer")
        {
            if (presence.RazerPlay == null) return;
            SetDefaultBoth(presence.RazerPlay);
            if (presence.RazerRec != null) SetDefaultBoth(presence.RazerRec);
            Log("Profile Razer | out=" + presence.RazerPlay.Name + " | mic=" + (presence.RazerRec == null ? "" : presence.RazerRec.Name));
        }
        else if (name == "Pixel")
        {
            if (presence.PixelPlay == null) return;
            SetDefaultBoth(presence.PixelPlay);
            if (presence.PixelRec != null) SetDefaultBoth(presence.PixelRec);
            Log("Profile Pixel | out=" + presence.PixelPlay.Name + " | mic=" + (presence.PixelRec == null ? "" : presence.PixelRec.Name));
        }
        else
        {
            if (presence.Screen == null) return;
            SetDefaultBoth(presence.Screen);
            Log("Profile Screen | out=" + presence.Screen.Name);
        }
        File.WriteAllText(StatePath, name, new UTF8Encoding(false));
    }

    static string ResolveStartupProfile(Presence presence)
    {
        if (presence.PixelOn) return "Pixel";
        if (presence.RazerOn) return "Razer";
        return "Screen";
    }

    static void EnsureApplied(string name, Presence presence)
    {
        AudioDevice play = AudioCore.GetDefault(0);
        AudioDevice rec = AudioCore.GetDefault(1);
        bool need = false;
        if (name == "Razer")
        {
            if (presence.RazerPlay != null && (play == null || play.Id != presence.RazerPlay.Id)) need = true;
            if (presence.RazerRec != null && (rec == null || rec.Id != presence.RazerRec.Id)) need = true;
        }
        else if (name == "Pixel")
        {
            if (presence.PixelPlay != null && (play == null || play.Id != presence.PixelPlay.Id)) need = true;
            if (presence.PixelRec != null && (rec == null || rec.Id != presence.PixelRec.Id)) need = true;
        }
        else if (presence.Screen != null && (play == null || play.Id != presence.Screen.Id))
        {
            need = true;
        }
        if (need) ApplyProfile(name, presence);
    }

    static string FormatDevice(AudioDevice device)
    {
        if (device == null) return "(absent)";
        return device.Name + " " + device.Id;
    }

    static bool Like(string text, string pattern)
    {
        string body = Regex.Escape(pattern ?? "").Replace("\\*", ".*").Replace("\\?", ".");
        return Regex.IsMatch(text ?? "", "^" + body + "$", RegexOptions.IgnoreCase);
    }

    static bool Eq(string a, string b)
    {
        return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetStdHandle(int stdHandle, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

class BackupRecord
{
    public string Flow = "";
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string FriendlyName = "";
}

class Roles
{
    public BackupRecord PixelPlay;
    public BackupRecord PixelRec;
    public BackupRecord RazerPlay;
    public BackupRecord RazerRec;
    public BackupRecord Screen;
}

class Presence
{
    public bool PixelOn;
    public bool RazerOn;
    public AudioDevice PixelPlay;
    public AudioDevice PixelRec;
    public AudioDevice RazerPlay;
    public AudioDevice RazerRec;
    public AudioDevice Screen;
}

class AudioDevice
{
    public string Name;
    public string Id;
    public string Type;
}

static class AudioCore
{
    static IntPtr Enumerator;
    static SetDefaultDelegate SetDefaultFn;
    static SetPropDelegate SetPropFn;
    static ReleaseDelegate ReleasePolicy;
    static IntPtr Policy;

    delegate int EnumEndpointsDelegate(IntPtr self, int dataFlow, uint stateMask, out IntPtr collection);
    delegate int GetDefaultDelegate(IntPtr self, int dataFlow, int role, out IntPtr device);
    delegate int GetCountDelegate(IntPtr self, out int count);
    delegate int ItemDelegate(IntPtr self, int index, out IntPtr device);
    delegate int OpenStoreDelegate(IntPtr self, uint access, out IntPtr store);
    delegate int GetIdDelegate(IntPtr self, out IntPtr id);
    delegate int GetValueDelegate(IntPtr self, ref PropertyKey key, IntPtr pv);
    delegate int ReleaseDelegate(IntPtr self);
    delegate int SetDefaultDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    delegate int SetPropDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int fx, ref PropertyKey key, IntPtr pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    public static void Initialize()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        Guid clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        Guid iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out Enumerator);
        if (hr < 0 || Enumerator == IntPtr.Zero) throw new InvalidOperationException("0x" + hr.ToString("X8"));

        Guid policyClsid = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        Guid policyIid = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
        hr = CoCreateInstance(ref policyClsid, IntPtr.Zero, 1, ref policyIid, out Policy);
        if (hr < 0 || Policy == IntPtr.Zero) throw new InvalidOperationException("0x" + hr.ToString("X8"));
        IntPtr vtable = Marshal.ReadIntPtr(Policy);
        SetPropFn = (SetPropDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 12 * IntPtr.Size), typeof(SetPropDelegate));
        SetDefaultFn = (SetDefaultDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size), typeof(SetDefaultDelegate));
        ReleasePolicy = (ReleaseDelegate)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size), typeof(ReleaseDelegate));
    }

    public static List<AudioDevice> ListDevices()
    {
        List<AudioDevice> list = new List<AudioDevice>();
        Collect(list, 0, "Playback");
        Collect(list, 1, "Recording");
        return list;
    }

    static void Collect(List<AudioDevice> list, int flow, string type)
    {
        IntPtr collection;
        EnumEndpointsDelegate enumFn = Vtable<EnumEndpointsDelegate>(Enumerator, 3);
        int hr = enumFn(Enumerator, flow, 1, out collection);
        if (hr < 0 || collection == IntPtr.Zero) return;
        try
        {
            int count;
            GetCountDelegate countFn = Vtable<GetCountDelegate>(collection, 3);
            if (countFn(collection, out count) < 0) return;
            ItemDelegate itemFn = Vtable<ItemDelegate>(collection, 4);
            for (int i = 0; i < count; i++)
            {
                IntPtr device;
                if (itemFn(collection, i, out device) < 0 || device == IntPtr.Zero) continue;
                try
                {
                    AudioDevice parsed = ReadDevice(device, type);
                    if (parsed != null) list.Add(parsed);
                }
                finally { Release(device); }
            }
        }
        finally { Release(collection); }
    }

    public static AudioDevice GetDefault(int flow)
    {
        IntPtr device;
        GetDefaultDelegate getFn = Vtable<GetDefaultDelegate>(Enumerator, 4);
        int hr = getFn(Enumerator, flow, 1, out device);
        if (hr < 0 || device == IntPtr.Zero) return null;
        try { return ReadDevice(device, flow == 0 ? "Playback" : "Recording"); }
        finally { Release(device); }
    }

    public static void SetDefault(string deviceId, int role)
    {
        int hr = SetDefaultFn(Policy, deviceId, role);
        if (hr < 0) throw new InvalidOperationException("0x" + hr.ToString("X8"));
    }

    public static void SetInterfaceName(string deviceId, string value)
    {
        PropertyKey key = new PropertyKey("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);
        IntPtr pv = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 31);
            Marshal.WriteIntPtr(pv, 8, Marshal.StringToHGlobalUni(value));
            int hr = SetPropFn(Policy, deviceId, 0, ref key, pv);
            IntPtr s = Marshal.ReadIntPtr(pv, 8);
            if (s != IntPtr.Zero) Marshal.FreeHGlobal(s);
            if (hr < 0) throw new InvalidOperationException("0x" + hr.ToString("X8"));
        }
        finally { Marshal.FreeHGlobal(pv); }
    }

    static AudioDevice ReadDevice(IntPtr device, string type)
    {
        IntPtr idPtr;
        GetIdDelegate getId = Vtable<GetIdDelegate>(device, 5);
        if (getId(device, out idPtr) < 0 || idPtr == IntPtr.Zero) return null;
        string id = Marshal.PtrToStringUni(idPtr);
        Marshal.FreeCoTaskMem(idPtr);
        AudioDevice parsed = new AudioDevice();
        parsed.Id = id;
        parsed.Type = type;
        parsed.Name = ReadFriendlyName(device);
        return parsed;
    }

    static string ReadFriendlyName(IntPtr device)
    {
        IntPtr store;
        OpenStoreDelegate open = Vtable<OpenStoreDelegate>(device, 4);
        if (open(device, 0, out store) < 0 || store == IntPtr.Zero) return "Unknown";
        try
        {
            PropertyKey key = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
            IntPtr pv = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                GetValueDelegate get = Vtable<GetValueDelegate>(store, 5);
                if (get(store, ref key, pv) < 0) return "Unknown";
                short vt = Marshal.ReadInt16(pv);
                if (vt != 31)
                {
                    PropVariantClear(pv);
                    return "Unknown";
                }
                IntPtr s = Marshal.ReadIntPtr(pv, 8);
                string value = s == IntPtr.Zero ? null : Marshal.PtrToStringUni(s);
                PropVariantClear(pv);
                return string.IsNullOrEmpty(value) ? "Unknown" : value;
            }
            finally { Marshal.FreeHGlobal(pv); }
        }
        finally { Release(store); }
    }

    static T Vtable<T>(IntPtr com, int slot)
    {
        IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
    }

    static void Release(IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        ReleaseDelegate rel = Vtable<ReleaseDelegate>(com, 2);
        rel(com);
    }

    [DllImport("ole32.dll")]
    static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(IntPtr pv);
}

static class Bluetooth
{
    public static bool PixelBudsConnected()
    {
        Guid bt = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        IntPtr info = SetupDiGetClassDevs(ref bt, null, IntPtr.Zero, 2);
        if (info == new IntPtr(-1)) return false;
        try
        {
            uint index = 0;
            while (true)
            {
                SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                if (!SetupDiEnumDeviceInfo(info, index++, ref data)) break;
                uint status, problem;
                if (CM_Get_DevNode_Status(out status, out problem, data.devInst, 0) != 0 || problem != 0) continue;
                string name = ReadName(info, ref data);
                if (name.IndexOf("Pixel Buds", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(info); }
        return false;
    }

    static string ReadName(IntPtr info, ref SP_DEVINFO_DATA data)
    {
        string friendly = ReadProp(info, ref data, 12);
        if (!string.IsNullOrEmpty(friendly)) return friendly;
        return ReadProp(info, ref data, 0) ?? "";
    }

    static string ReadProp(IntPtr info, ref SP_DEVINFO_DATA data, uint property)
    {
        uint regType, required;
        byte[] buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(info, ref data, property, out regType, buffer, (uint)buffer.Length, out required))
            return "";
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid classGuid;
        public uint devInst;
        public IntPtr reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr infoSet, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr infoSet, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
