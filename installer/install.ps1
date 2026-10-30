# FreeCADTool - one-shot installer for Windows.
# Invoked by install.bat. Sets up a complete local stack with NO questions asked:
#   1. Installs FreeCAD when it is missing.
#   2. Downloads AgentBridge (the chat front-end and agent runtime).
#   3. Downloads the FreeCADTool plugin into AgentBridge's Tools\ folder (activated by default).
#   4. Installs the AgentBridge Chat workbench into FreeCAD's Mod folder, wired for "reverse start".
#   5. Creates a default document, the one-click launcher and the "AI FreeCAD" icon.
# Everything is idempotent: a second run continues where the first stopped and reuses what is
# already there. Environment variables (see the README) override each default, so the same script
# also runs unattended on a fleet.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# --- Defaults (override with environment variables) ----------------------
$AbRepo     = if ($env:AB_REPO)     { $env:AB_REPO }     else { 'Graphene-Lab/AgentBridge' }
$AbTag      = if ($env:AB_TAG)      { $env:AB_TAG }      else { 'latest' }
$ToolRepo   = if ($env:TOOL_REPO)   { $env:TOOL_REPO }   else { 'Graphene-Lab/FreeCADTool' }
$ToolTag    = if ($env:TOOL_TAG)    { $env:TOOL_TAG }    else { 'latest' }
$WingetId   = if ($env:FREECAD_WINGET_ID) { $env:FREECAD_WINGET_ID } else { 'FreeCAD.FreeCAD' }
$InstallRoot= if ($env:INSTALL_ROOT){ $env:INSTALL_ROOT } else { Join-Path $env:LOCALAPPDATA 'freecad-tool-stack' }
$AbDir      = Join-Path $InstallRoot 'agentbridge'
$DocDir     = Join-Path $InstallRoot 'documents'
$DocPath    = Join-Path $DocDir 'AI FreeCAD.FCStd'
$LlmUrl     = if ($env:LLM_BASE_URL) { $env:LLM_BASE_URL } else { '' }
$LlmKey     = if ($env:LLM_KEY)      { $env:LLM_KEY }      else { '' }
$LlmModel   = if ($env:LLM_MODEL)    { $env:LLM_MODEL }    else { '' }
# The reverse-start endpoint the chat panel records. A release AgentBridge serves 5290.
$AbUrl      = if ($env:AB_URL)       { $env:AB_URL }       else { 'http://localhost:5290/v1/chat/completions' }

function Log($m)  { Write-Host "`n==> $m" -ForegroundColor Green }
function Warn($m) { Write-Host "! $m" -ForegroundColor Yellow }

# Everything that goes wrong but does not stop the install is collected here, so the final
# summary can tell the user what is NOT working instead of printing a bare "Done." over a
# stack that silently never came up.
$Problems = @()
function Problem($m) { Write-Host "! $m" -ForegroundColor Yellow; $script:Problems += $m }

# Keeps only the last $n characters of a program's output, so a long error dump never buries the
# one line that actually matters.
function Tail($t,$n) { $s = ('' + $t).Trim(); if ($s.Length -le $n) { return $s }; return $s.Substring($s.Length - $n) }

# Runs a native program (winget, curl, tar, freecadcmd) without letting its stderr kill the whole
# install. Windows PowerShell 5.1 raises a terminating NativeCommandError the moment a native
# program writes anything to stderr while $ErrorActionPreference is 'Stop', so the preference is
# dropped to 'Continue' here and success is decided by the exit code.
function Invoke-Native {
	param(
		[Parameter(Mandatory = $true)][string]$Exe,
		[string[]]$ArgList = @()
	)
	if (-not (Get-Command $Exe -ErrorAction SilentlyContinue) -and -not (Test-Path $Exe)) {
		return [pscustomobject]@{ Code = -1; Text = "$Exe not found" }
	}
	$saved = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	try {
		$out = & $Exe @ArgList 2>&1 | Out-String
		$code = $LASTEXITCODE
		$clean = ('' + $out).Trim()
		if ($clean.Length -gt 4000) { $clean = $clean.Substring($clean.Length - 4000) }
		$res = [pscustomobject]@{ Code = $code; Text = $clean }
	} finally {
		$ErrorActionPreference = $saved
	}
	return $res
}

