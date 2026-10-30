#!/usr/bin/env bash
#
# FreeCADTool - one-shot installer (Linux native + WSL)
#
# Sets up a complete local stack in one run, with no questions asked:
#   1. Installs FreeCAD when it is missing (apt / dnf / pacman).
#   2. Downloads AgentBridge (the chat front-end and agent runtime).
#   3. Downloads the FreeCADTool plugin into AgentBridge's Tools/ folder (activated by default).
#   4. Installs the AgentBridge Chat workbench into FreeCAD's Mod folder, wired for "reverse start".
#   5. Creates a default document, the one-click launcher and the "AI FreeCAD" application icon.
#
# Run:  bash install.sh
#
# Environment overrides (all optional): AB_REPO AB_TAG TOOL_REPO TOOL_TAG TOOL_ZIP_URL
# INSTALL_ROOT AB_URL LLM_BASE_URL LLM_KEY LLM_MODEL SKIP_FREECAD_INSTALL
#
set -euo pipefail

AB_REPO="${AB_REPO:-Graphene-Lab/AgentBridge}"
AB_TAG="${AB_TAG:-latest}"
TOOL_REPO="${TOOL_REPO:-Graphene-Lab/FreeCADTool}"
TOOL_TAG="${TOOL_TAG:-latest}"
INSTALL_ROOT="${INSTALL_ROOT:-$HOME/freecad-tool-stack}"
AB_DIR="$INSTALL_ROOT/agentbridge"
TOOL_DIR="$AB_DIR/Tools/FreeCADTool"
DOC_DIR="$INSTALL_ROOT/documents"
DOC_PATH="$DOC_DIR/AI FreeCAD.FCStd"
# The reverse-start endpoint the chat panel records. A release AgentBridge serves 5290.
AB_URL="${AB_URL:-http://localhost:5290/v1/chat/completions}"
AB_PORT="$(printf '%s' "$AB_URL" | sed -n 's#.*:\([0-9][0-9]*\)/.*#\1#p')"
AB_PORT="${AB_PORT:-5290}"

log()  { printf '\n\033[1;32m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m! %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31mx %s\033[0m\n' "$*" >&2; exit 1; }

PROBLEMS=()
problem() { warn "$*"; PROBLEMS+=("$*"); }

# Package installation needs root. `sudo -n` is tried first so an unattended run never blocks on
# a password prompt; if that is not allowed, the normal sudo is used (it asks once, like any
# package manager invocation) and a clear message is printed when even that is impossible.
SUDO=""
if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi
pkg_install() {
  local pm="$1"; shift
  case "$pm" in
	apt)      $SUDO -n apt-get install -y --no-install-recommends "$@" 2>/dev/null \
				|| $SUDO apt-get install -y --no-install-recommends "$@" ;;
	dnf)      $SUDO -n dnf install -y "$@" 2>/dev/null || $SUDO dnf install -y "$@" ;;
	pacman)   $SUDO -n pacman -S --noconfirm --needed "$@" 2>/dev/null || $SUDO pacman -S --noconfirm --needed "$@" ;;
	*)        return 1 ;;
  esac
}

find_freecad_cmd() {
  local c
  for c in "$(command -v freecadcmd 2>/dev/null || true)" \
		   "$(command -v FreeCADCmd 2>/dev/null || true)" \
		   /usr/bin/freecadcmd /usr/bin/FreeCADCmd /usr/local/bin/freecadcmd \
		   /snap/bin/freecadcmd /opt/freecadcmd; do
	[ -n "$c" ] && [ -x "$c" ] && { printf '%s' "$c"; return 0; }
  done
  for c in /opt/FreeCAD*/bin/freecadcmd /opt/freecad*/bin/freecadcmd /usr/lib/freecad*/bin/freecadcmd; do
	[ -x "$c" ] && { printf '%s' "$c"; return 0; }
  done
  return 1
}

find_freecad_gui() {
  local c
  for c in "$(command -v freecad 2>/dev/null || true)" \
		   "$(command -v FreeCAD 2>/dev/null || true)" \
		   /usr/bin/freecad /usr/bin/FreeCAD /usr/local/bin/freecad \
		   /snap/bin/freecad /opt/freecad; do
	[ -n "$c" ] && [ -x "$c" ] && { printf '%s' "$c"; return 0; }
  done
  for c in /opt/FreeCAD*/bin/freecad /opt/freecad*/bin/freecad /usr/lib/freecad*/bin/freecad; do
	[ -x "$c" ] && { printf '%s' "$c"; return 0; }
  done
  return 1
}

