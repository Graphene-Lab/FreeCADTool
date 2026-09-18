# AgentBridge chat dock for FreeCAD.
#
# A minimal OpenAI-compatible chat client that talks to a local AgentBridge server
# (POST /v1/chat/completions, Server-Sent Events streaming, multi-turn via
# session_id). The HTTP request runs on a background thread; results are pushed
# back to the GUI through Qt signals, so the FreeCAD UI never blocks.
import json
import os
import socket
import threading
import urllib.error
import urllib.request

from _qt import QtCore, QtWidgets

DEFAULT_URL = "http://localhost:5290/v1/chat/completions"
DEFAULT_TOOLS = ["FileTool", "GitTool", "FreeCADTool"]

# Idle timeout for the chat stream, in seconds. This is a watchdog on SILENCE, not a limit on
# the job: AgentBridge sends a keepalive comment every 15 s while the agent works, so a long
# task (a podcast, a CAD build — tens of minutes) never trips it, while a genuinely dead
# connection is reported in two minutes. Override with "timeout" in agentbridge.json or the
# AGENTBRIDGE_CHAT_TIMEOUT environment variable.
DEFAULT_IDLE_TIMEOUT = 120


def idle_timeout():
    raw = _host_config().get("timeout") or os.environ.get("AGENTBRIDGE_CHAT_TIMEOUT", "")
    try:
        value = int(str(raw).strip())
    except (TypeError, ValueError):
        return DEFAULT_IDLE_TIMEOUT
    return value if value >= 30 else DEFAULT_IDLE_TIMEOUT

# AgentBridge's HTTP server, by build kind: a release host serves the configured default
# (5290), a debug build started from the IDE serves 5291 (the dev shift in the host, see
# AgentBridge docs-dev/ARCHITECTURE.md). The panel tries both, so the chat works against
# whichever host is running without anyone editing a config.
HOST_PORTS = (5290, 5291)

_CONFIG_NAME = "agentbridge.json"
_resolved_url = None    # last endpoint that answered /health


def _host_config():
    """What the installer wrote next to this file (endpoint, executable, tool set). It is the
    persisted half of the endpoint decision: a debug AgentBridge publishes AGENTBRIDGE_URL for
    this session, and the installer records it here for the sessions where the environment was
    lost (a FreeCAD started from a shell that predates the host)."""
    try:
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), _CONFIG_NAME),
                  "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {}


def candidate_urls():
    """Endpoints to try, in order: the live session's endpoint (AGENTBRIDGE_URL, which a debug
    host publishes so a manually launched FreeCAD inherits it), then the endpoint the installer
    recorded, then both standard AgentBridge ports."""
    urls = []
    for url in (os.environ.get("AGENTBRIDGE_URL"), _host_config().get("url"), DEFAULT_URL):
        if url and url not in urls:
            urls.append(url)
    for port in HOST_PORTS:
        url = "http://localhost:%d/v1/chat/completions" % port
        if url not in urls:
            urls.append(url)
    return urls


def probe_host(timeout=1.5):
    """The first candidate endpoint that answers /health, or None. The answer is remembered, so
    the menu check and the panel itself agree on one endpoint."""
    global _resolved_url
    for url in candidate_urls():
        base = url.split("/v1/")[0].rstrip("/")
        try:
            with urllib.request.urlopen(base + "/health", timeout=timeout) as resp:
                if 200 <= resp.status < 300:
                    _resolved_url = url
                    return url
        except Exception:
            continue
    return None


def chat_url():
    """Endpoint the panel connects to: the endpoint that answered last, else the configured one."""
    return _resolved_url or candidate_urls()[0]


def _chat_tools():
    raw = _host_config().get("tools") or os.environ.get("AGENTBRIDGE_CHAT_TOOLS", "")
    if raw.strip():
        return [t.strip() for t in raw.split(",") if t.strip()]
    return list(DEFAULT_TOOLS)


