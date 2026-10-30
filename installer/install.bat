@echo off
REM FreeCADTool installer launcher for Windows.
REM Works even when only this .bat was downloaded: it fetches install.ps1 by itself.
REM The window stays open at the end so the user can read the messages.
setlocal
title FreeCADTool installer

set "PS1=%~dp0install.ps1"
if exist "%PS1%" goto run

echo Downloading the installer script...
set "PS1=%TEMP%\freecadtool-install.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest 'https://raw.githubusercontent.com/Graphene-Lab/FreeCADTool/main/installer/install.ps1' -OutFile \"$env:TEMP\freecadtool-install.ps1\" -UseBasicParsing"
if not exist "%TEMP%\freecadtool-install.ps1" (
	echo.
	echo Could not download the installer script. Check the internet connection.
	echo Press any key to close this window.
	pause >nul
	exit /b 1
)

:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"

echo.
echo The installer has finished. Review the messages above.
echo Press any key to close this window.
pause >nul