# Finds a FreeCAD executable (freecad.exe / freecadcmd.exe) on the PATH, in the standard install
# roots, or in an unpacked portable folder. FREECAD_HOME / FREECAD_CMD win, so a user who already
# has FreeCAD somewhere unusual never sees a download.
function Find-FreeCadLeaf([string]$leaf) {
	if ($leaf -like '*cmd*' -and $env:FREECAD_CMD) { return $env:FREECAD_CMD }
	if ($env:FREECAD_HOME) {
		foreach ($c in @((Join-Path $env:FREECAD_HOME $leaf), (Join-Path $env:FREECAD_HOME (Join-Path 'bin' $leaf)))) {
			if (Test-Path $c) { return $c }
		}
	}
	$cmd = Get-Command $leaf -ErrorAction SilentlyContinue
	if ($cmd) { return $cmd.Source }
	$roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))
	foreach ($root in $roots) {
		if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path $root)) { continue }
		$hit = Get-ChildItem -Path (Join-Path $root 'FreeCAD*\bin') -Filter $leaf -ErrorAction SilentlyContinue |
			   Select-Object -First 1
		if ($hit) { return $hit.FullName }
		foreach ($dir in @(Get-ChildItem -Path $root -Filter 'FreeCAD*' -Directory -ErrorAction SilentlyContinue)) {
			foreach ($c in @((Join-Path $dir.FullName $leaf), (Join-Path $dir.FullName (Join-Path 'bin' $leaf)))) {
				if (Test-Path $c) { return $c }
			}
		}
	}
	return $null
}

# Downloads a URL with curl (resume + retries, built into Windows 10+), falling back to
# Invoke-WebRequest on machines where curl.exe is missing.
function Save-Url([string]$url, [string]$outFile) {
	$curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
	if ($curl) {
		$d = Invoke-Native -Exe $curl -ArgList @('-fL', '--retry', '5', '--retry-delay', '5', '-C', '-', '--output', $outFile, $url)
		if ($d.Code -eq 0) { return }
		# A resumed transfer of a DIFFERENT asset cannot succeed (-C - sees a size mismatch), so a
		# failed resume is retried once from scratch before the error is reported.
		Remove-Item $outFile -Force -ErrorAction SilentlyContinue
		$d = Invoke-Native -Exe $curl -ArgList @('-fL', '--retry', '5', '--retry-delay', '5', '--output', $outFile, $url)
		if ($d.Code -eq 0) { return }
		throw "download of $url failed (exit $($d.Code)): $(Tail $d.Text 300)"
	}
	Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -TimeoutSec 3600
}

# --- FreeCAD -------------------------------------------------------------
Log "Checking FreeCAD"
$fcExe = Find-FreeCadLeaf 'freecad.exe'
$fcCmd = Find-FreeCadLeaf 'freecadcmd.exe'
if ($fcExe -and $fcCmd) {
	Log "FreeCAD found at $(Split-Path $fcExe)"
} else {
	Log "Installing FreeCAD with winget (this is the long step, about 1 GB)"
	$r = Invoke-Native -Exe 'winget' -ArgList @(
		'install', '-e', '--id', $WingetId,
		'--accept-source-agreements', '--accept-package-agreements', '--silent')
	if ($r.Code -ne 0) { Problem "winget could not install FreeCAD (exit $($r.Code)): $(Tail $r.Text 300). Install it from https://www.freecad.org/downloads and run this installer again." }
	# A just-installed FreeCAD is not on this window's PATH yet, so refresh it before looking.
	$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
				[System.Environment]::GetEnvironmentVariable('Path','User')
	$fcExe = Find-FreeCadLeaf 'freecad.exe'
	$fcCmd = Find-FreeCadLeaf 'freecadcmd.exe'
	if ($fcExe -and $fcCmd) { Log "FreeCAD installed at $(Split-Path $fcExe)" }
}
if (-not $fcExe) { Problem "FreeCAD (freecad.exe) was not found. The tool cannot drive CAD without it: install it from https://www.freecad.org/downloads and run this installer again - it continues where it stopped." }

