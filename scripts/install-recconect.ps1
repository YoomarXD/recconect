[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $ListProfiles,
    [switch] $DeepScan,

    [string] $ProfileName = '',
    [string] $ProfilePath = '',
    [string] $BepInExPluginsPath = '',
    [string] $GamePath = '',
    [string] $BepInExZipPath = '',
    [switch] $ListGameInstalls,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateSet('Diagnostics', 'Experimental', 'None')]
    [string] $ConfigMode = 'Diagnostics',

    [switch] $NoBuild,
    [switch] $CreateFriendZip,
    [switch] $DisableRecconect,
    [switch] $EnableRecconect,
    [string] $FriendZipPath = '',
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

    return $null
}

function Get-BundledDllPath {
    $candidates = @(
        (Join-Path $PSScriptRoot $PluginDllName),
        (Join-Path (Join-Path $PSScriptRoot '..') $PluginDllName)
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
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

    if ($RepoRoot) {
        $candidates += (Join-Path $RepoRoot $DefaultBepInExZipName)
    }

    $candidates += @(
        (Join-Path $PSScriptRoot $DefaultBepInExZipName),
        (Join-Path (Join-Path $PSScriptRoot '..') $DefaultBepInExZipName)
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Get-BuiltDllPath {
    param(
        [string] $RepoRoot,
        [string] $BuildConfiguration,
        [bool] $SkipBuild
    )

    if ($RepoRoot) {
        $solution = Join-Path $RepoRoot 'Recconect.sln'
        if (-not $SkipBuild) {
            $buildOutput = & dotnet build $solution -c $BuildConfiguration 2>&1
            $buildOutput | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet build failed with exit code $LASTEXITCODE"
            }
        }

        $builtDll = Join-Path $RepoRoot "bin\$BuildConfiguration\netstandard2.1\$PluginDllName"
        if (Test-Path -LiteralPath $builtDll) {
            return (Resolve-Path -LiteralPath $builtDll).Path
        }
    }

    $bundledDll = Get-BundledDllPath
    if ($bundledDll) {
        return $bundledDll
    }

    throw "Could not find $PluginDllName. Run from the repo, or use a friend zip that contains the DLL."
}

function New-RecconectConfigText {
    param([string] $Mode)

    $experimental = if ($Mode -eq 'Experimental') { 'true' } else { 'false' }

    return @"
[Diagnostics]
Enabled = true
LogJoinState = true

[Reconnect]
ExperimentalReconnectEnabled = $experimental
ConfigureRoomTtlOnCreate = true
AllowHostReconnect = false
PlayerTtlMilliseconds = 30000
EmptyRoomTtlMilliseconds = 60000
MaxReconnectAttempts = 3
ReconnectAttemptDelaySeconds = 2
ReconnectAttemptTimeoutSeconds = 12
EligibleDisconnectCauses = ClientTimeout,ServerTimeout,Exception,ExceptionOnConnect
"@
}

function Find-RecconectProfiles {
    param([bool] $UseDeepScan)

    $profileRoots = @(
        (Join-Path $env:APPDATA 'r2modmanPlus-local\R.E.P.O\profiles'),
        (Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\R.E.P.O\profiles'),
        (Join-Path $env:APPDATA 'Gale\R.E.P.O\profiles'),
        (Join-Path $env:LOCALAPPDATA 'r2modmanPlus-local\R.E.P.O\profiles'),
        (Join-Path $env:LOCALAPPDATA 'Thunderstore Mod Manager\DataFolder\R.E.P.O\profiles'),
        (Join-Path $env:LOCALAPPDATA 'Gale\R.E.P.O\profiles')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    $profiles = New-Object System.Collections.Generic.List[object]

    foreach ($root in $profileRoots) {
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $bepInEx = Join-Path $_.FullName 'BepInEx'
            $plugins = Join-Path $bepInEx 'plugins'
            if (Test-Path -LiteralPath $bepInEx) {
                $profiles.Add([pscustomobject]@{
                    Name = $_.Name
                    ProfilePath = $_.FullName
                    BepInExPath = $bepInEx
                    PluginsPath = $plugins
                    Source = $root
                })
            }
        }
    }

    if ($UseDeepScan) {
        $scanRoots = @($env:APPDATA, $env:LOCALAPPDATA) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
        foreach ($root in $scanRoots) {
            Get-ChildItem -LiteralPath $root -Directory -Filter 'BepInEx' -Recurse -Depth 7 -ErrorAction SilentlyContinue | ForEach-Object {
                $bepInExPath = $_.FullName
                $profile = Split-Path -Parent $_.FullName
                $plugins = Join-Path $_.FullName 'plugins'
                if ($profile -match 'R\.?E\.?P\.?O|repo' -and -not ($profiles | Where-Object { $_.BepInExPath -eq $bepInExPath })) {
                    $profiles.Add([pscustomobject]@{
                        Name = Split-Path -Leaf $profile
                        ProfilePath = $profile
                        BepInExPath = $_.FullName
                        PluginsPath = $plugins
                        Source = 'DeepScan'
                    })
                }
            }
        }
    }

    return $profiles | Sort-Object Source, Name -Unique
}

function Find-RepoGameInstalls {
    $steamRoots = @()
    $regPaths = @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam'
    )

    foreach ($path in $regPaths) {
        try {
            $props = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') {
                if ($props.$name) {
                    $steamRoots += $props.$name
                }
            }
        } catch {}
    }

    $steamRoots += @('C:\Program Files (x86)\Steam', 'C:\Program Files\Steam')
    $steamRoots = $steamRoots |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        ForEach-Object { (Resolve-Path -LiteralPath $_).Path } |
        Select-Object -Unique

    $libraryRoots = New-Object System.Collections.Generic.List[string]
    foreach ($steamRoot in $steamRoots) {
        $libraryRoots.Add($steamRoot)
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $content = Get-Content -Raw -LiteralPath $vdf
            [regex]::Matches($content, '"path"\s+"([^"]+)"') | ForEach-Object {
                $libraryRoots.Add(($_.Groups[1].Value -replace '\\\\', '\'))
            }
        }
    }

    $installs = New-Object System.Collections.Generic.List[object]
    foreach ($libraryRoot in ($libraryRoots | Select-Object -Unique)) {
        $manifest = Join-Path $libraryRoot 'steamapps\appmanifest_3241660.acf'
        if (-not (Test-Path -LiteralPath $manifest)) {
            continue
        }

        $manifestText = Get-Content -Raw -LiteralPath $manifest
        $installDir = if ($manifestText -match '"installdir"\s+"([^"]+)"') { $Matches[1] } else { 'REPO' }
        $gameDir = Join-Path (Join-Path $libraryRoot 'steamapps\common') $installDir
        $exe = Join-Path $gameDir 'REPO.exe'
        if (Test-Path -LiteralPath $exe) {
            $installs.Add([pscustomobject]@{
                Name = 'R.E.P.O.'
                GamePath = (Resolve-Path -LiteralPath $gameDir).Path
                ExePath = (Resolve-Path -LiteralPath $exe).Path
                Source = $manifest
            })
        }
    }

    return $installs | Sort-Object GamePath -Unique
}

function Resolve-InstallTarget {
    param(
        [string] $RequestedPluginsPath,
        [string] $RequestedProfilePath,
        [string] $RequestedProfileName,
        [bool] $UseDeepScan
    )

    if ($RequestedPluginsPath) {
        return [pscustomobject]@{
            Name = Split-Path -Leaf (Split-Path -Parent $RequestedPluginsPath)
            ProfilePath = Split-Path -Parent (Split-Path -Parent $RequestedPluginsPath)
            BepInExPath = Split-Path -Parent $RequestedPluginsPath
            PluginsPath = $RequestedPluginsPath
            Source = 'Argument'
        }
    }

    if ($RequestedProfilePath) {
        $profile = Resolve-Path -LiteralPath $RequestedProfilePath
        return [pscustomobject]@{
            Name = Split-Path -Leaf $profile.Path
            ProfilePath = $profile.Path
            BepInExPath = Join-Path $profile.Path 'BepInEx'
            PluginsPath = Join-Path $profile.Path 'BepInEx\plugins'
            Source = 'Argument'
        }
    }

    $profiles = @(Find-RecconectProfiles -UseDeepScan $UseDeepScan)
    if ($RequestedProfileName) {
        $profiles = @($profiles | Where-Object { $_.Name -like $RequestedProfileName -or $_.Name -eq $RequestedProfileName })
    }

    if ($profiles.Count -eq 0) {
        throw "No R.E.P.O. BepInEx profiles were found. Re-run with -ListProfiles -DeepScan, or pass -BepInExPluginsPath."
    }

    if ($profiles.Count -gt 1) {
        $profiles | Format-Table Name, PluginsPath, Source -AutoSize | Out-Host
        throw "Multiple profiles matched. Re-run with -ProfileName or -BepInExPluginsPath."
    }

    return $profiles[0]
}

function Resolve-GameTarget {
    param([string] $RequestedGamePath)

    if ($RequestedGamePath) {
        $resolved = Resolve-Path -LiteralPath $RequestedGamePath
        $exe = Join-Path $resolved.Path 'REPO.exe'
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "GamePath does not look like the R.E.P.O. game folder because REPO.exe was not found: $($resolved.Path)"
        }

        return [pscustomobject]@{
            Name = 'R.E.P.O.'
            GamePath = $resolved.Path
            BepInExPath = Join-Path $resolved.Path 'BepInEx'
            PluginsPath = Join-Path $resolved.Path 'BepInEx\plugins'
            Source = 'Argument'
        }
    }

    $installs = @(Find-RepoGameInstalls)
    if ($installs.Count -eq 0) {
        throw "No Steam R.E.P.O. install was found. Pass -GamePath with the folder containing REPO.exe."
    }

    if ($installs.Count -gt 1) {
        $installs | Format-Table GamePath, Source -AutoSize | Out-Host
        throw "Multiple R.E.P.O. installs matched. Re-run with -GamePath."
    }

    return [pscustomobject]@{
        Name = $installs[0].Name
        GamePath = $installs[0].GamePath
        BepInExPath = Join-Path $installs[0].GamePath 'BepInEx'
        PluginsPath = Join-Path $installs[0].GamePath 'BepInEx\plugins'
        Source = $installs[0].Source
    }
}

function Install-BepInEx {
    param(
        [object] $GameTarget,
        [string] $ZipPath,
        [bool] $Overwrite
    )

    if (-not $ZipPath) {
        throw "BepInEx is not installed and no $DefaultBepInExZipName was found. Pass -BepInExZipPath."
    }

    $gamePath = Resolve-Path -LiteralPath $GameTarget.GamePath
    $exe = Join-Path $gamePath.Path 'REPO.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Refusing to install BepInEx outside a R.E.P.O. game folder: $($gamePath.Path)"
    }

    $alreadyInstalled = (Test-Path -LiteralPath (Join-Path $gamePath.Path 'BepInEx\core\BepInEx.dll')) -and
        (Test-Path -LiteralPath (Join-Path $gamePath.Path 'winhttp.dll'))

    if ($alreadyInstalled -and -not $Overwrite) {
        Write-Host "BepInEx already appears installed at: $($gamePath.Path)"
        return
    }

    if ($PSCmdlet.ShouldProcess($gamePath.Path, "Extract BepInEx from $ZipPath")) {
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $gamePath.Path -Force
        New-Item -ItemType Directory -Force -Path (Join-Path $gamePath.Path 'BepInEx\plugins') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $gamePath.Path 'BepInEx\config') | Out-Null
    }

    Write-Host "Installed BepInEx to: $($gamePath.Path)"
}

