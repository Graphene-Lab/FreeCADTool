using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIOrchestrator.API;

public partial class FreeCADTool
{
    // 0 = never started, 1 = installing, 2 = installed, 3 = failed.
    private static int _chatInstallState;

    /// <summary>Install the AgentBridge chat panel into the user's FreeCAD Mod
    /// dir, one-shot and in the background, so FreeCAD offers the chat from its Tools menu and
    /// its File toolbar (enabled while a document is open) the next time the GUI starts.
    /// Entry points and behaviour: AgentBridgeChat/InitGui.py.
    /// Honors FREECAD_DISABLE_AUTOINSTALL=1 (used by the harness) to skip the real Mod-dir write.</summary>
    private void EnsureChatMod()
    {
        if (Environment.GetEnvironmentVariable("FREECAD_DISABLE_AUTOINSTALL") == "1") return;
        if (Interlocked.CompareExchange(ref _chatInstallState, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try
            {
                var modDir = GetModDir();
                if (string.IsNullOrWhiteSpace(modDir)) throw new InvalidOperationException("no Mod dir");
                InstallChatMod(modDir!);
                Volatile.Write(ref _chatInstallState, 2);
                SystemNotifier.Notify("FreeCADTool", FreeCADStrings.Body("ChatReady"));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _chatInstallState, 3);
                Log.LogStep($"FreeCADTool chat Mod install failed: {ex.Message}");
            }
        });
    }

    // Assemble the AgentBridgeChat workbench in the Mod dir: the chat files plus a
    // copy of the MCP bridge package, so the workbench is self-contained and can
    // start the bridge inside the GUI FreeCAD.
    private static void InstallChatMod(string modDir)
    {
        var pluginDir = Path.GetDirectoryName(typeof(FreeCADTool).Assembly.Location)
                    ?? throw new InvalidOperationException("no plugin dir");
        var chatSrc = Path.Combine(pluginDir, "chat_mod");
        if (!Directory.Exists(chatSrc))
            throw new InvalidOperationException("chat_mod payload not found next to the plugin");

        var target = Path.Combine(modDir, "AgentBridgeChat");
        Directory.CreateDirectory(target);
        CopyDirectory(chatSrc, target);

        var bridgeSrc = Path.Combine(pluginDir, "bridge", "RobustMCPBridge", "freecad_mcp_bridge");
        if (Directory.Exists(bridgeSrc))
            CopyDirectory(bridgeSrc, Path.Combine(target, "freecad_mcp_bridge"));

        // "Reverse start": what the panel needs to launch the host itself when the user opens the
        // chat and AgentBridge is not running — the executable that is running this installer and
        // the endpoint/tools that go with it. The panel reads this next to its own files; without
        // it (an older install, or autostart switched off) the chat simply talks to whatever is
        // already listening.
        try
        {
            var config = new Dictionary<string, object?>
            {
                ["url"] = HostEndpoint(),
                ["exe"] = Environment.ProcessPath,
                ["tools"] = "FileTool,GitTool,FreeCADTool",
                ["autostart"] = true,
            };
            File.WriteAllText(Path.Combine(target, "agentbridge.json"),
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.LogStep($"FreeCADTool: could not write agentbridge.json ({ex.Message})");
        }
    }

    // The host's chat endpoint, for the panel's config: this process' AGENTBRIDGE_URL, else the
    // one the host published at USER level (a debug AgentBridge writes it there for the tools it
    // spawns — the current process may predate its own write), else the release default.
    private static string HostEndpoint()
    {
        var url = Environment.GetEnvironmentVariable("AGENTBRIDGE_URL");
        if (string.IsNullOrWhiteSpace(url) && OperatingSystem.IsWindows())
            url = Environment.GetEnvironmentVariable("AGENTBRIDGE_URL", EnvironmentVariableTarget.User);
        return string.IsNullOrWhiteSpace(url) ? "http://localhost:5290/v1/chat/completions" : url!;
    }

    // Dev-only entry (invoked by the host at startup under DEBUG): install the chat
    // workbench into every FreeCAD Mod dir found on this machine, so opening the
    // FreeCAD GUI shows the chat without the agent having used FreeCADTool first.
    // The Mod dir is versioned on Windows (…\FreeCAD\v1-1\Mod) and flat on Linux
    // (…/FreeCAD/Mod), so both layouts are globbed. We cannot ask a running FreeCAD
    // for getUserAppDataDir here (that would require FreeCAD already up), so the
    // OS-standard paths are used directly.
    /// <summary>Install the AgentBridge chat workbench into every installed FreeCAD
    /// Mod directory. Debug-only convenience invoked by the host at startup; a no-op
    /// when <c>FREECAD_DISABLE_AUTOINSTALL=1</c>.</summary>
    public static void PreInstallChatMod()
    {
        if (Environment.GetEnvironmentVariable("FREECAD_DISABLE_AUTOINSTALL") == "1") return;
        try
        {
            // The enumeration only yields dirs whose FreeCAD base/version parent exists,
            // so InstallChatMod can safely create the Mod dir itself (FreeCAD creates
            // Mod lazily, so it may not be present yet).
            foreach (var modDir in EnumerateFreeCADModDirs())
                InstallChatMod(modDir);
            Log.LogStep("FreeCADTool chat Mod pre-installed (debug)");
        }
        catch (Exception ex)
        {
            Log.LogStep($"FreeCADTool chat Mod pre-install failed: {ex.Message}");
        }
    }

    // The FreeCAD user Mod directory is asked OF FreeCAD first (a portable archive keeps its
    // data INSIDE the install folder, which is how a chat installed under %APPDATA%\FreeCAD
    // ends up invisible), then derived from the install the tool found, and only as a last
    // resort guessed from the OS-standard locations. Duplicates are dropped.
    private static IEnumerable<string> EnumerateFreeCADModDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in CandidateModDirs())
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate!))
                yield return candidate!;
    }

    private static IEnumerable<string?> CandidateModDirs()
    {
        // 1. What FreeCAD itself reports — the one answer that fits every install kind, including
        //    a portable archive (FreeCAD 1.1.3 answers %APPDATA%\FreeCAD\v1-1 even for one).
        var userData = FreeCADBootstrap.GetUserAppDataDir();
        if (!string.IsNullOrWhiteSpace(userData))
            yield return Path.Combine(userData!, "Mod");

        // 2. The OS-standard locations, for the case where FreeCAD cannot be run at all. The
        //    install's own <root>/data/Mod is deliberately NOT used: that is FreeCAD's RESOURCE
        //    Mod dir (the bundled workbenches live there) and it is read on every start, so a
        //    second copy of this add-on in it would be loaded twice — a duplicated menu entry, a
        //    duplicated toolbar button and two bridge starts. The user Mod dir above is the one
        //    place a user add-on belongs.
        string appData;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                 "Library", "Application Support");
        else
            appData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    ".local", "share");

        var fcBase = Path.Combine(appData, "FreeCAD");
        if (!Directory.Exists(fcBase)) yield break;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // One Mod dir per installed FreeCAD version (v1-1, v0-20, …).
            foreach (var v in Directory.GetDirectories(fcBase, "v*", SearchOption.TopDirectoryOnly))
                yield return Path.Combine(v, "Mod");
        }
        else
        {
            yield return Path.Combine(fcBase, "Mod");
        }
    }
}