# --- AgentBridge + FreeCADTool plugin ------------------------------------
New-Item -ItemType Directory -Force -Path $AbDir | Out-Null
if (Test-Path (Join-Path $AbDir 'agent.exe')) {
	Log "AgentBridge already installed; skipping download"
} else {
	Log "Downloading AgentBridge"
	$asset = 'agentbridge-win-x64.tar.gz'
	if ($AbTag -eq 'latest') { $url = "https://github.com/$AbRepo/releases/latest/download/$asset" }
	else                     { $url = "https://github.com/$AbRepo/releases/download/$AbTag/$asset" }
	$pkg = Join-Path $AbDir $asset
	try { Save-Url $url $pkg } catch { throw "AgentBridge download failed: $($_.Exception.Message)" }
	# Verify the archive before extracting: a truncated or resumed transfer is corrupt.
	$verified = $false
	for ($i = 0; $i -lt 2; $i++) {
		$t = Invoke-Native -Exe 'tar' -ArgList @('-tzf', $pkg)
		if ($t.Code -eq 0) { $verified = $true; break }
		Warn "AgentBridge archive is corrupt; downloading again from scratch"
		Remove-Item $pkg -Force -ErrorAction SilentlyContinue
		try { Save-Url $url $pkg } catch { break }
	}
	if (-not $verified) { throw "AgentBridge archive could not be verified (download corrupt)" }
	$t = Invoke-Native -Exe 'tar' -ArgList @('-xzf', $pkg, '-C', $AbDir)
	if ($t.Code -ne 0) { throw "AgentBridge extraction failed (exit $($t.Code)): $(Tail $t.Text 300)" }
	if (-not (Test-Path (Join-Path $AbDir 'agent.exe'))) { throw "AgentBridge binary missing after extraction" }
}

# AgentBridge loads plugins from Tools\ next to its executable. The self-contained plugin zip
# published by the FreeCADTool release carries the DLL, the MCP bridge and the chat workbench.
Log "Installing the FreeCADTool plugin into Tools\FreeCADTool"
$toolsDir = Join-Path $AbDir 'Tools\FreeCADTool'
try {
	if ($env:TOOL_ZIP_URL) {
		$zipUrl = $env:TOOL_ZIP_URL
	} else {
		$api = if ($ToolTag -eq 'latest') { "https://api.github.com/repos/$ToolRepo/releases/latest" }
			   else { "https://api.github.com/repos/$ToolRepo/releases/tags/$ToolTag" }
		$rel = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'FreeCADTool-Installer' } -TimeoutSec 30
		$assetInfo = @($rel.assets) | Where-Object { $_.name -like 'FreeCADTool-*.zip' } | Select-Object -First 1
		if (-not $assetInfo) { throw "release $($rel.tag_name) has no FreeCADTool-*.zip asset" }
		$zipUrl = $assetInfo.browser_download_url
	}
	$zip = Join-Path $env:TEMP ("freecadtool-plugin-" + [guid]::NewGuid().ToString('N') + ".zip")
	Save-Url $zipUrl $zip
	$stage = Join-Path $env:TEMP ("freecadtool-plugin-" + [guid]::NewGuid().ToString('N'))
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	[IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)
	$payload = Join-Path $stage 'FreeCADTool'
	if (-not (Test-Path (Join-Path $payload 'FreeCADTool.dll'))) {
		$dll = Get-ChildItem -Path $stage -Filter 'FreeCADTool.dll' -Recurse -ErrorAction SilentlyContinue |
			   Select-Object -First 1
		if (-not $dll) { throw "FreeCADTool.dll not found in the release zip" }
		$payload = $dll.Directory.FullName
	}
	# Upgrade in place: an older plugin folder would otherwise keep stale files around.
	if (Test-Path $toolsDir) { Remove-Item $toolsDir -Recurse -Force -ErrorAction SilentlyContinue }
	New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
	Copy-Item -Path (Join-Path $payload '*') -Destination $toolsDir -Recurse -Force
	Remove-Item $zip -Force -ErrorAction SilentlyContinue
	Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
	Log "FreeCADTool plugin installed"
} catch {
	Problem ("Could not install the FreeCADTool plugin: " + $_.Exception.Message)
}

