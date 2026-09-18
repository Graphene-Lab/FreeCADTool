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

    // The discovery walks directories, and a MISS would otherwise be repeated on every tool
    // call, so the outcome is remembered for the process. FREECAD_CMD / FREECAD_HOME are read
    // before the cache, so a host that sets them still wins without restarting anything.
    private static readonly object DiscoveryGate = new();
    private static string? _discovered;
    private static bool _discoveredSet;

    /// <summary>Locate the freecadcmd executable: the environment overrides first
    /// (<c>FREECAD_CMD</c>, then <c>FREECAD_HOME</c>), then the search for this OS. Nothing
    /// refuses to work because FreeCAD lives in an unusual place — the PATH, the standard
    /// install folders and the fixed drives are all searched, and the two variables cover
    /// everything else (an archive unpacked in a deep folder of its own, a Flatpak/Snap
    /// sandbox). A negative result is logged with the hint, because that is the case a user
    /// has to act on.</summary>
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

        lock (DiscoveryGate)
        {
            if (_discoveredSet) return _discovered;
            _discovered = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? FindWindows()
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? FindMac()
                : FindLinux();
            _discoveredSet = true;
            Log.LogStep(_discovered != null
                ? $"FreeCADBootstrap: freecadcmd found at {_discovered}"
                : "FreeCADBootstrap: freecadcmd not found (looked on PATH, in the usual install folders and on the fixed drives) — set FREECAD_CMD or FREECAD_HOME to the install");
            return _discovered;
        }
    }

    private static string CmdName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "freecadcmd.exe" : "freecadcmd";

    /// <summary>The command itself, found through PATH — the one place every OS agrees on, and
    /// where a package manager (Linux) or a user who unpacked FreeCAD by hand puts it.</summary>
    private static string? OnPath(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    private static string? FindWindows()
    {
        const string name = "freecadcmd.exe";
        var onPath = OnPath(name);
        if (onPath != null) return onPath;

        var roots = new List<string>();
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
        // A per-user install ("install for me only") lands here, not in Program Files.
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(local)) roots.Add(Path.Combine(local, "Programs"));
        // The portable archives are unpacked anywhere, most often straight on a drive; every
        // fixed drive is checked, so a FreeCAD on D: or E: is found exactly like one on C:.
        try { foreach (var d in DriveInfo.GetDrives()) if (d.DriveType == DriveType.Fixed) roots.Add(d.RootDirectory.FullName); }
        catch { }

        foreach (var root in roots)
        {
            var hit = ScanRoot(root, name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Searches one root for a FreeCAD install folder and returns the command inside it:
    /// <c>&lt;root&gt;/FreeCAD*/bin/&lt;name&gt;</c> or <c>&lt;root&gt;/FreeCAD*/&lt;name&gt;</c>.
    /// Both spellings the project uses are tried, because the installers and the official
    /// archives disagree: <c>FreeCAD 1.0</c> on Windows and Fedora, <c>freecad-1.1</c> for the
    /// unpacked tarball (Windows' search is case-insensitive, so one pass covers it there).</summary>
    private static string? ScanRoot(string root, string name)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var pattern in new[] { "FreeCAD*", "freecad*" })
            {
                foreach (var dir in Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly))
                {
                    var a = Path.Combine(dir, "bin", name);
                    if (File.Exists(a)) return a;
                    var b = Path.Combine(dir, name);
                    if (File.Exists(b)) return b;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? FindLinux()
    {
        // Fedora ships FreeCADCmd, Debian/Ubuntu freecadcmd, and some packages symlink
        // freecad-cmd: all three are the same program under a different name.
        var onPath = OnPath("freecadcmd", "FreeCADCmd", "freecad-cmd");
        if (onPath != null) return onPath;

        foreach (var c in new[]
                 {
                     "/usr/bin/freecadcmd", "/usr/bin/freecad-cmd", "/usr/bin/FreeCADCmd",
                     "/usr/local/bin/freecadcmd", "/usr/local/bin/FreeCADCmd",
                     "/snap/bin/freecadcmd",
                 })
            if (File.Exists(c)) return c;

        // The layouts that keep the program outside the PATH: /opt/freecad-1.1 (the unpacked
        // official archive, including the AppImage extract) and /usr/lib/freecad* (the
        // Ubuntu PPA). A Flatpak or Snap sandbox cannot be driven from outside — the bridge
        // has to run inside that sandbox, and the plugin cannot start it — which is what
        // FREECAD_CMD / FREECAD_HOME are for when the user installed it another way.
        foreach (var root in new[] { "/opt", "/usr/lib" })
        {
            var hit = ScanRoot(root, "freecadcmd") ?? ScanRoot(root, "FreeCADCmd");
            if (hit != null) return hit;
        }
        return null;
    }

    private static string? FindMac()
    {
        var onPath = OnPath("freecadcmd");
        if (onPath != null) return onPath;

        // /Applications needs an admin install; ~/Applications is the per-user one.
        var applicationDirs = new List<string>();
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home)) applicationDirs.Add(Path.Combine(home, "Applications"));
        applicationDirs.Add("/Applications");

        foreach (var dir in applicationDirs)
        {
            foreach (var app in new[] { "FreeCAD.app", "FreeCAD 1.app", "FreeCAD 0.20.app", "FreeCAD 0.21.app" })
            {
                var c = Path.Combine(dir, app, "Contents/Resources/bin/freecadcmd");
                if (File.Exists(c)) return c;
            }
            try
            {
                foreach (var bundle in Directory.GetDirectories(dir, "FreeCAD*.app", SearchOption.TopDirectoryOnly))
                {
                    var c = Path.Combine(bundle, "Contents/Resources/bin/freecadcmd");
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
        }
        return null;
    }
}
