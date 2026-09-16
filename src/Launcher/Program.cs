using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ArtiosCadFastExport;

internal static class Launcher
{
    // Свойства заполняются из JSON; никакие исполняемые файлы локально не копируются.
    public sealed class ActiveRelease { public string release { get; set; } }
    public sealed class Session { public string runtimeDirectory { get; set; } }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = NetworkPath(AppDomain.CurrentDomain.BaseDirectory);
            string name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            string command;
            string profile = null;
            if (name.StartsWith("Begin-")) { command = "pilot-begin"; profile = name.Substring(6); }
            else if (name.StartsWith("Finish-")) { command = "pilot-finish"; profile = name.Substring(7); }
            else if (name == "Setup") command = "setup";
            else if (name == "Reset") command = "reset";
            else if (name == "Diagnostics") command = "diagnostics";
            else throw new ArgumentException("Используйте Setup.exe или группу вывода ArtiosCAD.");
            string release = Storage.Read<ActiveRelease>(Path.Combine(root, "active-release.json")).release;
            Storage.PlainFileName(release);
            string versionDir = Path.Combine(root, "releases", release);
            if (command == "pilot-finish")
            {
                // Завершаем задание той же версией, которой выполнен Begin, даже после обновления на сервере.
                var config = Storage.Read<RuntimeConfig>(Path.Combine(versionDir, "runtime.json"));
                var session = Storage.Read<Session>(Path.Combine(config.Root, "pilot-session.json"));
                string candidate = Storage.FullPath(session.runtimeDirectory ?? "");
                string releasesRoot = Storage.FullPath(Path.Combine(root, "releases")).TrimEnd('\\') + "\\";
                if (!candidate.StartsWith(releasesRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Версия задания не принадлежит этому сетевому приложению.");
                versionDir = candidate;
            }
            string arguments = Quote(command);
            if (profile != null)
            {
                Profiles.Inputs(profile);
                if (args.Length != 1) throw new ArgumentException("ArtiosCAD должен передать один путь к XML.");
                arguments += " " + Quote(profile) + " " + Quote(args[0]);
            }
            if (command == "setup") arguments += " " + Quote(root.TrimEnd('\\'));
            var start = new ProcessStartInfo(Path.Combine(versionDir, "ArtiosCAD-FE.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = versionDir };
            using (var process = Process.Start(start)) { process.WaitForExit(); return process.ExitCode; }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ArtiosCAD Fast Export — загрузчик", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static string Quote(string value)
    {
        // Правила Windows: обратные слеши перед кавычкой и в конце аргумента удваиваются.
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
    }

    private static string NetworkPath(string path)
    {
        if (path.StartsWith(@"\\")) return path;
        string drive = Path.GetPathRoot(path);
        var remote = new StringBuilder(4096); int length = remote.Capacity;
        if (drive.Length >= 2 && WNetGetConnection(drive.Substring(0, 2), remote, ref length) == 0)
            return remote.ToString().TrimEnd('\\') + "\\" + path.Substring(drive.Length);
        return path;
    }
}
