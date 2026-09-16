using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AIOrchestrator.API;

public partial class FreeCADTool
{
    // 0 = never started, 1 = installing, 2 = installed, 3 = failed.
    private static int _chatInstallState;

    /// <summary>Install the AgentBridge chat workbench into the user's FreeCAD Mod
    /// dir, one-shot and in the background, so the chat dock appears automatically
    /// the next time the FreeCAD GUI starts. Honors FREECAD_DISABLE_AUTOINSTALL=1
    /// (used by the harness) to skip the real Mod-dir write.</summary>
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
    }
}