# --- AgentBridge configuration -------------------------------------------
# No questions: an already configured AgentBridge is never overwritten. Pass LLM_BASE_URL,
# LLM_KEY and LLM_MODEL to pre-configure the AI provider for a fully unattended install;
# otherwise the provider is chosen in AgentBridge's own settings the first time it runs.
$cfg = Join-Path $AbDir 'PersistentData'
New-Item -ItemType Directory -Force -Path $cfg | Out-Null
if ($LlmKey -and -not (Test-Path (Join-Path $cfg 'providers.json'))) {
	Log "Configuring the AgentBridge AI provider"
	$providers = @{ providers = @(@{
		name    = 'FreeCADStackLLM'
		baseUrl = if ($LlmUrl)   { $LlmUrl }   else { 'http://localhost:11434/v1' }
		apiKey  = $LlmKey
		model   = if ($LlmModel) { $LlmModel } else { 'gpt-4o-mini' }
	}) }
	$providers | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $cfg 'providers.json') -Encoding UTF8
	# Set-Content -Encoding UTF8 writes a BOM in Windows PowerShell; rewrite it without one.
	$provText = [System.IO.File]::ReadAllText((Join-Path $cfg 'providers.json'))
	[System.IO.File]::WriteAllText((Join-Path $cfg 'providers.json'), $provText, (New-Object System.Text.UTF8Encoding($false)))
} elseif (Test-Path (Join-Path $cfg 'providers.json')) {
	Log "Keeping the existing AgentBridge providers.json"
}

# --- Chat workbench inside FreeCAD ---------------------------------------
# FreeCADTool installs this itself the first time it runs, but the one-click launcher needs the
# chat to be there BEFORE the agent has ever been called - so it is installed here.
Log "Installing the AgentBridge Chat workbench into FreeCAD"
$chatModOk = $false
$modDir = $null
if ($fcCmd -and (Test-Path $fcCmd)) {
	# Ask FreeCAD itself where its user data lives: a portable archive keeps it INSIDE the install
	# folder, so a guessed %APPDATA% path would put the chat somewhere FreeCAD never loads.
	$probe = Join-Path $env:TEMP ("freecad-mod-probe-" + [guid]::NewGuid().ToString('N') + ".py")
	$probeOut = "$probe.out"
	$probeText = "import FreeCAD, io`nwith io.open(r'$probeOut', 'w', encoding='utf-8') as fh:`n    fh.write(FreeCAD.getUserAppDataDir())`n"
	[System.IO.File]::WriteAllText($probe, $probeText, (New-Object System.Text.UTF8Encoding($false)))
	$pr = Invoke-Native -Exe $fcCmd -ArgList @($probe)
	if (Test-Path $probeOut) {
		$userData = (Get-Content -Raw $probeOut).Trim()
		if (-not [string]::IsNullOrWhiteSpace($userData)) { $modDir = Join-Path $userData 'Mod' }
	}
	Remove-Item $probe, $probeOut -Force -ErrorAction SilentlyContinue
}
if (-not $modDir) {
	# Fallback for a FreeCAD that cannot be run yet: one Mod dir per installed version (v1-1, ...).
	$fcBase = Join-Path $env:APPDATA 'FreeCAD'
	$versionDirs = @(Get-ChildItem -Path $fcBase -Directory -Filter 'v*' -ErrorAction SilentlyContinue |
					 Sort-Object Name -Descending)
	if ($versionDirs.Count -gt 0) { $modDir = Join-Path $versionDirs[0].FullName 'Mod' }
}
if (-not $modDir -and $fcExe) {
	$ver = (Get-Item $fcExe).VersionInfo.ProductVersion
	if ($ver -match '^(\d+)\.(\d+)') { $modDir = Join-Path $env:APPDATA "FreeCAD\v$($Matches[1])-$($Matches[2])\Mod" }
}
if (-not $modDir) { $modDir = Join-Path $env:APPDATA 'FreeCAD\Mod' }