function Install-Recconect {
    param(
        [object] $Target,
        [string] $DllPath,
        [string] $Mode,
        [bool] $Overwrite
    )

    $pluginsPath = $Target.PluginsPath
    $pluginDir = Join-Path $pluginsPath $PluginFolderName
    $targetDll = Join-Path $pluginDir $PluginDllName
    $configDir = Join-Path $Target.BepInExPath 'config'
    $configPath = Join-Path $configDir "$PluginGuid.cfg"
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

    if ($PSCmdlet.ShouldProcess($pluginDir, "Install $PluginDllName")) {
        New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

        if ((Test-Path -LiteralPath $targetDll) -and -not $Overwrite) {
            Copy-Item -LiteralPath $targetDll -Destination "$targetDll.bak-$stamp" -Force
        }

        Copy-Item -LiteralPath $DllPath -Destination $targetDll -Force
    }

    if ($Mode -ne 'None') {
        if ($PSCmdlet.ShouldProcess($configPath, "Write $Mode config")) {
            New-Item -ItemType Directory -Force -Path $configDir | Out-Null

            if ((Test-Path -LiteralPath $configPath) -and -not $Overwrite) {
                Copy-Item -LiteralPath $configPath -Destination "$configPath.bak-$stamp" -Force
            }

            New-RecconectConfigText -Mode $Mode | Set-Content -LiteralPath $configPath -Encoding UTF8
        }
    }

    Write-Host "Installed Recconect to: $pluginDir"
    if ($Mode -ne 'None') {
        Write-Host "Wrote config: $configPath"
    }
}

