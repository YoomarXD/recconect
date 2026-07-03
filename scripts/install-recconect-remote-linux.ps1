[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $LinuxHost = '192.168.200.3',
    [string] $LinuxUser = 'yoomarxd',
    [string] $GamePath = '',
    [string] $RemoteStageDir = '/tmp/recconect-install',
    [switch] $ListGameInstalls,
    [switch] $FetchLog,
    [string] $LogDestination = '',
    [string] $BepInExZipPath = '',
    [switch] $DisableRecconect,
    [switch] $EnableRecconect,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateSet('Diagnostics', 'Experimental', 'None')]
    [string] $ConfigMode = 'Experimental',

    [switch] $NoBuild,
    [switch] $InstallDotNetSdk,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$PluginGuid = 'com.yoomarxd.recconect'
$PluginFolderName = 'YoomarXD-Recconect'
$PluginDllName = 'Recconect.dll'
$DefaultBepInExZipName = 'BepInEx_win_x64_5.4.23.5.zip'

function Get-RepoRoot {
    $scriptRoot = Resolve-Path -LiteralPath $PSScriptRoot
    $parent = Resolve-Path -LiteralPath (Join-Path $scriptRoot '..') -ErrorAction SilentlyContinue

    if ($parent -and (Test-Path -LiteralPath (Join-Path $parent.Path 'Recconect.sln'))) {
        return $parent.Path
    }

    if (Test-Path -LiteralPath (Join-Path $scriptRoot.Path 'Recconect.sln')) {
        return $scriptRoot.Path
    }

    throw 'Could not find Recconect.sln. Run this script from the repo checkout.'
}

function Get-DotNetCommand {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $defaultPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $defaultPath) {
        return $defaultPath
    }

    return $null
}

function Ensure-DotNetSdk {
    param([bool] $AllowInstall)

    $dotnet = Get-DotNetCommand
    if ($dotnet) {
        return $dotnet
    }

    $installCommand = 'winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-package-agreements --accept-source-agreements'
    if (-not $AllowInstall) {
        throw "The .NET SDK is required to build from source. Install it with: $installCommand`nThen rerun this script, or rerun with -InstallDotNetSdk."
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'winget was not found. Install the .NET SDK manually, then rerun this script.'
    }

    Write-Host 'Installing .NET 8 SDK with winget...'
    & $winget.Source install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install Microsoft.DotNet.SDK.8 with exit code $LASTEXITCODE."
    }

    $dotnet = Get-DotNetCommand
    if (-not $dotnet) {
        throw 'The .NET SDK installer completed, but dotnet.exe was not found in this PowerShell session. Open a new PowerShell window and rerun the script.'
    }

    return $dotnet
}

function Get-BuiltDllPath {
    param(
        [string] $RepoRoot,
        [string] $BuildConfiguration,
        [bool] $SkipBuild,
        [bool] $AllowDotNetInstall
    )

    $solution = Join-Path $RepoRoot 'Recconect.sln'
    if (-not $SkipBuild) {
        $dotnet = Ensure-DotNetSdk -AllowInstall:$AllowDotNetInstall
        $buildOutput = & $dotnet build $solution -c $BuildConfiguration -p:CopyFilesToPluginOutputDirectoryOnBuild=false 2>&1
        $buildOutput | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }

    $builtDll = Join-Path $RepoRoot "bin\$BuildConfiguration\netstandard2.1\$PluginDllName"
    if (Test-Path -LiteralPath $builtDll) {
        return (Resolve-Path -LiteralPath $builtDll).Path
    }

    throw "Could not find built DLL: $builtDll"
}