class ChatClient(QtCore.QObject):
    """Runs one streaming chat request off the GUI thread and reports progress."""

    chunk = QtCore.Signal(str)   # a content delta from the agent
    done = QtCore.Signal()
    error = QtCore.Signal(str)

    def __init__(self, url):
        super().__init__()
        self.url = url
        self.session_id = None
        self._busy = False

    @property
    def busy(self):
        return self._busy

    def send(self, messages):
        if self._busy:
            return
        self._busy = True
        threading.Thread(target=self._worker, args=(list(messages),), daemon=True).start()

    def _worker(self, messages):
        body = {"model": "default-agent", "messages": messages, "stream": True,
               "tools": _chat_tools()}
        if self.session_id:
            body["session_id"] = self.session_id
        try:
            req = urllib.request.Request(
                self.url,
                data=json.dumps(body).encode("utf-8"),
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            with urllib.request.urlopen(req, timeout=idle_timeout()) as resp:
                for raw in resp:
                    line = raw.decode("utf-8", "replace").rstrip("\r\n")
                    if not line.startswith("data: "):
                        continue
                    data = line[len("data: "):]
                    if data == "[DONE]":
                        break
                    try:
                        obj = json.loads(data)
                    except ValueError:
                        continue
                    sid = obj.get("session_id")
                    if isinstance(sid, str) and sid:
                        self.session_id = sid
                    try:
                        delta = obj["choices"][0].get("delta", {})
                    except (KeyError, IndexError, TypeError):
                        continue
                    content = delta.get("content")
                    if content:
                        self.chunk.emit(content)
            self.done.emit()
        except urllib.error.HTTPError as exc:
            self.error.emit("HTTP %s: %s" % (exc.code, _read_err(exc)))
        except urllib.error.URLError as exc:
            self.error.emit("Cannot reach AgentBridge at %s (%s). Is it running?" % (self.url, exc.reason))
        except (socket.timeout, TimeoutError):
            # Silence, not a slow job: the host sends a keepalive every 15 s while the agent
            # works, so nothing arriving for idle_timeout() seconds means the connection is dead.
            self.error.emit("No answer from AgentBridge for %d s — the connection looks dead. "
                          "The task may still be running; check the AgentBridge window or log."
                          % idle_timeout())
        except Exception as exc:  # noqa: BLE001 — surface anything to the user
            self.error.emit(str(exc))
        finally:
            self._busy = False


def _read_err(http_error):
    try:
        return http_error.read().decode("utf-8", "replace")[:400]
    except Exception:
        return http_error.reason or ""


class ChatWidget(QtWidgets.QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._history = []          # list of {"role","content"}
        self._streaming = ""        # in-flight assistant text
        self._client = ChatClient(chat_url())
        self._build_ui()
        self._client.chunk.connect(self._on_chunk)
        self._client.done.connect(self._on_done)
        self._client.error.connect(self._on_error)

    def _build_ui(self):
        layout = QtWidgets.QVBoxLayout(self)
        layout.setContentsMargins(6, 6, 6, 6)
        layout.setSpacing(6)

        self.log = QtWidgets.QTextEdit(self)
        self.log.setReadOnly(True)
        self.log.setPlaceholderText("Chat with the AgentBridge agent. Ask it to build or edit CAD here.")
        layout.addWidget(self.log, 1)

        row = QtWidgets.QHBoxLayout()
        self.input = QtWidgets.QLineEdit(self)
        self.input.setPlaceholderText("Type a prompt, press Enter to send…")
        self.input.returnPressed.connect(self._on_send)
        self.send_btn = QtWidgets.QPushButton("Send", self)
        self.send_btn.clicked.connect(self._on_send)
        row.addWidget(self.input, 1)
        row.addWidget(self.send_btn)
        layout.addLayout(row)

        self.status = QtWidgets.QLabel("", self)
        self.status.setStyleSheet("color: gray;")
        layout.addWidget(self.status)

    def _render(self):
        parts = []
        for m in self._history:
            who = "You" if m["role"] == "user" else "Agent"
            parts.append("%s: %s" % (who, m["content"]))
        if self._streaming:
            parts.append("Agent: %s" % self._streaming)
        self.log.setPlainText("\n\n".join(parts))
        sb = self.log.verticalScrollBar()
        sb.setValue(sb.maximum())

    def _on_send(self):
        text = self.input.text().strip()
        if not text or self._client.busy:
            return
        self._history.append({"role": "user", "content": text})
        self.input.clear()
        self._streaming = ""
        self._render()
        self.send_btn.setEnabled(False)
        self.status.setText("Thinking…")
        self._client.send(self._history)

    def _on_chunk(self, delta):
        self._streaming += delta
        self._render()

    def _on_done(self):
        if self._streaming:
            self._history.append({"role": "assistant", "content": self._streaming})
        self._streaming = ""
        self.send_btn.setEnabled(True)
        self.status.setText("")
        self._render()

    def _on_error(self, msg):
        self._streaming = ""
        self._history.append({"role": "assistant", "content": "⚠ %s" % msg})
        self.send_btn.setEnabled(True)
        self.status.setText("Error")
        self._render()


_dock = None     # the single chat dock, kept across open/close so the conversation survives


def _main_window():
    """FreeCAD's main window — the QMainWindow a dock must be added to.

    This is NOT `QApplication.instance()`: that is the application object, which does not have
    addDockWidget, so the dock was never created and the chat silently never appeared."""
    try:
        import FreeCADGui
        return FreeCADGui.getMainWindow()
    except Exception:
        return None


def show_chat_dock():
    """Add the AgentBridge chat dock to the FreeCAD main window (once), or reveal it again."""
    global _dock
    mw = _main_window()
    if mw is None:
        return
    if _dock is not None:
        _dock.show()
        _dock.raise_()
        return
    dock = QtWidgets.QDockWidget("AgentBridge Chat", mw)
    dock.setWidget(ChatWidget(dock))
    dock.setObjectName("AgentBridgeChatDock")
    mw.addDockWidget(QtCore.Qt.RightDockWidgetArea, dock)
    _dock = dock


def hide_chat_dock():
    """Hide the dock if it is open. The dock is kept (not destroyed) so reopening it from
    the Tools menu or the File toolbar restores the same conversation, including the
    AgentBridge session it is talking to."""
    if _dock is not None:
        _dock.hide()
