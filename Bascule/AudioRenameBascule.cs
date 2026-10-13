using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

[assembly: AssemblyTitle("AudioRename")]
[assembly: AssemblyProduct("AudioRename")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyVersion("1.1.0.0")]

static class Program
{
    static void Main()
    {
        string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        string root = Path.GetDirectoryName(dir);
        string script = Path.Combine(root, "AutoAudioSwitch.ps1");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo();
        start.FileName = powershell;
        start.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"";
        start.WorkingDirectory = root;
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        Process.Start(start);
    }
}
