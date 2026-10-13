using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AudioRename")]
[assembly: AssemblyProduct("AudioRename")]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace AudioRename
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    sealed class AudioDevice
    {
        public string Flow;
        public string Id;
        public string Name;
        public string Description;
        public int State;

        public string FlowLabel
        {
            get { return Flow == "Render" ? "Lecture" : "Enregistrement"; }
        }

        public string EndpointId
        {
            get
            {
                string prefix = Flow == "Render" ? "{0.0.0.00000000}." : "{0.0.1.00000000}.";
                return prefix + Id;
            }
        }

        public string StateLabel
        {
            get
            {
                int low = State & 0xF;
                bool disabled = (low & 2) != 0 || (State & 0x10000000) != 0;
                var parts = new List<string>();
                if ((low & 1) != 0 && !disabled) parts.Add("Actif");
                if (disabled) parts.Add("Désactivé");
                if ((low & 8) != 0) parts.Add("Déconnecté");
                if (parts.Count == 0) parts.Add("Inconnu");
                return string.Join(", ", parts.ToArray());
            }
        }

        public bool IsHidden
        {
            get { return (State & 0x10000000) != 0 || (State & 0x20000000) != 0; }
        }

        public bool IsActiveGroup
        {
            get { return (State & 1) != 0 && !IsHidden; }
        }

        public bool IsDisabledGroup
        {
            get { return (State & 2) != 0 || (State & 0x10000000) != 0; }
        }

        public bool IsDisconnectedGroup
        {
            get { return (State & 8) != 0; }
        }

        public bool IsInactive
        {
            get { return !IsActiveGroup; }
        }
    }

    static class AudioRegistry
    {
        const string NameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        const string DescriptionValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        const string DescriptionAliasValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},26";
        const string InterfaceNameValue = "{026e516e-b814-414b-83cd-856d6fef4822},2";

        public static List<AudioDevice> LoadAll()
        {
            var list = new List<AudioDevice>();
            LoadFlow(list, "Render");
            LoadFlow(list, "Capture");
            list.Sort(delegate(AudioDevice a, AudioDevice b)
            {
                if (a.Flow != b.Flow) return a.Flow == "Render" ? -1 : 1;
                int active = a.IsInactive.CompareTo(b.IsInactive);
                if (active != 0) return active;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        static void LoadFlow(List<AudioDevice> list, string flow)
        {
            string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" + flow;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(root))
            {
                if (key == null) return;
                foreach (string id in key.GetSubKeyNames())
                {
                    using (RegistryKey device = key.OpenSubKey(id))
                    using (RegistryKey props = device == null ? null : device.OpenSubKey("Properties"))
                    {
                        if (device == null || props == null) continue;
                        object stateObj = device.GetValue("DeviceState");
                        int state = stateObj is int ? (int)stateObj : 0;
                        string name = props.GetValue(NameValue) as string ?? "";
                        string storedDescription = props.GetValue(DescriptionValue) as string ?? "";
                        string published = ReadPublishedName(flow, id);
                        string description = DescriptionFromPublished(name, published, storedDescription);
                        list.Add(new AudioDevice
                        {
                            Flow = flow,
                            Id = id,
                            Name = name,
                            Description = description,
                            State = state
                        });
                    }
                }
            }
        }

        static string ReadPublishedName(string flow, string id)
        {
            string path = @"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\" + EndpointId(flow, id);
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
            {
                if (key == null) return "";
                return key.GetValue("FriendlyName") as string ?? "";
            }
        }

        static string DescriptionFromPublished(string name, string published, string stored)
        {
            string prefix = name + " (";
            if (!string.IsNullOrEmpty(name) &&
                published.StartsWith(prefix, StringComparison.Ordinal) &&
                published.EndsWith(")", StringComparison.Ordinal) &&
                published.Length > prefix.Length + 1)
            {
                return published.Substring(prefix.Length, published.Length - prefix.Length - 1);
            }
            return stored;
        }

        static string EndpointId(string flow, string id)
        {
            string prefix = flow == "Render" ? "{0.0.0.00000000}." : "{0.0.1.00000000}.";
            return prefix + id;
        }

        public static void Rename(AudioDevice device, string name, string description)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Le nom est vide.");

            name = name.Trim();
            description = description == null ? "" : description.Trim();

            Privilege.Enable("SeTakeOwnershipPrivilege");
            Privilege.Enable("SeRestorePrivilege");
            Privilege.Enable("SeBackupPrivilege");

            string sub = PropertiesPath(device.Flow, device.Id);
            WriteProtectedValues(sub, name, description);
            WritePublishedName(device, name, description);
            RestartAudio();
            ApplyMmsysName(device.Flow, device.Id, name, description);
        }

        public static DeviceBackup ReadSnapshot(AudioDevice device)
        {
            var backup = new DeviceBackup();
            backup.Flow = device.Flow;
            backup.Id = device.Id;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PropertiesPath(device.Flow, device.Id)))
            {
                if (key == null) throw new InvalidOperationException("Clé audio introuvable.");
                backup.Name = key.GetValue(NameValue) as string ?? "";
                backup.Description = key.GetValue(DescriptionValue) as string ?? "";
                object alias = key.GetValue(DescriptionAliasValue);
                backup.HasAlias = alias is string;
                backup.Alias = alias as string ?? "";
                object interfaceName = key.GetValue(InterfaceNameValue);
                backup.HasInterface = interfaceName is string;
                backup.InterfaceName = interfaceName as string ?? "";
            }
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PublishedPath(device)))
            {
                if (key != null)
                {
                    object friendly = key.GetValue("FriendlyName");
                    backup.HasFriendly = friendly is string;
                    backup.FriendlyName = friendly as string ?? "";
                    object desc = key.GetValue("DeviceDesc");
                    backup.HasDeviceDesc = desc is string;
                    backup.DeviceDesc = desc as string ?? "";
                }
            }
            return backup;
        }

        public static void Restore(DeviceBackup backup)
        {
            Privilege.Enable("SeTakeOwnershipPrivilege");
            Privilege.Enable("SeRestorePrivilege");
            Privilege.Enable("SeBackupPrivilege");
            WriteProperties(PropertiesPath(backup.Flow, backup.Id), delegate(RegistryKey key)
            {
                key.SetValue(NameValue, backup.Name ?? "", RegistryValueKind.String);
                key.SetValue(DescriptionValue, backup.Description ?? "", RegistryValueKind.String);
                if (backup.HasAlias)
                    key.SetValue(DescriptionAliasValue, backup.Alias ?? "", RegistryValueKind.String);
                if (backup.HasInterface)
                    key.SetValue(InterfaceNameValue, backup.InterfaceName ?? "", RegistryValueKind.String);
            });
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PublishedPath(backup.Flow, backup.Id), true))
            {
                if (key != null)
                {
                    if (backup.HasFriendly)
                        key.SetValue("FriendlyName", backup.FriendlyName ?? "", RegistryValueKind.String);
                    if (backup.HasDeviceDesc)
                        key.SetValue("DeviceDesc", backup.DeviceDesc ?? "", RegistryValueKind.String);
                }
            }
            RestartAudio();
            ApplyMmsysName(backup.Flow, backup.Id, backup.Name, backup.Description);
        }

        static string QuotePs(string value)
        {
            if (value == null) value = "";
            return value.Replace("\"", "").Replace("'", "''");
        }

        static void ApplyMmsysName(string flow, string id, string name, string description)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Restore-AudioNames.ps1");
            string command = "& '" + QuotePs(script) + "' -MmsysOnly -Flow '" + QuotePs(flow) + "' -Id '" + QuotePs(id) + "' -Name '" + QuotePs(name) + "' -Description '" + QuotePs(description) + "'";
            string args = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"";
            var start = new ProcessStartInfo();
            start.FileName = "powershell.exe";
            start.Arguments = args;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            Process process = Process.Start(start);
            if (process == null || !process.WaitForExit(30000) || process.ExitCode != 0)
                throw new InvalidOperationException("Le panneau Son n'a pas été mis à jour.");
        }

        static string PropertiesPath(string flow, string id)
        {
            return @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" + flow + @"\" + id + @"\Properties";
        }

        static string PublishedPath(AudioDevice device)
        {
            return PublishedPath(device.Flow, device.Id);
        }

        static string PublishedPath(string flow, string id)
        {
            string prefix = flow == "Render" ? "{0.0.0.00000000}." : "{0.0.1.00000000}.";
            return @"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\" + prefix + id;
        }

        static void WriteProtectedValues(string sub, string name, string description)
        {
            WriteProperties(sub, delegate(RegistryKey key)
            {
                key.SetValue(NameValue, name, RegistryValueKind.String);
                key.SetValue(DescriptionValue, description, RegistryValueKind.String);
                if (key.GetValue(DescriptionAliasValue) is string)
                    key.SetValue(DescriptionAliasValue, description, RegistryValueKind.String);
                if (key.GetValue(InterfaceNameValue) is string)
                    key.SetValue(InterfaceNameValue, description, RegistryValueKind.String);
            });
        }

        static void WriteProperties(string sub, Action<RegistryKey> write)
        {
            RegistryKey hive = Registry.LocalMachine;
            RegistryRights take = RegistryRights.TakeOwnership | RegistryRights.ReadPermissions;
            RegistryKey key = hive.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, take);
            if (key == null) throw new InvalidOperationException("Clé audio introuvable.");

            string oldSddl;
            using (key)
            {
                RegistrySecurity acl = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access | AccessControlSections.Group);
                oldSddl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.All);
                acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                key.SetAccessControl(acl);
            }

            RegistryRights change = RegistryRights.ChangePermissions | RegistryRights.ReadPermissions;
            using (key = hive.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, change))
            {
                RegistrySecurity acl = key.GetAccessControl(AccessControlSections.Access);
                acl.AddAccessRule(new RegistryAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    RegistryRights.FullControl,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                key.SetAccessControl(acl);
            }

            using (key = hive.OpenSubKey(sub, true))
            {
                write(key);
            }

            using (key = hive.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, change))
            {
                RegistrySecurity acl = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access | AccessControlSections.Group);
                acl.SetSecurityDescriptorSddlForm(oldSddl);
                key.SetAccessControl(acl);
            }
        }

        static void WritePublishedName(AudioDevice device, string name, string description)
        {
            string published = string.IsNullOrEmpty(description) ? name : name + " (" + description + ")";
            string path = @"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\" + device.EndpointId;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path, true))
            {
                if (key == null) return;
                key.SetValue("FriendlyName", published, RegistryValueKind.String);
                object desc = key.GetValue("DeviceDesc");
                if (desc is string && ((string)desc).Length > 0 && ((string)desc)[0] != '@')
                    key.SetValue("DeviceDesc", published, RegistryValueKind.String);
            }
        }

        static void RestartAudio()
        {
            using (var audio = new ServiceController("Audiosrv"))
            {
                var stopped = new List<ServiceController>();
                foreach (ServiceController dep in audio.DependentServices)
                {
                    if (dep.ServiceName.StartsWith("AarSvc", StringComparison.OrdinalIgnoreCase))
                        continue;
                    dep.Refresh();
                    if (dep.Status == ServiceControllerStatus.Running || dep.Status == ServiceControllerStatus.StartPending)
                    {
                        dep.Stop();
                        dep.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                        stopped.Add(dep);
                    }
                }
                audio.Refresh();
                if (audio.Status != ServiceControllerStatus.Stopped)
                {
                    audio.Stop();
                    audio.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
                audio.Start();
                audio.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                foreach (ServiceController dep in stopped)
                {
                    try
                    {
                        dep.Refresh();
                        if (dep.Status == ServiceControllerStatus.Stopped)
                            dep.Start();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }
    }

    static class Privilege
    {
        public static void Enable(string name)
        {
            Native.Enable(name);
        }

        static class Native
        {
            [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
            static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);

            [DllImport("advapi32.dll", SetLastError = true)]
            static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, int length, IntPtr previous, IntPtr returned);

            [StructLayout(LayoutKind.Sequential)]
            struct Luid
            {
                public uint LowPart;
                public int HighPart;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct LuidAndAttributes
            {
                public Luid Luid;
                public uint Attributes;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct TokenPrivileges
            {
                public uint PrivilegeCount;
                public LuidAndAttributes Privileges;
            }

            public static void Enable(string name)
            {
                IntPtr token;
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out token))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Luid luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                TokenPrivileges privileges = new TokenPrivileges();
                privileges.PrivilegeCount = 1;
                privileges.Privileges.Luid = luid;
                privileges.Privileges.Attributes = 2;
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new System.ComponentModel.Win32Exception(error);
            }
        }
    }

    sealed class DeviceList : UserControl
    {
        const int HeaderHeight = 28;
        const int RowHeight = 26;
        readonly List<AudioDevice> items = new List<AudioDevice>();
        readonly VScrollBar bar = new VScrollBar();
        int selected = -1;

        public event EventHandler SelectionChanged;

        public DeviceList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = SystemColors.Window;
            bar.Dock = DockStyle.Right;
            bar.Scroll += delegate { Invalidate(); };
            Controls.Add(bar);
            MouseWheel += delegate(object sender, MouseEventArgs e)
            {
                int step = e.Delta > 0 ? -3 : 3;
                SetScroll(bar.Value + step);
            };
        }

        public AudioDevice SelectedDevice
        {
            get { return selected >= 0 && selected < items.Count ? items[selected] : null; }
        }

        public void SetItems(IList<AudioDevice> devices, string keepId)
        {
            items.Clear();
            items.AddRange(devices);
            selected = items.Count == 0 ? -1 : 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (keepId != null && keepId == items[i].Flow + items[i].Id)
                    selected = i;
            }
            UpdateScroll();
            EnsureVisible();
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScroll();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Y < HeaderHeight) return;
            int index = bar.Value + (e.Y - HeaderHeight) / RowHeight;
            if (index < 0 || index >= items.Count) return;
            selected = index;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int width = Math.Max(0, ClientSize.Width - bar.Width);
            int nameWidth = width * 34 / 100;
            int stateWidth = 150;
            if (nameWidth + stateWidth > width - 80) stateWidth = Math.Max(80, width / 5);
            int descriptionWidth = Math.Max(0, width - nameWidth - stateWidth);
            Graphics g = e.Graphics;
            g.Clear(SystemColors.Window);
            g.FillRectangle(SystemBrushes.Control, 0, 0, width, HeaderHeight);
            DrawHeader(g, "Nom", 0, nameWidth);
            DrawHeader(g, "Description", nameWidth, descriptionWidth);
            DrawHeader(g, "État", nameWidth + descriptionWidth, stateWidth);
            g.DrawLine(SystemPens.ControlDark, 0, HeaderHeight - 1, width, HeaderHeight - 1);

            int first = bar.Value;
            int y = HeaderHeight;
            for (int i = first; i < items.Count && y < ClientSize.Height; i++)
            {
                AudioDevice device = items[i];
                bool isSelected = i == selected;
                if (isSelected)
                    g.FillRectangle(SystemBrushes.Highlight, 0, y, width, RowHeight);
                Brush brush = isSelected
                    ? SystemBrushes.HighlightText
                    : (device.IsInactive ? SystemBrushes.GrayText : SystemBrushes.ControlText);
                DrawCell(g, device.Name, 6, y, nameWidth - 10, brush);
                DrawCell(g, device.Description, nameWidth + 6, y, descriptionWidth - 10, brush);
                DrawCell(g, device.StateLabel, nameWidth + descriptionWidth + 6, y, stateWidth - 10, brush);
                y += RowHeight;
            }
        }

        static void DrawHeader(Graphics g, string text, int x, int width)
        {
            var rect = new Rectangle(x + 6, 0, Math.Max(0, width - 8), HeaderHeight);
            TextRenderer.DrawText(g, text, SystemFonts.MessageBoxFont, rect, SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            g.DrawLine(SystemPens.ControlDark, x + width - 1, 4, x + width - 1, HeaderHeight - 5);
        }

        static void DrawCell(Graphics g, string text, int x, int y, int width, Brush brush)
        {
            if (width <= 0) return;
            var rect = new Rectangle(x, y, width, RowHeight);
            TextRenderer.DrawText(g, text ?? "", SystemFonts.MessageBoxFont, rect, ((SolidBrush)brush).Color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void UpdateScroll()
        {
            int visible = Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);
            int max = Math.Max(0, items.Count - visible);
            bar.LargeChange = visible;
            bar.SmallChange = 1;
            bar.Maximum = max + visible - 1;
            if (bar.Value > max) bar.Value = max;
            bar.Enabled = max > 0;
        }

        void SetScroll(int value)
        {
            int visible = Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);
            int max = Math.Max(0, items.Count - visible);
            if (value < 0) value = 0;
            if (value > max) value = max;
            bar.Value = value;
            Invalidate();
        }

        void EnsureVisible()
        {
            if (selected < 0) return;
            int visible = Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);
            if (selected < bar.Value) bar.Value = selected;
            else if (selected >= bar.Value + visible) bar.Value = selected - visible + 1;
        }
    }

    sealed class DeviceBackup
    {
        public string Flow;
        public string Id;
        public string Name;
        public string Description;
        public bool HasAlias;
        public string Alias;
        public bool HasInterface;
        public string InterfaceName;
        public bool HasFriendly;
        public string FriendlyName;
        public bool HasDeviceDesc;
        public string DeviceDesc;
    }

    static class BackupStore
    {
        static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudioRename.backup.txt"); }
        }

        public static bool Exists(AudioDevice device)
        {
            return Find(device) != null;
        }

        public static DeviceBackup Find(AudioDevice device)
        {
            foreach (DeviceBackup item in Load())
            {
                if (item.Flow == device.Flow && item.Id == device.Id) return item;
            }
            return null;
        }

        public static void Save(DeviceBackup backup)
        {
            List<DeviceBackup> all = Load();
            bool replaced = false;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Flow == backup.Flow && all[i].Id == backup.Id)
                {
                    all[i] = backup;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) all.Add(backup);
            var lines = new List<string>();
            foreach (DeviceBackup item in all)
            {
                lines.Add("Flow=" + Escape(item.Flow));
                lines.Add("Id=" + Escape(item.Id));
                lines.Add("Name=" + Escape(item.Name));
                lines.Add("Description=" + Escape(item.Description));
                lines.Add("HasAlias=" + (item.HasAlias ? "1" : "0"));
                lines.Add("Alias=" + Escape(item.Alias));
                lines.Add("HasInterface=" + (item.HasInterface ? "1" : "0"));
                lines.Add("Interface=" + Escape(item.InterfaceName));
                lines.Add("HasFriendly=" + (item.HasFriendly ? "1" : "0"));
                lines.Add("FriendlyName=" + Escape(item.FriendlyName));
                lines.Add("HasDeviceDesc=" + (item.HasDeviceDesc ? "1" : "0"));
                lines.Add("DeviceDesc=" + Escape(item.DeviceDesc));
                lines.Add("---");
            }
            File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
        }

        static List<DeviceBackup> Load()
        {
            var list = new List<DeviceBackup>();
            if (!File.Exists(FilePath)) return list;
            var current = new DeviceBackup();
            foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                if (raw == "---")
                {
                    if (!string.IsNullOrEmpty(current.Id)) list.Add(current);
                    current = new DeviceBackup();
                    continue;
                }
                int eq = raw.IndexOf('=');
                if (eq < 0) continue;
                string key = raw.Substring(0, eq);
                string value = Unescape(raw.Substring(eq + 1));
                if (key == "Flow") current.Flow = value;
                else if (key == "Id") current.Id = value;
                else if (key == "Name") current.Name = value;
                else if (key == "Description") current.Description = value;
                else if (key == "HasAlias") current.HasAlias = value == "1";
                else if (key == "Alias") current.Alias = value;
                else if (key == "HasInterface") current.HasInterface = value == "1";
                else if (key == "Interface") current.InterfaceName = value;
                else if (key == "HasFriendly") current.HasFriendly = value == "1";
                else if (key == "FriendlyName") current.FriendlyName = value;
                else if (key == "HasDeviceDesc") current.HasDeviceDesc = value == "1";
                else if (key == "DeviceDesc") current.DeviceDesc = value;
            }
            return list;
        }

        static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Unescape(string value)
        {
            var text = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == '\\') { text.Append('\\'); i++; continue; }
                    if (next == 'r') { text.Append('\r'); i++; continue; }
                    if (next == 'n') { text.Append('\n'); i++; continue; }
                }
                text.Append(value[i]);
            }
            return text.ToString();
        }
    }

    sealed class MainForm : Form
    {
        readonly TabControl tabs = new TabControl();
        readonly DeviceList playbackList = new DeviceList();
        readonly DeviceList recordingList = new DeviceList();
        readonly TextBox nameBox = new TextBox();
        readonly TextBox descriptionBox = new TextBox();
        readonly CheckBox showActive = new CheckBox();
        readonly CheckBox showDisabled = new CheckBox();
        readonly CheckBox showDisconnected = new CheckBox();
        readonly Button renameButton = new Button();
        readonly Button backupButton = new Button();
        readonly Button restoreButton = new Button();
        readonly Label status = new Label();
        readonly CheckBox autoRestoreBox = new CheckBox();
        readonly CheckBox autoSwitchBox = new CheckBox();
        List<AudioDevice> devices = new List<AudioDevice>();
        bool filling;

        DeviceList ActiveList
        {
            get { return tabs.SelectedIndex == 1 ? recordingList : playbackList; }
        }

        public MainForm()
        {
            Text = "Périphériques audio";
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            Font = new Font("Segoe UI", 9f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 520);
            MinimumSize = new Size(720, 420);

            showActive.Text = "Actifs";
            showDisabled.Text = "Désactivés";
            showDisconnected.Text = "Déconnectés";
            showActive.Checked = true;
            showDisabled.Checked = false;
            showDisconnected.Checked = false;
            var filters = new Panel();
            filters.Dock = DockStyle.Top;
            filters.Height = 36;
            int filterX = 12;
            foreach (CheckBox box in new[] { showActive, showDisabled, showDisconnected })
            {
                box.AutoSize = true;
                box.CheckedChanged += delegate { FillLists(SelectedId()); };
                filters.Controls.Add(box);
                box.Location = new Point(filterX, 8);
                filterX += box.PreferredSize.Width + 18;
            }

            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 176;

            var nameLabel = new Label();
            nameLabel.Text = "Nom";
            nameLabel.AutoSize = true;
            nameLabel.Location = new Point(12, 14);
            bottom.Controls.Add(nameLabel);

            nameBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            nameBox.Location = new Point(110, 10);
            bottom.Controls.Add(nameBox);

            var descriptionLabel = new Label();
            descriptionLabel.Text = "Description";
            descriptionLabel.AutoSize = true;
            descriptionLabel.Location = new Point(12, 46);
            bottom.Controls.Add(descriptionLabel);

            descriptionBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            descriptionBox.Location = new Point(110, 42);
            bottom.Controls.Add(descriptionBox);

            renameButton.Text = "Renommer";
            renameButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            renameButton.Size = new Size(110, 28);
            renameButton.Click += delegate { RenameSelected(); };
            bottom.Controls.Add(renameButton);

            var refreshButton = new Button();
            refreshButton.Text = "Actualiser";
            refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refreshButton.Size = new Size(110, 28);
            refreshButton.Click += delegate { Reload(SelectedId()); };
            bottom.Controls.Add(refreshButton);

            backupButton.Text = "Sauvegarder";
            backupButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            backupButton.Size = new Size(110, 28);
            backupButton.Click += delegate { BackupSelected(); };
            bottom.Controls.Add(backupButton);

            restoreButton.Text = "Restaurer";
            restoreButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            restoreButton.Size = new Size(110, 28);
            restoreButton.Click += delegate { RestoreSelected(); };
            bottom.Controls.Add(restoreButton);

            status.AutoSize = false;
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Location = new Point(12, 140);
            status.Height = 22;
            bottom.Controls.Add(status);

            var versionLabel = new Label();
            versionLabel.AutoSize = false;
            versionLabel.Text = version.ToString(3);
            versionLabel.TextAlign = ContentAlignment.MiddleRight;
            versionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            versionLabel.Size = new Size(72, 22);
            versionLabel.Location = new Point(760, 140);
            bottom.Controls.Add(versionLabel);

            autoSwitchBox.Text = "Bascule automatique du son";
            autoSwitchBox.Location = new Point(12, 110);
            autoSwitchBox.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            autoSwitchBox.AutoSize = false;
            autoSwitchBox.Size = new Size(360, 24);
            autoSwitchBox.Checked = RunTool("powershell.exe", SwitchArgs("-Query"), 15000) == 0;
            autoSwitchBox.CheckedChanged += OnAutoSwitchChanged;
            bottom.Controls.Add(autoSwitchBox);

            autoRestoreBox.Text = "Restaurer la dernière sauvegarde au démarrage";
            autoRestoreBox.Location = new Point(520, 110);
            autoRestoreBox.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            autoRestoreBox.AutoSize = false;
            autoRestoreBox.Size = new Size(330, 24);
            autoRestoreBox.Checked = RunTool("schtasks.exe", "/Query /TN \"AudioRenameAutoRestore\"", 15000) == 0;
            autoRestoreBox.CheckedChanged += OnAutoRestoreChanged;
            bottom.Controls.Add(autoRestoreBox);

            bottom.Resize += delegate
            {
                int right = bottom.ClientSize.Width - 12;
                refreshButton.Location = new Point(right - refreshButton.Width, 10);
                renameButton.Location = new Point(refreshButton.Left - 8 - renameButton.Width, 10);
                restoreButton.Location = new Point(right - restoreButton.Width, 46);
                backupButton.Location = new Point(restoreButton.Left - 8 - backupButton.Width, 46);
                int fieldWidth = Math.Max(80, renameButton.Left - 16 - nameBox.Left);
                nameBox.Width = fieldWidth;
                descriptionBox.Width = fieldWidth;
                versionLabel.Location = new Point(right - versionLabel.Width, 140);
                status.Width = Math.Max(80, versionLabel.Left - 8 - status.Left);
                versionLabel.BringToFront();
                renameButton.BringToFront();
                refreshButton.BringToFront();
                backupButton.BringToFront();
                restoreButton.BringToFront();
            };

            ConfigureList(playbackList);
            ConfigureList(recordingList);
            var playbackPage = new TabPage("Lecture");
            var recordingPage = new TabPage("Enregistrement");
            playbackPage.Padding = new Padding(0);
            recordingPage.Padding = new Padding(0);
            playbackList.Dock = DockStyle.Fill;
            recordingList.Dock = DockStyle.Fill;
            playbackPage.Controls.Add(playbackList);
            recordingPage.Controls.Add(recordingList);
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(12, 4);
            tabs.TabPages.Add(playbackPage);
            tabs.TabPages.Add(recordingPage);
            tabs.SelectedIndexChanged += delegate { if (!filling) LoadSelection(); };

            Controls.Add(tabs);
            Controls.Add(filters);
            Controls.Add(bottom);
            Reload(null);
        }

        void ConfigureList(DeviceList list)
        {
            list.SelectionChanged += delegate { if (!filling) LoadSelection(); };
        }

        string SelectedId()
        {
            AudioDevice device = ActiveList.SelectedDevice;
            return device == null ? null : device.Flow + device.Id;
        }

        void Reload(string keepId)
        {
            try
            {
                devices = AudioRegistry.LoadAll();
                if (keepId != null && keepId.StartsWith("Capture")) tabs.SelectedIndex = 1;
                FillLists(keepId);
                status.Text = devices.Count + " appareils lus.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        void FillLists(string keepId)
        {
            filling = true;
            FillOne(playbackList, "Render", keepId);
            FillOne(recordingList, "Capture", keepId);
            filling = false;
            LoadSelection();
        }

        void FillOne(DeviceList list, string flow, string keepId)
        {
            var shown = new List<AudioDevice>();
            foreach (AudioDevice device in devices)
            {
                if (device.Flow == flow && MatchesFilter(device))
                    shown.Add(device);
            }
            list.SetItems(shown, keepId);
        }

        bool MatchesFilter(AudioDevice device)
        {
            if (showActive.Checked && device.IsActiveGroup) return true;
            if (showDisabled.Checked && device.IsDisabledGroup) return true;
            if (showDisconnected.Checked && device.IsDisconnectedGroup) return true;
            return false;
        }

        void LoadSelection()
        {
            AudioDevice device = ActiveList.SelectedDevice;
            if (device == null)
            {
                nameBox.Text = "";
                descriptionBox.Text = "";
                renameButton.Enabled = false;
                backupButton.Enabled = false;
                restoreButton.Enabled = false;
                return;
            }
            nameBox.Text = device.Name;
            descriptionBox.Text = device.Description;
            renameButton.Enabled = true;
            backupButton.Enabled = true;
            restoreButton.Enabled = BackupStore.Exists(device);
        }

        void RenameSelected()
        {
            AudioDevice device = ActiveList.SelectedDevice;
            if (device == null) return;
            string name = nameBox.Text.Trim();
            string description = descriptionBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Indique un nom.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            renameButton.Enabled = false;
            status.Text = "Renommage de " + device.Name + "...";
            UseWaitCursor = true;
            try
            {
                AudioRegistry.Rename(device, name, description);
                Reload(device.Flow + device.Id);
                status.Text = "Renommé : " + name + (description.Length == 0 ? "" : " (" + description + ")") + ". Le son a été coupé une seconde.";
            }
            catch (Exception ex)
            {
                status.Text = "Échec du renommage.";
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                renameButton.Enabled = true;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        void BackupSelected()
        {
            AudioDevice device = ActiveList.SelectedDevice;
            if (device == null) return;
            backupButton.Enabled = false;
            UseWaitCursor = true;
            try
            {
                BackupStore.Save(AudioRegistry.ReadSnapshot(device));
                restoreButton.Enabled = true;
                string label = device.Name + (device.Description.Length == 0 ? "" : " (" + device.Description + ")");
                status.Text = "Sauvegarde : " + label + ".";
            }
            catch (Exception ex)
            {
                status.Text = "Échec de la sauvegarde.";
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                backupButton.Enabled = ActiveList.SelectedDevice != null;
                UseWaitCursor = false;
            }
        }

        void RestoreSelected()
        {
            AudioDevice device = ActiveList.SelectedDevice;
            DeviceBackup backup = device == null ? null : BackupStore.Find(device);
            if (backup == null)
            {
                MessageBox.Show(this, "Aucune sauvegarde pour cet appareil.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string label = backup.Name + (string.IsNullOrEmpty(backup.Description) ? "" : " (" + backup.Description + ")");
            DialogResult answer = MessageBox.Show(this, "Restaurer " + label + " ?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            restoreButton.Enabled = false;
            status.Text = "Restauration de " + label + "...";
            UseWaitCursor = true;
            try
            {
                AudioRegistry.Restore(backup);
                Reload(device.Flow + device.Id);
                status.Text = "Restauré : " + label + ". Le son a été coupé une seconde.";
            }
            catch (Exception ex)
            {
                status.Text = "Échec de la restauration.";
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                restoreButton.Enabled = true;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        static string SwitchArgs(string mode)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoAudioSwitch.ps1");
            return "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" " + mode;
        }

        static int RunTool(string file, string args, int timeout)
        {
            try
            {
                var start = new ProcessStartInfo();
                start.FileName = file;
                start.Arguments = args;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                Process process = Process.Start(start);
                if (process == null || !process.WaitForExit(timeout)) return -1;
                return process.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        void OnAutoSwitchChanged(object sender, EventArgs e)
        {
            string mode = autoSwitchBox.Checked ? "-Enable" : "-Disable";
            if (RunTool("powershell.exe", SwitchArgs(mode), 20000) == 0) return;
            autoSwitchBox.CheckedChanged -= OnAutoSwitchChanged;
            autoSwitchBox.Checked = !autoSwitchBox.Checked;
            autoSwitchBox.CheckedChanged += OnAutoSwitchChanged;
            MessageBox.Show(this, "La bascule automatique n'a pas pu être modifiée.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void OnAutoRestoreChanged(object sender, EventArgs e)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Start-RestoreAudioNames.vbs");
            string args;
            if (autoRestoreBox.Checked)
            {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudioRename.backup.txt")))
                    MessageBox.Show(this, "Aucune sauvegarde pour l'instant. La restauration s'appliquera dès qu'il y en aura une.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                args = "/Create /TN \"AudioRenameAutoRestore\" /TR \"wscript.exe \\\"" + script + "\\\"\" /SC ONLOGON /DELAY 0000:15 /RL HIGHEST /F";
            }
            else
            {
                args = "/Delete /TN \"AudioRenameAutoRestore\" /F";
            }
            if (RunTool("schtasks.exe", args, 20000) == 0) return;
            autoRestoreBox.CheckedChanged -= OnAutoRestoreChanged;
            autoRestoreBox.Checked = !autoRestoreBox.Checked;
            autoRestoreBox.CheckedChanged += OnAutoRestoreChanged;
            MessageBox.Show(this, "La restauration au démarrage n'a pas pu être modifiée.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
