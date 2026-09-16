import os
import sys

# Headless launcher for the FreeCAD Robust MCP Bridge.
# Run with: freecadcmd <this_script>   (freecadcmd.exe on Windows)
# The bridge package path is resolved relative to this file, so it works on any OS.
_here = os.path.dirname(os.path.abspath(__file__))
_bridge_dir = os.path.join(_here, "bridge", "RobustMCPBridge", "freecad_mcp_bridge")
sys.path.insert(0, _bridge_dir)

from server import FreecadMCPPlugin
import FreeCAD

p = FreecadMCPPlugin(host='127.0.0.1', port=9876, xmlrpc_port=9875, enable_xmlrpc=True)
p.start()
print("BRIDGE_READY headless=%s" % (not FreeCAD.GuiUp), flush=True)
p.run_forever()