function Set-RecconectEnabled {
    param(
        [object] $Target,
        [bool] $Enabled
    )

    $pluginDir = Join-Path $Target.PluginsPath $PluginFolderName
    $dll = Join-Path $pluginDir $PluginDllName
    $disabled = Join-Path $pluginDir "$PluginDllName.disabled"

    if ($Enabled) {
        if (Test-Path -LiteralPath $disabled) {
            Rename-Item -LiteralPath $disabled -NewName $PluginDllName -Force
            Write-Host "Enabled Recconect: $dll"
        } elseif (Test-Path -LiteralPath $dll) {
            Write-Host "Recconect is already enabled: $dll"
        } else {
            throw "Could not find disabled or enabled Recconect DLL in: $pluginDir"
        }
        return
    }

    if (Test-Path -LiteralPath $dll) {
        Rename-Item -LiteralPath $dll -NewName "$PluginDllName.disabled" -Force
        Write-Host "Disabled Recconect: $disabled"
    } elseif (Test-Path -LiteralPath $disabled) {
        Write-Host "Recconect is already disabled: $disabled"
    } else {
        throw "Could not find Recconect DLL in: $pluginDir"
    }
}

function New-FriendZip {
    param(
        [string] $DllPath,
        [string] $Mode,
        [string] $Destination,
        [string] $BepInExZip
    )

    $repoRoot = Get-RepoRoot
    if (-not $Destination) {
        $base = if ($repoRoot) { $repoRoot } else { (Resolve-Path -LiteralPath $PSScriptRoot).Path }
        $Destination = Join-Path $base 'dist\Recconect-friend-test.zip'
    }

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null

    $stageDir = Join-Path $destinationParent 'Recconect-friend-test'
    if (Test-Path -LiteralPath $stageDir) {
        $resolvedStage = Resolve-Path -LiteralPath $stageDir
        $resolvedParent = Resolve-Path -LiteralPath $destinationParent
        if (-not $resolvedStage.Path.StartsWith($resolvedParent.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove stage directory outside destination parent: $($resolvedStage.Path)"
        }
        Remove-Item -LiteralPath $stageDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    Copy-Item -LiteralPath $DllPath -Destination (Join-Path $stageDir $PluginDllName)
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $stageDir 'Install-Recconect.ps1')
    New-RecconectConfigText -Mode $Mode | Set-Content -LiteralPath (Join-Path $stageDir "$PluginGuid.cfg") -Encoding UTF8
    if ($BepInExZip) {
        Copy-Item -LiteralPath $BepInExZip -Destination (Join-Path $stageDir $DefaultBepInExZipName)
    }

    @"
# Recconect Friend Test Install

1. Extract this zip.
2. Open PowerShell in the extracted folder.
3. Run:

   .\Install-Recconect.ps1 -ListProfiles

4. Install to the test R.E.P.O. profile:

   .\Install-Recconect.ps1 -ProfileName "ReconnectTest" -ConfigMode $Mode

If profile discovery fails, pass the exact path:

   .\Install-Recconect.ps1 -BepInExPluginsPath "<profile>\BepInEx\plugins" -ConfigMode $Mode

If BepInEx is not installed, pass the game folder instead:

   .\Install-Recconect.ps1 -GamePath "D:\SteamLibrary\steamapps\common\REPO" -ConfigMode $Mode

Do not test host disconnects yet. Test client network interruption first.
"@ | Set-Content -LiteralPath (Join-Path $stageDir 'README-FRIEND-INSTALL.txt') -Encoding UTF8

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }

    $stageFiles = Get-ChildItem -LiteralPath $stageDir -Force
    Compress-Archive -LiteralPath $stageFiles.FullName -DestinationPath $Destination -Force
    Write-Host "Created friend zip: $Destination"
}

