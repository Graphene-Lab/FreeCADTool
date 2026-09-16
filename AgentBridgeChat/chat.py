# AgentBridge chat dock for FreeCAD.
#
# A minimal OpenAI-compatible chat client that talks to a local AgentBridge server
# (POST /v1/chat/completions, Server-Sent Events streaming, multi-turn via
# session_id). The HTTP request runs on a background thread; results are pushed
# back to the GUI through Qt signals, so the FreeCAD UI never blocks.
import json
import os
import threading
import urllib.error
import urllib.request

from _qt import QtCore, QtWidgets

DEFAULT_URL = "http://localhost:5290/v1/chat/completions"
DEFAULT_TOOLS = ["FileTool", "GitTool", "FreeCADTool"]


def _chat_url():
    return os.environ.get("AGENTBRIDGE_URL", DEFAULT_URL)


def _chat_tools():
    raw = os.environ.get("AGENTBRIDGE_CHAT_TOOLS", "")
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
            with urllib.request.urlopen(req, timeout=300) as resp:
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
        self._client = ChatClient(_chat_url())
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


_DOCK_ATTR = "_agentbridge_chat_dock"


def show_chat_dock():
    """Add (once) the AgentBridge chat dock to the FreeCAD main window."""
    mw = QtWidgets.QApplication.instance()
    if mw is None:
        return
    existing = getattr(mw, _DOCK_ATTR, None)
    if existing is not None:
        existing.show()
        existing.raise_()
        return
    dock = QtWidgets.QDockWidget("AgentBridge Chat", mw)
    dock.setWidget(ChatWidget(dock))
    dock.setObjectName("AgentBridgeChatDock")
    mw.addDockWidget(QtCore.Qt.RightDockWidgetArea, dock)
    setattr(mw, _DOCK_ATTR, dock)
