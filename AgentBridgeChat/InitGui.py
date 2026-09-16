# AgentBridge Chat workbench for FreeCAD.
#
# Auto-shows a chat dock that talks to a local AgentBridge server over its
# OpenAI-compatible HTTP API, and starts the Robust MCP Bridge inside THIS GUI
# FreeCAD so the agent's FreeCADTool edits the very window you are looking at
# (same-instance driving). No workbench selection and no clicks: the dock appears
# by itself when the FreeCAD GUI starts, on Windows, Linux and macOS alike.
#
# The chat endpoint, model and tool set are overridable by environment variables:
#   AGENTBRIDGE_URL    full chat URL (default http://localhost:5290/v1/chat/completions)
#   AGENTBRIDGE_CHAT_TOOLS  comma-separated tool names (default FileTool,GitTool,FreeCADTool)
import os
import sys

_mod_dir = os.path.dirname(os.path.abspath(__file__))
if _mod_dir not in sys.path:
    sys.path.insert(0, _mod_dir)

import FreeCAD
import FreeCADGui as Gui

_bridge_started = False


def _start_bridge():
    # Bring up the MCP bridge in this GUI process so AgentBridge's FreeCADTool
    # connects to the instance the user sees. Skip if we already started one, or
    # if a separately-installed RobustMCPBridge workbench already has one running.
    global _bridge_started
    if _bridge_started:
        return
    try:
        pkg = os.path.join(_mod_dir, "freecad_mcp_bridge")
        if os.path.isdir(pkg) and pkg not in sys.path:
            sys.path.insert(0, pkg)
        from bridge_utils import get_running_plugin, register_mcp_plugin
        if get_running_plugin() is not None:
            _bridge_started = True
            return
        from server import FreecadMCPPlugin
        plugin = FreecadMCPPlugin(host="127.0.0.1", port=9876, xmlrpc_port=9875, enable_xmlrpc=True)
        plugin.start()
        register_mcp_plugin(plugin, 9875, 9876)
        _bridge_started = True
        FreeCAD.Console.PrintMessage("AgentBridge chat: MCP bridge started (this GUI instance).\n")
    except Exception as exc:
        FreeCAD.Console.PrintWarning("AgentBridge chat: bridge not started (%s).\n" % exc)


def _open_chat():
    if not FreeCAD.GuiUp:
        return
    _start_bridge()
    try:
        from chat import show_chat_dock
        show_chat_dock()
    except Exception as exc:
        FreeCAD.Console.PrintWarning("AgentBridge chat: could not open chat (%s).\n" % exc)


class AgentBridgeChatWorkbench:
    def GetResources(self):
        return {
            "MenuText": "AgentBridge Chat",
            "ToolTip": "Chat with the AgentBridge AI assistant inside FreeCAD",
        }

    def Initialize(self, name=""):
        # A manual entry point too, in case the auto-open is missed.
        self.appendMenu("AgentBridge", ["AgentBridge_OpenChat"])

    def Activate(self, name=""):
        _open_chat()


class _OpenChatCommand:
    def GetResources(self):
        return {"MenuText": "Open AgentBridge Chat", "ToolTip": "Open the AgentBridge chat panel"}

    def IsActive(self):
        return FreeCAD.GuiUp

    def Activated(self):
        _open_chat()


# InitGui is only loaded by the GUI workbench loader, but guard the registration so
# the module stays import-safe if FreeCADGui is a headless stub (no addCommand).
if hasattr(Gui, "addCommand"):
    Gui.addCommand("AgentBridge_OpenChat", _OpenChatCommand())
if hasattr(Gui, "addWorkbench"):
    Gui.addWorkbench(AgentBridgeChatWorkbench())


# Auto-open once the GUI event loop is running. InitGui is imported while the GUI
# is still coming up, so poll briefly for GuiUp instead of assuming it is ready.
def _auto_open(tries=0):
    if FreeCAD.GuiUp:
        _open_chat()
    elif tries < 24:  # up to ~12s for the GUI to come up
        try:
            from _qt import QtCore
            QtCore.QTimer.singleShot(500, lambda: _auto_open(tries + 1))
        except Exception:
            pass


try:
    from _qt import QtCore
    QtCore.QTimer.singleShot(800, lambda: _auto_open(0))
except Exception:
    pass