$repoRoot = Get-RepoRoot
$bepInExZip = Get-BepInExZipPath -RequestedZipPath $BepInExZipPath -RepoRoot $repoRoot

if ($ListProfiles) {
    $profiles = @(Find-RecconectProfiles -UseDeepScan $DeepScan)
    if ($profiles.Count -eq 0) {
        Write-Host "No R.E.P.O. BepInEx profiles found."
    } else {
        $profiles | Format-Table Name, PluginsPath, Source -AutoSize
    }
    return
}

if ($ListGameInstalls) {
    $installs = @(Find-RepoGameInstalls)
    if ($installs.Count -eq 0) {
        Write-Host "No Steam R.E.P.O. installs found."
    } else {
        $installs | Format-Table GamePath, Source -AutoSize
    }
    return
}

if ($GamePath -or ((-not $BepInExPluginsPath) -and (-not $ProfilePath) -and (-not $ProfileName) -and (-not $CreateFriendZip))) {
    $gameTarget = Resolve-GameTarget -RequestedGamePath $GamePath
    if ($DisableRecconect -or $EnableRecconect) {
        Set-RecconectEnabled -Target $gameTarget -Enabled:$EnableRecconect
        return
    }

    $dllPath = Get-BuiltDllPath -RepoRoot $repoRoot -BuildConfiguration $Configuration -SkipBuild:$NoBuild
    Install-BepInEx -GameTarget $gameTarget -ZipPath $bepInExZip -Overwrite:$Force
    Install-Recconect -Target $gameTarget -DllPath $dllPath -Mode $ConfigMode -Overwrite:$Force
    return
}