install_freecad() {
  if [ "${SKIP_FREECAD_INSTALL:-0}" = "1" ]; then
	warn "SKIP_FREECAD_INSTALL=1: not installing FreeCAD"
	return 0
  fi
  log "Installing FreeCAD"
  if command -v apt-get >/dev/null 2>&1; then
	$SUDO -n apt-get update -y 2>/dev/null || true
	pkg_install apt freecad || problem "apt could not install FreeCAD"
  elif command -v dnf >/dev/null 2>&1; then
	pkg_install dnf freecad || problem "dnf could not install FreeCAD"
  elif command -v pacman >/dev/null 2>&1; then
	pkg_install pacman freecad || problem "pacman could not install FreeCAD"
  else
	problem "No supported package manager found (apt, dnf, pacman): install FreeCAD yourself from https://www.freecad.org/downloads and run this installer again"
  fi
}

# --- FreeCAD -------------------------------------------------------------
log "Checking FreeCAD"
FC_CMD="$(find_freecad_cmd || true)"
FC_GUI="$(find_freecad_gui || true)"
if [ -z "$FC_CMD" ] || [ -z "$FC_GUI" ]; then
  install_freecad
  hash -r 2>/dev/null || true
  FC_CMD="$(find_freecad_cmd || true)"
  FC_GUI="$(find_freecad_gui || true)"
fi
if [ -n "$FC_CMD" ]; then log "FreeCAD command found: $FC_CMD"; else problem "FreeCAD was not found, so the tool has no CAD engine to drive. Install it from https://www.freecad.org/downloads and run this installer again."; fi
if [ -z "$FC_GUI" ]; then problem "The FreeCAD GUI (freecad) was not found: the chat panel lives inside the FreeCAD window, so it cannot be opened without it."; fi

# --- AgentBridge + FreeCADTool plugin ------------------------------------
mkdir -p "$AB_DIR"
if [ -x "$AB_DIR/agent" ]; then
  log "AgentBridge already installed; skipping download"
else
  log "Downloading AgentBridge"
  asset="agentbridge-linux-x64.tar.gz"
  if [ "$AB_TAG" = "latest" ]; then
	url="https://github.com/$AB_REPO/releases/latest/download/$asset"
  else
	url="https://github.com/$AB_REPO/releases/download/$AB_TAG/$asset"
  fi
  # Direct download from the public release; no gh auth needed. The archive is large, so retry
  # and resume a partial transfer instead of failing on a network blip.
  curl -fL --retry 5 --retry-delay 5 -C - "$url" -o "$AB_DIR/$asset" \
	|| die "AgentBridge download failed: $url"
  if ! tar -tzf "$AB_DIR/$asset" >/dev/null 2>&1; then
	warn "AgentBridge archive is corrupt; downloading again from scratch"
	rm -f "$AB_DIR/$asset"
	curl -fL --retry 5 --retry-delay 5 "$url" -o "$AB_DIR/$asset" || die "AgentBridge download failed"
	tar -tzf "$AB_DIR/$asset" >/dev/null 2>&1 || die "AgentBridge archive could not be verified (download corrupt)"
  fi
  tar -xzf "$AB_DIR/$asset" -C "$AB_DIR" || die "AgentBridge extraction failed"
  [ -x "$AB_DIR/agent" ] || chmod +x "$AB_DIR/agent" 2>/dev/null || true
fi

# AgentBridge loads plugins from Tools/ next to its executable. The self-contained plugin zip
# published by the FreeCADTool release carries the DLL, the MCP bridge and the chat workbench.
log "Installing the FreeCADTool plugin into Tools/FreeCADTool"
tmpdir="$(mktemp -d)"
zip_url="${TOOL_ZIP_URL:-}"
if [ -z "$zip_url" ]; then
  if [ "$TOOL_TAG" = "latest" ]; then
	api="https://api.github.com/repos/$TOOL_REPO/releases/latest"
  else
	api="https://api.github.com/repos/$TOOL_REPO/releases/tags/$TOOL_TAG"
  fi
  zip_url="$(curl -sfL -H 'User-Agent: FreeCADTool-Installer' "$api" | python3 -c '
import sys, json
rel = json.load(sys.stdin)
for a in rel.get("assets", []):
	n = a.get("name", "")
	if n.startswith("FreeCADTool-") and n.endswith(".zip"):
		print(a["browser_download_url"]); break