$chatSrc = Join-Path $toolsDir 'chat_mod'
$chatTarget = Join-Path $modDir 'AgentBridgeChat'
if (Test-Path (Join-Path $chatSrc 'InitGui.py')) {
	try {
		New-Item -ItemType Directory -Force -Path $chatTarget | Out-Null
		Copy-Item -Path (Join-Path $chatSrc '*') -Destination $chatTarget -Recurse -Force
		# Remove before copying, otherwise Copy-Item nests the source folder inside the old one on a
		# second run.
		$bridgeDst = Join-Path $chatTarget 'freecad_mcp_bridge'
		if (Test-Path $bridgeDst) { Remove-Item -LiteralPath $bridgeDst -Recurse -Force }
		$bridgeSrc = Join-Path $toolsDir 'bridge\RobustMCPBridge\freecad_mcp_bridge'
		if (Test-Path $bridgeSrc) {
			Copy-Item -Path $bridgeSrc -Destination $bridgeDst -Recurse -Force
		}
		# "Reverse start" + "open the chat on launch": opening the dock starts AgentBridge itself,
		# so one icon is enough to go from desktop to a prompt that can edit the open document.
		$hostCfg = [ordered]@{
			url                 = $AbUrl
			exe                 = (Join-Path $AbDir 'agent.exe')
			tools               = 'FileTool,GitTool,FreeCADTool'
			autostart           = $true
			open_chat_on_start  = $true
		}
		# Written BOM-free on purpose: the chat add-on parses it with Python's json on a plain
		# UTF-8 read, and a BOM there silently dropped exe/autostart/open_chat_on_start.
		$cfgPath = Join-Path $chatTarget 'agentbridge.json'
		$cfgText = $hostCfg | ConvertTo-Json -Depth 4
		[System.IO.File]::WriteAllText($cfgPath, $cfgText, (New-Object System.Text.UTF8Encoding($false)))
		$chatModOk = $true
		Log "Chat workbench installed in $chatTarget"
	} catch {
		Problem ("Could not install the chat workbench: " + $_.Exception.Message)
	}
} else {
	Problem "The chat payload (chat_mod) was not found next to the plugin, so FreeCAD will not offer the chat panel."
}

# --- Default document ----------------------------------------------------
# The chat is enabled while a document is open (FreeCAD's own rule). The launcher opens this one,
# so the very first click lands on a document with the prompt already open.
if ($fcCmd -and (Test-Path $fcCmd)) {
	Log "Creating the default document"
	New-Item -ItemType Directory -Force -Path $DocDir | Out-Null
	$mk = Join-Path $env:TEMP ("freecad-make-doc-" + [guid]::NewGuid().ToString('N') + ".py")
	$mkText = @"
import FreeCAD, os
path = r'$DocPath'
if not os.path.exists(path):
	doc = FreeCAD.newDocument('AI FreeCAD')
	doc.saveAs(path)
"@
	[System.IO.File]::WriteAllText($mk, $mkText, (New-Object System.Text.UTF8Encoding($false)))
	$mkr = Invoke-Native -Exe $fcCmd -ArgList @($mk)
	Remove-Item $mk -Force -ErrorAction SilentlyContinue
	if (Test-Path $DocPath) { Log "Default document ready" }
	else { Problem "Could not create the default document ($(Tail $mkr.Text 200)). Open FreeCAD and create one yourself; the chat becomes available as soon as a document is open." }
}

