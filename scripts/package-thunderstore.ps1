param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $OutputDir = 'dist',

    [string] $IconPath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$solution = Join-Path $repoRoot 'Recconect.sln'
$manifestPath = Join-Path $repoRoot 'manifest.json'
$readmePath = Join-Path $repoRoot 'README.md'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Missing manifest.json"
}

if (-not (Test-Path -LiteralPath $readmePath)) {
    throw "Missing README.md"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$packageName = "$($manifest.name)-$($manifest.version_number)"
$outputRoot = Join-Path $repoRoot $OutputDir
$stageDir = Join-Path $outputRoot $packageName
$zipPath = Join-Path $outputRoot "$packageName.zip"

if (Test-Path -LiteralPath $stageDir) {
    $resolvedStage = Resolve-Path -LiteralPath $stageDir
    $resolvedOutput = Resolve-Path -LiteralPath $outputRoot -ErrorAction SilentlyContinue
    if ($resolvedOutput -and -not $resolvedStage.Path.StartsWith($resolvedOutput.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove stage directory outside output root: $($resolvedStage.Path)"
    }
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}

dotnet build $solution -c $Configuration

$dll = Join-Path $repoRoot "bin\$Configuration\netstandard2.1\Recconect.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Build did not produce expected DLL: $dll"
}

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stageDir 'manifest.json')
Copy-Item -LiteralPath $readmePath -Destination (Join-Path $stageDir 'README.md')
Copy-Item -LiteralPath $dll -Destination (Join-Path $stageDir 'Recconect.dll')

$resolvedIcon = $null
if ($IconPath) {
    $resolvedIcon = Resolve-Path -LiteralPath $IconPath
} elseif (Test-Path -LiteralPath (Join-Path $repoRoot 'icon.png')) {
    $resolvedIcon = Resolve-Path -LiteralPath (Join-Path $repoRoot 'icon.png')
}

if ($resolvedIcon) {
    Copy-Item -LiteralPath $resolvedIcon.Path -Destination (Join-Path $stageDir 'icon.png')
} else {
    Write-Warning "No icon.png was provided. Thunderstore packages require icon.png before publishing."
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$stageFiles = Get-ChildItem -LiteralPath $stageDir -Force
if (-not $stageFiles) {
    throw "Package stage directory is empty: $stageDir"
}

Compress-Archive -LiteralPath $stageFiles.FullName -DestinationPath $zipPath -Force
Write-Host "Created package: $zipPath"