if ($BepInExPluginsPath -or $ProfilePath -or $ProfileName) {
    $target = Resolve-InstallTarget `
        -RequestedPluginsPath $BepInExPluginsPath `
        -RequestedProfilePath $ProfilePath `
        -RequestedProfileName $ProfileName `
        -UseDeepScan:$DeepScan

    if (-not (Test-Path -LiteralPath $target.BepInExPath)) {
        throw "Target profile does not have BepInEx installed: $($target.BepInExPath). Use -GamePath to install into the game folder, or install BepInExPack in the profile first."
    }

    if ($DisableRecconect -or $EnableRecconect) {
        Set-RecconectEnabled -Target $target -Enabled:$EnableRecconect
        return
    }

    $dllPath = Get-BuiltDllPath -RepoRoot $repoRoot -BuildConfiguration $Configuration -SkipBuild:$NoBuild
    Install-Recconect -Target $target -DllPath $dllPath -Mode $ConfigMode -Overwrite:$Force
}

if ($CreateFriendZip) {
    $dllPath = Get-BuiltDllPath -RepoRoot $repoRoot -BuildConfiguration $Configuration -SkipBuild:$NoBuild
    New-FriendZip -DllPath $dllPath -Mode $ConfigMode -Destination $FriendZipPath -BepInExZip $bepInExZip
}
