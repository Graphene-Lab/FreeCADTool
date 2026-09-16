using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AIOrchestrator.API;

/// <summary>
/// Locates a FreeCAD install and starts the bundled headless MCP bridge as a
/// background process, so the agent never has to start FreeCAD by hand. Mirrors
/// the AIOrchestrator <c>LocalExllamaV2Server</c> pattern: check-if-up,
/// start-if-not, wait-until-ready, thread-safe. On failure it returns a reason
/// key so the caller can show a localized OS notification.
/// </summary>
internal static class FreeCADBootstrap
{
    private static readonly object Gate = new();

    /// <summary>True when something is already listening on the bridge port.</summary>
    public static bool IsBridgeUp(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            var ar = c.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1))) return false;
            c.EndConnect(ar);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ensure the bridge is reachable, launching a headless FreeCAD if needed.
    /// Returns null on success, or a reason key:
    /// <c>"freecad_not_found"</c> | <c>"bridge_script_missing"</c> |
    /// <c>"launch_failed"</c> | <c>"timeout"</c>.
    /// </summary>
    public static string? EnsureBridge(string host, int port)
    {
        if (IsBridgeUp(host, port)) return null;
        lock (Gate)
        {
            if (IsBridgeUp(host, port)) return null;

            var script = BridgeScriptPath();
            if (script == null) return "bridge_script_missing";

            var freecad = FindFreeCAD();
            if (freecad == null) return "freecad_not_found";

            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = freecad,
                    Arguments = $"\"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(script)
                });
                p?.Dispose();
            }
            catch { return "launch_failed"; }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (IsBridgeUp(host, port)) return null;
                Thread.Sleep(1000);
            }
            return "timeout";
        }
    }

    // The bridge launcher ships next to the plugin DLL (Tools/FreeCADTool/).
    private static string? BridgeScriptPath()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(FreeCADTool).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;
            var p = Path.Combine(dir, "bridge_headless.py");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Locate the freecadcmd executable across OSes. Env override first.</summary>
    public static string? FindFreeCAD()
    {
        var cmd = Environment.GetEnvironmentVariable("FREECAD_CMD");
        if (!string.IsNullOrWhiteSpace(cmd) && File.Exists(cmd)) return cmd;
        var home = Environment.GetEnvironmentVariable("FREECAD_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var c = Path.Combine(home, CmdName());
            if (File.Exists(c)) return c;
            c = Path.Combine(home, "bin", CmdName());
            if (File.Exists(c)) return c;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return FindWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return FindMac();
        return FindLinux();
    }

    private static string CmdName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "freecadcmd.exe" : "freecadcmd";

    private static string? FindWindows()
    {
        const string name = "freecadcmd.exe";
        var roots = new List<string>();
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(pf)) roots.Add(pf);
        if (!string.IsNullOrWhiteSpace(pf86)) roots.Add(pf86);
        try { foreach (var d in DriveInfo.GetDrives()) if (d.DriveType == DriveType.Fixed) roots.Add(d.RootDirectory.FullName); }
        catch { }

        foreach (var root in roots)
        {
            var hit = ScanRoot(root, name);
            if (hit != null) return hit;
        }
        return null;
    }

    // <root>/FreeCAD*/bin/<name> or <root>/FreeCAD*/<name> (one level deep).
    private static string? ScanRoot(string root, string name)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var dir in Directory.GetDirectories(root, "FreeCAD*", SearchOption.TopDirectoryOnly))
            {
                var a = Path.Combine(dir, "bin", name);
                if (File.Exists(a)) return a;
                var b = Path.Combine(dir, name);
                if (File.Exists(b)) return b;
            }
        }
        catch { }
        return null;
    }

    private static string? FindLinux()
    {
        foreach (var c in new[] { "/usr/bin/freecadcmd", "/usr/bin/freecad-cmd", "/usr/local/bin/freecadcmd" })
            if (File.Exists(c)) return c;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var c = Path.Combine(d, "freecadcmd");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static string? FindMac()
    {
        foreach (var app in new[] { "/Applications/FreeCAD.app", "/Applications/FreeCAD 1.app", "/Applications/FreeCAD 0.20.app" })
        {
            var c = Path.Combine(app, "Contents/Resources/bin/freecadcmd");
            if (File.Exists(c)) return c;
        }
        try
        {
            foreach (var dir in Directory.GetDirectories("/Applications", "FreeCAD*", SearchOption.TopDirectoryOnly))
            {
                var c = Path.Combine(dir, "Contents/Resources/bin/freecadcmd");
                if (File.Exists(c)) return c;
            }
        }
        catch { }
        return null;
    }
}
