using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIOrchestrator.API;

public partial class FreeCADTool
{
    // FCGear master zip — the same repo the FreeCAD Addon Manager clones.
    private const string FCGearZipUrl = "https://github.com/looooo/freecad.gears/archive/refs/heads/master.zip";

    // 0 = never started, 1 = installing, 2 = installed, 3 = failed.
    private static int _gearInstallState;

    /// <summary>Start a one-shot background install of the FCGear add-on into the
    /// user's FreeCAD Mod dir, notifying the user when it finishes. Safe to call
    /// repeatedly — only the first call starts the work; later calls are no-ops.
    /// Honors <c>FREECAD_DISABLE_AUTOINSTALL=1</c> (used by the test harness) to
    /// verify the contract without a real download / Mod-dir write.</summary>
    private void EnsureFCGearBackground()
    {
        if (Environment.GetEnvironmentVariable("FREECAD_DISABLE_AUTOINSTALL") == "1") return;
        if (Interlocked.CompareExchange(ref _gearInstallState, 1, 0) != 0) return;
        SystemNotifier.Notify("FreeCADTool", FreeCADStrings.Body("GearInstalling"));
        Task.Run(() =>
        {
            try
            {
                var modDir = GetModDir();
                if (string.IsNullOrWhiteSpace(modDir)) throw new InvalidOperationException("no Mod dir");
                InstallFCGear(modDir!);
                Volatile.Write(ref _gearInstallState, 2);
                SystemNotifier.Notify("FreeCADTool", FreeCADStrings.Body("GearInstalled"));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _gearInstallState, 3);
                Log.LogStep($"FreeCADTool FCGear install failed: {ex.Message}");
                SystemNotifier.Notify("FreeCADTool", FreeCADStrings.Body("GearFailed"));
            }
        });
    }

    // Ask the running FreeCAD for its user Mod directory (where add-ons live).
    private string? GetModDir()
    {
        var r = Run("import FreeCAD, os\n_result_ = os.path.join(FreeCAD.getUserAppDataDir(), 'Mod')");
        return r.Success ? r.Result?.ToString() : null;
    }

    private static void InstallFCGear(string modDir)
    {
        Directory.CreateDirectory(modDir);
        var target = Path.Combine(modDir, "freecad.gears");
        if (Directory.Exists(Path.Combine(target, "freecad", "gears"))) return; // already installed

        var tmp = Path.Combine(Path.GetTempPath(), "fcgear_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var zip = Path.Combine(tmp, "fcgear.zip");
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(5);
                File.WriteAllBytes(zip, http.GetByteArrayAsync(FCGearZipUrl).GetAwaiter().GetResult());
            }
            ZipFile.ExtractToDirectory(zip, tmp);
            var extracted = Directory.GetDirectories(tmp, "freecad.gears-*", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.GetDirectories(tmp, "freecad.gears*", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (extracted == null || !Directory.Exists(Path.Combine(extracted, "freecad", "gears")))
                throw new InvalidOperationException("FCGear archive layout unexpected");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyDirectory(extracted, target);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(source))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}