' 2>/dev/null || true)"
fi
if [ -z "$zip_url" ]; then
  problem "Could not resolve the FreeCADTool plugin download (GitHub API rate limit or no release asset)"
else
  if curl -sfL "$zip_url" -o "$tmpdir/plugin.zip"; then
	python3 -m zipfile -e "$tmpdir/plugin.zip" "$tmpdir/plugin" >/dev/null 2>&1 || true
	payload="$tmpdir/plugin/FreeCADTool"
	if [ ! -f "$payload/FreeCADTool.dll" ]; then
	  payload="$(dirname "$(find "$tmpdir/plugin" -name FreeCADTool.dll -print -quit 2>/dev/null)")"
	fi
	if [ -f "$payload/FreeCADTool.dll" ]; then
	  rm -rf "$TOOL_DIR"
	  mkdir -p "$TOOL_DIR"
	  cp -a "$payload/." "$TOOL_DIR/"
	  log "FreeCADTool plugin installed"
	else
	  problem "FreeCADTool.dll not found in the plugin archive"
	fi
  else
	problem "Could not download the FreeCADTool plugin archive"
  fi
fi
rm -rf "$tmpdir"

# --- AgentBridge configuration -------------------------------------------
mkdir -p "$AB_DIR/PersistentData"
if [ -n "${LLM_KEY:-}" ] && [ ! -f "$AB_DIR/PersistentData/providers.json" ]; then
  log "Configuring the AgentBridge AI provider"
  cat > "$AB_DIR/PersistentData/providers.json" <<JSON
{
  "providers": [
	{
	  "name": "FreeCADStackLLM",
	  "baseUrl": "${LLM_BASE_URL:-http://localhost:11434/v1}",
	  "apiKey": "$LLM_KEY",
	  "model": "${LLM_MODEL:-gpt-4o-mini}"
	}
  ]
}
JSON
elif [ -f "$AB_DIR/PersistentData/providers.json" ]; then
  log "Keeping the existing AgentBridge providers.json"
fi

# --- Chat workbench inside FreeCAD ---------------------------------------
# FreeCADTool installs this itself the first time it runs, but the one-click launcher needs the
# chat to be there BEFORE the agent has ever been called - so it is installed here.
log "Installing the AgentBridge Chat workbench into FreeCAD"
mod_dir=""
if [ -n "$FC_CMD" ]; then
  probe="$(mktemp -d)"
  cat > "$probe/moddir.py" <<'PY'
import io
import FreeCAD
with io.open(__import__('os').path.join(__import__('os').path.dirname(
		__import__('os').path.abspath(__file__)), 'moddir.txt'), 'w', encoding='utf-8') as fh:
	fh.write(FreeCAD.getUserAppDataDir())
PY
  "$FC_CMD" "$probe/moddir.py" >/dev/null 2>&1 || true
  if [ -s "$probe/moddir.txt" ]; then
	mod_dir="$(cat "$probe/moddir.txt")/Mod"
  fi
  rm -rf "$probe"
fi
if [ -z "$mod_dir" ]; then
  mod_dir="${XDG_DATA_HOME:-$HOME/.local/share}/FreeCAD/Mod"
fi

chat_src="$TOOL_DIR/chat_mod"
chat_target="$mod_dir/AgentBridgeChat"
if [ -f "$chat_src/InitGui.py" ]; then
  mkdir -p "$chat_target"
  cp -a "$chat_src/." "$chat_target/"
  rm -rf "$chat_target/freecad_mcp_bridge"
  if [ -d "$TOOL_DIR/bridge/RobustMCPBridge/freecad_mcp_bridge" ]; then
	cp -a "$TOOL_DIR/bridge/RobustMCPBridge/freecad_mcp_bridge" "$chat_target/freecad_mcp_bridge"
  fi
  # "Reverse start" + "open the chat on launch": opening the dock starts AgentBridge itself, so
  # one icon is enough to go from desktop to a prompt that can edit the open document.
  cat > "$chat_target/agentbridge.json" <<JSON
{
  "url": "$AB_URL",
  "exe": "$AB_DIR/agent",
  "tools": "FileTool,GitTool,FreeCADTool",
  "autostart": true,
  "open_chat_on_start": true
}
JSON
  log "Chat workbench installed in $chat_target"
else
  problem "The chat payload (chat_mod) was not found next to the plugin, so FreeCAD will not offer the chat panel"
fi

