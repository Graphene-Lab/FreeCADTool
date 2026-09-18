# AgentBridge Chat for FreeCAD — the chat panel that talks to a local AgentBridge
# server over its OpenAI-compatible HTTP API, and starts the MCP bridge inside THIS
# GUI instance so the agent's FreeCADTool edits the very window you are looking at
# (same-instance driving).
#
# WHERE IT LIVES, AND WHY. Nothing opens by itself: the chat is a command, offered
# from FreeCAD's own **Tools** menu and as a button in the standard **File** toolbar
# next to New/Open/Save — in every workbench, through the manipulator API FreeCAD
# provides for exactly this (see _ChatCommandManipulator). Like every FreeCAD command
# that acts on a drawing, it is **disabled while no document is open**: a chat that
# asks the agent to change a project means nothing before there is a project, and the
# start page is where the user decides which project that is.
#
# The MCP bridge follows the same rule: it starts when a document appears (not at
# startup), so nothing listens before there is something to edit and no service fails
# at boot. The bridge is what makes the agent drive the visible instance; a bridge
# started by the tool instead would be headless and invisible (see the plugin README,
# "Ordering note").
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
_COMMAND = "AgentBridge_OpenChat"


def _has_document():
    """True while FreeCAD has a document open — the gate this module shares with the
    command's IsActive, so the menu entry and the toolbar button grey out on the start
    page exactly like FreeCAD's own drawing commands."""
    try:
        return FreeCAD.ActiveDocument is not None
    except Exception:
        return False


def _start_bridge():
    # Bring up the MCP bridge in this GUI process so AgentBridge's FreeCADTool
    # connects to the instance the user sees. Skip if we already started one, or
    # if a separately-installed RobustMCPBridge workbench already has one running.
    # Quiet on purpose: a bridge that cannot start is reported by the chat itself
    # when the user asks for something, not by a notification at boot.
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
        FreeCAD.Console.PrintMessage("AgentBridge chat: bridge not started (%s).\n" % exc)


def _open_chat():
    # Only with a document, and only from the GUI: this is the same rule IsActive
    # enforces, repeated here because a caller (a macro, a test) can invoke the
    # function directly.
    if not FreeCAD.GuiUp or not _has_document():
        return
    _start_bridge()
    try:
        from chat import show_chat_dock
        show_chat_dock()
    except Exception as exc:
        FreeCAD.Console.PrintWarning("AgentBridge chat: could not open chat (%s).\n" % exc)


def _close_chat():
    """Hide the dock. Used when the last document closes: the chat exists to work on
    the open document, so it goes away with it (reopen from the menu/button)."""
    try:
        from chat import hide_chat_dock
        hide_chat_dock()
    except Exception:
        pass


class _OpenChatCommand:
    def GetResources(self):
        return {
            "MenuText": "Open AgentBridge Chat",
            "ToolTip": "Chat with the AgentBridge AI assistant (needs an open document)",
            "Pixmap": os.path.join(_mod_dir, "AgentBridgeChat.svg"),
        }

    def IsActive(self):
        # FreeCAD's own convention: no document, nothing for the agent to change, so
        # the Tools entry and the File-toolbar button are greyed out.
        return FreeCAD.GuiUp and _has_document()

    def Activated(self):
        _open_chat()


class _ChatCommandManipulator:
    """Puts the command where a FreeCAD user looks for it, in every workbench: the
    standard Tools menu, and the File toolbar next to New/Open/Save. This is the
    supported way to extend the standard menus (the same API the bundled BIM workbench
    uses); FreeCAD keeps the command's enabled state in sync with IsActive for us."""

    def modifyMenuBar(self):
        return [{"append": _COMMAND, "menuItem": "Std_DlgParameter"}]

    def modifyToolBars(self):
        return [{"append": _COMMAND, "toolBar": "File"}]


class _DocumentWatcher:
    """The bridge follows the document, not the application: start it when a document
    appears, close the chat when the last one goes away."""

    def _document_changed(self):
        if _has_document():
            _start_bridge()
        else:
            _close_chat()

    def slotCreatedDocument(self, doc):
        self._document_changed()

    def slotDeletedDocument(self, doc):
        self._document_changed()

    def slotActivateDocument(self, doc):
        self._document_changed()


# Registration. Every call is guarded: InitGui is imported by FreeCAD's loader while
# the GUI comes up, and the module must stay import-safe under a headless stub.
if hasattr(Gui, "addCommand"):
    Gui.addCommand(_COMMAND, _OpenChatCommand())
if hasattr(Gui, "addWorkbenchManipulator"):
    Gui.addWorkbenchManipulator(_ChatCommandManipulator())
if hasattr(FreeCAD, "addDocumentObserver"):
    _watcher = _DocumentWatcher()   # kept referenced: FreeCAD does not own it
    FreeCAD.addDocumentObserver(_watcher)


def _apply_to_current_workbench(tries=0):
    """The manipulator applies to the NEXT workbench activation, and this module may be
    loaded after FreeCAD already built the current one — so the command can be missing
    from the menu the user is looking at right now. Refresh that workbench, but only if
    the command really is absent (never re-layout a workbench that already shows it)."""
    if not FreeCAD.GuiUp:
        if tries < 24:
            try:
                from _qt import QtCore
                QtCore.QTimer.singleShot(500, lambda: _apply_to_current_workbench(tries + 1))
            except Exception:
                pass
        return
    try:
        from _qt import QtWidgets
        if Gui.getMainWindow().findChild(QtWidgets.QAction, _COMMAND) is not None:
            _start_bridge_if_document_open()
            return
        Gui.activeWorkbench().reloadActive()
    except Exception:
        pass
    _start_bridge_if_document_open()


def _start_bridge_if_document_open():
    # A document can already be open when this module loads (session restore, a file on
    # the command line): the observer would never hear about it, so check once here.
    if _has_document():
        _start_bridge()


try:
    from _qt import QtCore
    QtCore.QTimer.singleShot(800, lambda: _apply_to_current_workbench(0))
except Exception:
    pass