# --- One-click launcher + shortcut --------------------------------------
Log "Creating the one-click launcher and shortcuts"
$launcherTemplate = @'
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$ab = Join-Path $root 'agentbridge\agent.exe'
$fc = __FC_EXE__
$doc = Join-Path $root 'documents\AI FreeCAD.FCStd'
$port = __AB_PORT__
function Test-Host { (Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue).TcpTestSucceeded }
# Start AgentBridge first: the chat panel inside FreeCAD talks to it, and it drives the whole
# agent runtime. Minimized, because the conversation happens in FreeCAD's own dock.
if (-not (Test-Host)) {
	if (Test-Path $ab) { Start-Process -FilePath $ab -WorkingDirectory (Split-Path $ab) -WindowStyle Minimized }
	for ($i = 0; $i -lt 60; $i++) { if (Test-Host) { break }; Start-Sleep -Seconds 1 }
}
# Then FreeCAD, once, on the default document: a document must be open for the chat to be
# offered, and the workbench opens the dock itself (open_chat_on_start in agentbridge.json).
if (-not (Get-Process -Name 'freecad' -ErrorAction SilentlyContinue)) {
	if (Test-Path $doc) { Start-Process -FilePath $fc -ArgumentList "`"$doc`"" }
	else { Start-Process -FilePath $fc }
}
'@
$launcherPath = Join-Path $InstallRoot 'start.ps1'
$abPort = if ($AbUrl -match ':(\d+)/') { $Matches[1] } else { '5290' }
$launcherText = $launcherTemplate.Replace('__FC_EXE__', "'" + ($fcExe -replace "'", "''") + "'").Replace('__AB_PORT__', $abPort)
[System.IO.File]::WriteAllText($launcherPath, $launcherText, (New-Object System.Text.UTF8Encoding($false)))

$ws = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'AI FreeCAD'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
foreach ($lnk in @((Join-Path $desktop 'AI FreeCAD.lnk'), (Join-Path $startMenu 'AI FreeCAD.lnk'))) {
	$sc = $ws.CreateShortcut($lnk)
	$sc.TargetPath = Join-Path $PSHOME 'powershell.exe'
	$sc.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$launcherPath`""
	$sc.WorkingDirectory = $InstallRoot
	$sc.IconLocation = if ($fcExe) { "$fcExe,0" } else { "$AbDir\agent.exe,0" }
	$sc.Description = 'AI FreeCAD - FreeCAD with the AI assistant'
	$sc.Save()
}

Write-Host ''
# A bare "Done." over a stack that never actually came up leaves the user with no idea what
# failed. Anything collected during the run is listed here instead.
if ($Problems.Count -gt 0) {
	Log "Finished with $($Problems.Count) problem(s). Please read this before using the assistant."
	Write-Host "  The installation did not fully work. What was reported:"
	foreach ($p in $Problems) { Write-Host "    - $p" -ForegroundColor Yellow }
	Write-Host ''
	Write-Host "  Fix what is listed above and run install.bat again: it continues where it stopped"
	Write-Host "  and reuses everything that is already there."
} else {
	Log 'Done.'
}
Write-Host "  HOW TO START:"
Write-Host "    Double-click the 'AI FreeCAD' icon on the Desktop (or in the Start Menu)."
Write-Host "    It starts AgentBridge and FreeCAD on a document, with the chat panel already open."
Write-Host "    Then type a request such as 'build a 20x10x5 box and export it as STEP'."
Write-Host ''
Write-Host "  FreeCAD:        $(if ($fcExe) { $fcExe } else { 'not found' })"
Write-Host "  AgentBridge:    $AbDir"
Write-Host "  Plugin:         $toolsDir"
Write-Host "  Chat workbench: $chatTarget"
Write-Host "  Install root:   $InstallRoot"
if (-not (Test-Path (Join-Path $cfg 'providers.json'))) {
	Write-Host ''
	Write-Host "  AI provider:    not configured yet. Open AgentBridge's settings once and choose your"
	Write-Host "                  provider/model, or re-run the installer with LLM_BASE_URL, LLM_KEY"
	Write-Host "                  and LLM_MODEL set."
}
Write-Host ''
Write-Host "  Manual start, if you ever need it:"
Write-Host "    Assistant: $AbDir\agent.exe"
if ($fcExe) { Write-Host "    FreeCAD:   `"$fcExe`" `"$DocPath`"" }