function Get-BepInExZipPath {
    param(
        [string] $RequestedZipPath,
        [string] $RepoRoot
    )

    $candidates = @()
    if ($RequestedZipPath) {
        $candidates += $RequestedZipPath
    }

    $candidates += @(
        (Join-Path $RepoRoot $DefaultBepInExZipName),
        (Join-Path $PSScriptRoot $DefaultBepInExZipName),
        (Join-Path (Join-Path $PSScriptRoot '..') $DefaultBepInExZipName)
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Could not find $DefaultBepInExZipName. Pass -BepInExZipPath."
}

function New-RecconectConfigText {
    param([string] $Mode)

    $experimental = if ($Mode -eq 'Experimental') { 'true' } else { 'false' }

    return @"
[Diagnostics]
Enabled = true
LogJoinState = true
VerboseRuntimeDiagnostics = false

[Reconnect]
ExperimentalReconnectEnabled = $experimental
ConfigureRoomTtlOnCreate = true
PreservePlayerObjectsDuringReconnect = true
ForcePlayerRespawnAfterReconnect = false
AllowHostReconnect = false
PlayerTtlMilliseconds = 120000
EmptyRoomTtlMilliseconds = 180000
MaxReconnectAttempts = 5
ReconnectAttemptDelaySeconds = 2
ReconnectAttemptTimeoutSeconds = 12
ReconnectStabilizeSeconds = 20
ReconnectRespawnGraceSeconds = 5
EligibleDisconnectCauses = ClientTimeout,ServerTimeout,Exception,ExceptionOnConnect
"@
}

function ConvertTo-ShellSingleQuoted {
    param([string] $Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Get-Remote {
    return "$LinuxUser@$LinuxHost"
}

function Invoke-RemoteBash {
    param(
        [string] $Script,
        [switch] $Echo
    )

    $remote = Get-Remote
    $localTemp = Join-Path ([System.IO.Path]::GetTempPath()) "recconect-remote-$([System.Guid]::NewGuid().ToString('N')).sh"
    $remoteTemp = "/tmp/recconect-remote-$PID-$([System.Guid]::NewGuid().ToString('N')).sh"
    $normalized = ($Script -replace "`r`n", "`n") -replace "`r", "`n"

    try {
        [System.IO.File]::WriteAllText($localTemp, $normalized, [System.Text.UTF8Encoding]::new($false))
        & scp -q -o BatchMode=yes -o ConnectTimeout=10 $localTemp "${remote}:$remoteTemp"
        if ($LASTEXITCODE -ne 0) {
            throw "scp failed uploading temporary remote script to ${remote}:$remoteTemp"
        }

        $output = & ssh -o BatchMode=yes -o ConnectTimeout=10 $remote "bash '$remoteTemp'; rc=`$?; rm -f '$remoteTemp'; exit `$rc" 2>&1
        if ($Echo) {
            $output | ForEach-Object { Write-Host $_ }
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Remote command failed on $remote with exit code $LASTEXITCODE."
        }

        if ($Echo) {
            return
        }

        return $output
    }
    finally {
        Remove-Item -LiteralPath $localTemp -Force -ErrorAction SilentlyContinue
    }
}

function Get-RemoteGameInstalls {
    $script = @'
set -euo pipefail
roots=(
  "$HOME/.steam/steam"
  "$HOME/.local/share/Steam"
  "$HOME/snap/steam/common/.local/share/Steam"
  "$HOME/snap/steam/common/.steam/steam"
)

seen=""
for root in "${roots[@]}"; do
  [ -d "$root/steamapps" ] || continue
  manifest="$root/steamapps/appmanifest_3241660.acf"
  [ -f "$manifest" ] || continue
    install_dir=$(sed -n 's/.*"installdir"[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
    [ -n "$install_dir" ] || install_dir="REPO"
    game="$root/steamapps/common/$install_dir"
    [ -f "$game/REPO.exe" ] || continue
    real=$(readlink -f "$game")
    case ":$seen:" in
      *":$real:"*) ;;
      *) seen="$seen:$real"; printf '%s\n' "$real" ;;
    esac
done

if [ -z "$seen" ]; then
  for root in "${roots[@]}"; do
    [ -d "$root/steamapps/common" ] || continue
    find "$root/steamapps/common" -maxdepth 2 -type f -name REPO.exe -printf '%h\n' 2>/dev/null | while IFS= read -r game; do
      readlink -f "$game"
    done
  done | sort -u
fi
'@

    return @(Invoke-RemoteBash -Script $script | Where-Object { $_ -and ($_ -notmatch '^$') })
}

function Resolve-RemoteGamePath {
    if ($GamePath) {
        return $GamePath
    }

    $installs = @(Get-RemoteGameInstalls)
    if ($installs.Count -eq 0) {
        throw "No remote R.E.P.O. install was found on $(Get-Remote). Pass -GamePath."
    }

    if ($installs.Count -gt 1) {
        $installs | ForEach-Object { Write-Host "Remote game install: $_" }
        throw 'Multiple remote R.E.P.O. installs were found. Pass -GamePath.'
    }

    return $installs[0]
}

function Copy-ToRemote {
    param(
        [string] $LocalPath,
        [string] $RemotePath
    )

    $remote = Get-Remote
    & scp -q -o BatchMode=yes -o ConnectTimeout=10 $LocalPath "${remote}:$RemotePath"
    if ($LASTEXITCODE -ne 0) {
        throw "scp failed copying $LocalPath to ${remote}:$RemotePath"
    }
}

function Receive-RemoteFile {
    param(
        [string] $RemotePath,
        [string] $LocalPath
    )

    $remote = Get-Remote
    & scp -q -o BatchMode=yes -o ConnectTimeout=10 "${remote}:$RemotePath" $LocalPath
    if ($LASTEXITCODE -ne 0) {
        throw "scp failed copying ${remote}:$RemotePath to $LocalPath"
    }
}

$repoRoot = Get-RepoRoot

if ($ListGameInstalls) {
    $installs = @(Get-RemoteGameInstalls)
    if ($installs.Count -eq 0) {
        Write-Host "No remote R.E.P.O. installs found on $(Get-Remote)."
    } else {
        $installs | ForEach-Object { Write-Host $_ }
    }
    return
}

$resolvedGamePath = Resolve-RemoteGamePath

if ($DisableRecconect -and $EnableRecconect) {
    throw 'Pass only one of -DisableRecconect or -EnableRecconect.'
}

if ($DisableRecconect -or $EnableRecconect) {
    $enableValue = if ($EnableRecconect) { '1' } else { '0' }
    Invoke-RemoteBash -Echo -Script @"
set -euo pipefail
game=$(ConvertTo-ShellSingleQuoted $resolvedGamePath)
plugin_folder=$(ConvertTo-ShellSingleQuoted $PluginFolderName)
plugin_dll=$(ConvertTo-ShellSingleQuoted $PluginDllName)
enable=$enableValue
dll="`$game/BepInEx/plugins/`$plugin_folder/`$plugin_dll"
disabled="`$dll.disabled"

if [ "`$enable" = "1" ]; then
  if [ -f "`$disabled" ]; then
    mv -f "`$disabled" "`$dll"
    echo "Enabled Recconect: `$dll"
  elif [ -f "`$dll" ]; then
    echo "Recconect is already enabled: `$dll"
  else
    echo "No Recconect DLL found to enable: `$dll" >&2
    exit 2
  fi
else
  if [ -f "`$dll" ]; then
    mv -f "`$dll" "`$disabled"
    echo "Disabled Recconect: `$disabled"
  elif [ -f "`$disabled" ]; then
    echo "Recconect is already disabled: `$disabled"
  else
    echo "No Recconect DLL found to disable: `$dll" >&2
    exit 2
  fi
fi
"@
    return
}

if ($FetchLog) {
    if (-not $LogDestination) {
        $logDir = Join-Path $repoRoot 'artifacts'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $LogDestination = Join-Path $logDir "linux-LogOutput-$stamp.log"
    }

    Receive-RemoteFile -RemotePath "$resolvedGamePath/BepInEx/LogOutput.log" -LocalPath $LogDestination
    Write-Host "Fetched remote log: $LogDestination"
    return
}

$dllPath = Get-BuiltDllPath -RepoRoot $repoRoot -BuildConfiguration $Configuration -SkipBuild:$NoBuild -AllowDotNetInstall:$InstallDotNetSdk
$bepInExZip = Get-BepInExZipPath -RequestedZipPath $BepInExZipPath -RepoRoot $repoRoot
$remoteStage = $RemoteStageDir.TrimEnd('/')

Invoke-RemoteBash -Echo -Script @"
set -euo pipefail
mkdir -p $(ConvertTo-ShellSingleQuoted $remoteStage)
"@

Copy-ToRemote -LocalPath $dllPath -RemotePath "$remoteStage/$PluginDllName"
Copy-ToRemote -LocalPath $bepInExZip -RemotePath "$remoteStage/$DefaultBepInExZipName"

$remoteConfigPath = "$remoteStage/$PluginGuid.cfg"
$configText = New-RecconectConfigText -Mode $ConfigMode
$uploadConfigScript = @"
set -euo pipefail
mkdir -p $(ConvertTo-ShellSingleQuoted $remoteStage)
cat > $(ConvertTo-ShellSingleQuoted $remoteConfigPath) <<'RECCONECT_CFG'
$configText
RECCONECT_CFG
"@
Invoke-RemoteBash -Echo -Script $uploadConfigScript

$forceValue = if ($Force) { '1' } else { '0' }
$installScript = @"
set -euo pipefail
game=$(ConvertTo-ShellSingleQuoted $resolvedGamePath)
stage=$(ConvertTo-ShellSingleQuoted $remoteStage)
plugin_folder=$(ConvertTo-ShellSingleQuoted $PluginFolderName)
plugin_dll=$(ConvertTo-ShellSingleQuoted $PluginDllName)
plugin_guid=$(ConvertTo-ShellSingleQuoted $PluginGuid)
bepinex_zip=$(ConvertTo-ShellSingleQuoted $DefaultBepInExZipName)
force=$forceValue

if [ ! -f "`$game/REPO.exe" ]; then
  echo "GamePath does not look like R.E.P.O.; REPO.exe missing: `$game" >&2
  exit 2
fi

if [ ! -f "`$game/BepInEx/core/BepInEx.dll" ] || [ ! -f "`$game/winhttp.dll" ]; then
  echo "Installing BepInEx Windows x64 pack into Proton game folder: `$game"
  unzip -oq "`$stage/`$bepinex_zip" -d "`$game"
else
  echo "BepInEx already appears installed: `$game"
fi

mkdir -p "`$game/BepInEx/plugins/`$plugin_folder" "`$game/BepInEx/config"
stamp=`$(date +%Y%m%d-%H%M%S)
target_dll="`$game/BepInEx/plugins/`$plugin_folder/`$plugin_dll"
target_cfg="`$game/BepInEx/config/`$plugin_guid.cfg"

if [ -f "`$target_dll" ] && [ "`$force" != "1" ]; then
  cp -f "`$target_dll" "`$target_dll.bak-`$stamp"
fi

if [ -f "`$target_cfg" ] && [ "`$force" != "1" ]; then
  cp -f "`$target_cfg" "`$target_cfg.bak-`$stamp"
fi

cp -f "`$stage/`$plugin_dll" "`$target_dll"
cp -f "`$stage/`$plugin_guid.cfg" "`$target_cfg"
chmod -R u+rwX "`$game/BepInEx" "`$game/doorstop_config.ini" "`$game/winhttp.dll" 2>/dev/null || true

echo "Installed Recconect DLL: `$target_dll"
echo "Installed Recconect config: `$target_cfg"
echo "Remote log path after launch: `$game/BepInEx/LogOutput.log"
"@

Invoke-RemoteBash -Echo -Script $installScript