# --- Default document ----------------------------------------------------
# The chat is enabled while a document is open (FreeCAD's own rule). The launcher opens this one,
# so the very first click lands on a document with the prompt already open.
if [ -n "$FC_CMD" ]; then
  log "Creating the default document"
  mkdir -p "$DOC_DIR"
  mktmp="$(mktemp -d)"
  cat > "$mktmp/mkdoc.py" <<PY
import os
import FreeCAD
path = r"""$DOC_PATH"""
if not os.path.exists(path):
	doc = FreeCAD.newDocument('AI FreeCAD')
	doc.saveAs(path)
PY
  "$FC_CMD" "$mktmp/mkdoc.py" >/dev/null 2>&1 || true
  rm -rf "$mktmp"
  if [ -f "$DOC_PATH" ]; then log "Default document ready"; else problem "Could not create the default document; open FreeCAD and create one yourself — the chat appears as soon as a document is open"; fi
fi

# --- One-click launcher + application icon -------------------------------
log "Creating the one-click launcher and the 'AI FreeCAD' icon"
cat > "$INSTALL_ROOT/start.sh" <<EOF
#!/bin/bash
ROOT=$(printf '%q' "$INSTALL_ROOT")
AB=$(printf '%q' "$AB_DIR/agent")
FC=$(printf '%q' "${FC_GUI:-freecad}")
DOC=$(printf '%q' "$DOC_PATH")
PORT=$AB_PORT

# AgentBridge first: the chat panel inside FreeCAD talks to it, and it drives the agent runtime.
if ! (exec 3<>/dev/tcp/127.0.0.1/"\$PORT") 2>/dev/null; then
  if [ -x "\$AB" ]; then
	setsid "\$AB" >/dev/null 2>&1 < /dev/null &
  fi
  for _ in \$(seq 1 60); do
	(exec 3<>/dev/tcp/127.0.0.1/"\$PORT") 2>/dev/null && break
	sleep 1
  done
fi

# Then FreeCAD, once, on the default document: a document must be open for the chat to be
# offered, and the workbench opens the dock itself (open_chat_on_start in agentbridge.json).
if ! pgrep -x -f "\$FC" >/dev/null 2>&1; then
  if [ -f "\$DOC" ]; then
	setsid "\$FC" "\$DOC" >/dev/null 2>&1 < /dev/null &
  else
	setsid "\$FC" >/dev/null 2>&1 < /dev/null &
  fi
fi
EOF
chmod +x "$INSTALL_ROOT/start.sh"

apps_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$apps_dir" "$HOME/Desktop"
cat > "$apps_dir/ai-freecad.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=AI FreeCAD
Comment=Start FreeCAD with the AI assistant and its chat panel
Exec=bash "$INSTALL_ROOT/start.sh"
Icon=freecad
Terminal=false
Categories=Graphics;Engineering;Science;
DESKTOP
cp "$apps_dir/ai-freecad.desktop" "$HOME/Desktop/ai-freecad.desktop" 2>/dev/null || true
chmod +x "$HOME/Desktop/ai-freecad.desktop" 2>/dev/null || true

echo
if [ "${#PROBLEMS[@]}" -gt 0 ]; then
  log "Finished with ${#PROBLEMS[@]} problem(s). Please read this before using the assistant."
  echo "  The installation did not fully work. What was reported:"
  for p in "${PROBLEMS[@]}"; do echo "    - $p"; done
  echo
  echo "  Fix what is listed above and run install.sh again: it continues where it stopped"
  echo "  and reuses everything that is already there."
else
  log "Done."
fi
echo "  HOW TO START:"
echo "    Open the 'AI FreeCAD' icon (Applications menu or Desktop)."
echo "    It starts AgentBridge and FreeCAD on a document, with the chat panel already open."
echo "    Then type a request such as 'build a 20x10x5 box and export it as STEP'."
echo
echo "  FreeCAD:        ${FC_GUI:-not found}"
echo "  AgentBridge:    $AB_DIR"
echo "  Plugin:         $TOOL_DIR"
echo "  Chat workbench: $chat_target"
echo "  Install root:   $INSTALL_ROOT"
if [ ! -f "$AB_DIR/PersistentData/providers.json" ]; then
  echo
  echo "  AI provider:    not configured yet. Open AgentBridge's settings once and choose your"
  echo "                  provider/model, or re-run the installer with LLM_BASE_URL, LLM_KEY"
  echo "                  and LLM_MODEL set."
fi
echo
echo "  Manual start, if you ever need it:"
echo "    Assistant: $AB_DIR/agent"
[ -n "$FC_GUI" ] && echo "    FreeCAD:   $FC_GUI \"$DOC_PATH\""
