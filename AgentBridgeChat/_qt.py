# Cross-version Qt import for the AgentBridge chat workbench.
#
# FreeCAD 1.x ships PySide6 and exposes it as `PySide`; FreeCAD 0.20 ships PySide2
# whose `PySide` shim does NOT re-export QtWidgets, so `from PySide import QtWidgets`
# raises there. Try the modern name first, then fall back to the versioned packages so
# the same workbench loads on both.
try:
    from PySide import QtCore, QtGui, QtWidgets          # FreeCAD 1.x (PySide6)
except ImportError:
    try:
        from PySide2 import QtCore, QtGui, QtWidgets     # FreeCAD 0.20
    except ImportError:
        from PySide6 import QtCore, QtGui, QtWidgets     # explicit PySide6
