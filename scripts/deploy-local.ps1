param(
    [Parameter(Mandatory = $true)]
    [string] $BepInExPluginsPath,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$solution = Join-Path $repoRoot 'Recconect.sln'
$targetRoot = Resolve-Path -LiteralPath $BepInExPluginsPath
$targetDir = Join-Path $targetRoot 'YoomarXD-Recconect'

dotnet build $solution -c $Configuration

$dll = Join-Path $repoRoot "bin\$Configuration\netstandard2.1\Recconect.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Build did not produce expected DLL: $dll"
}

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $targetDir 'Recconect.dll') -Force

Write-Host "Deployed Recconect.dll to $targetDir"
