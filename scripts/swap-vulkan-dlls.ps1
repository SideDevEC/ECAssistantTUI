<#
.SYNOPSIS
Swaps LLamaSharp 0.27.0 Vulkan native DLLs with latest llama.cpp build.
Run this AFTER building Core and TUI on Windows.
Updates: NuGet cache, Core, TUI, Console, and all Tests.
#>
param(
    [string]$LlamaVersion = "b10472",
    [string]$RepoRoot = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$downloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/$LlamaVersion/llama-$LlamaVersion-bin-win-vulkan-x64.zip"
$tempZip = "$env:TEMP\llama-vulkan-$LlamaVersion.zip"
$tempExtract = "$env:TEMP\llama-vulkan-$LlamaVersion-extracted"

Write-Host "=== Vulkan DLL Swap ===" -ForegroundColor Cyan
Write-Host "Version: $LlamaVersion"
Write-Host ""

# ── Download ──
Write-Host "Downloading..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZip

# ── Extract ──
Write-Host "Extracting..." -ForegroundColor Yellow
if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

# ── Find ggml-vulkan.dll in extracted ──
$newDllDir = Get-ChildItem -Path $tempExtract -Recurse -Filter "ggml-vulkan.dll" | Select-Object -First 1 -ExpandProperty DirectoryName
if (-not $newDllDir) {
    Write-Host "ERROR: ggml-vulkan.dll not found in archive" -ForegroundColor Red
    exit 1
}
Write-Host "Found DLLs: $newDllDir" -ForegroundColor Green
Write-Host ""

# ── NuGet cache (source of truth — survives rebuilds) ──
$nugetDir = "$env:USERPROFILE\.nuget\packages\llamasharp.backend.vulkan.windows\0.27.0\LLamaSharpRuntimes\win-x64\native\vulkan"

# ── All target directories ──
$targets = @()

# NuGet cache
if (Test-Path $nugetDir) { $targets += @{Path=$nugetDir; Label="NuGet cache"} }

# All vulkan/ folders under repo (Core, TUI, Console, Tests)
Get-ChildItem -Path $RepoRoot -Recurse -Directory -Filter "vulkan" | Where-Object {
    $_.FullName -like "*\runtimes\win-x64\native\vulkan*"
} | ForEach-Object {
    $rel = $_.FullName.Replace($RepoRoot, "").TrimStart("\")
    $targets += @{Path=$_.FullName; Label=$rel}
}

if ($targets.Count -eq 0) {
    Write-Host "No vulkan/ folders found. Build projects first." -ForegroundColor Red
    exit 1
}

# ── Swap ──
$dlls = @("ggml.dll", "ggml-base.dll", "ggml-vulkan.dll", "llama.dll", "mtmd.dll")

foreach ($target in $targets) {
    $dir = $target.Path
    $label = $target.Label

    if (-not (Test-Path "$dir\ggml-vulkan.dll")) { continue }

    Write-Host "[$label]" -ForegroundColor Cyan

    # Backup
    $backup = "$dir\backup-llamasharp-0.27.0"
    if (-not (Test-Path $backup)) {
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        foreach ($dll in $dlls) {
            if (Test-Path "$dir\$dll") { Copy-Item "$dir\$dll" "$backup\$dll" }
        }
        Write-Host "  Backed up to: $backup" -ForegroundColor DarkGray
    }

    # Swap
    foreach ($dll in $dlls) {
        if (Test-Path "$newDllDir\$dll") {
            Copy-Item "$newDllDir\$dll" "$dir\$dll" -Force
            Write-Host "  OK $dll" -ForegroundColor Green
        }
    }
    Write-Host ""
}

# ── Copy Core/TUI output to Console lib/ ──
Write-Host "Copying Core/TUI to Console lib/..." -ForegroundColor Yellow

$coreDll = Get-ChildItem -Path "$RepoRoot" -Recurse -Filter "ECAssistant.Core.dll" | Where-Object {
    $_.FullName -like "*\bin\Release\*" -or $_.FullName -like "*\bin\Debug\*"
} | Select-Object -First 1 -ExpandProperty FullName

$tuiDll = Get-ChildItem -Path "$RepoRoot" -Recurse -Filter "ECAssistant.TUI.dll" | Where-Object {
    $_.FullName -like "*\bin\Release\*" -or $_.FullName -like "*\bin\Debug\*"
} | Select-Object -First 1 -ExpandProperty FullName

$consoleLib = "$RepoRoot\ECAssistantConsole\lib"
if (Test-Path $consoleLib) {
    if ($coreDll) { Copy-Item $coreDll "$consoleLib\ECAssistant.Core.dll" -Force; Write-Host "  OK Core -> Console\lib\" -ForegroundColor Green }
    if ($tuiDll) { Copy-Item $tuiDll "$consoleLib\ECAssistant.TUI.dll" -Force; Write-Host "  OK TUI -> Console\lib\" -ForegroundColor Green }
} else {
    Write-Host "  Console\lib\ not found — skipping" -ForegroundColor DarkGray
}
Write-Host ""

# ── Cleanup ──
Remove-Item $tempZip -Force
Remove-Item $tempExtract -Recurse -Force

Write-Host "=== DONE ===" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Rebuild: dotnet build -c Release"
Write-Host "  2. Set gpu_layers > 0 in appsettings.json"
Write-Host "  3. Test with short prompt, then longer one"
Write-Host "  4. If crash -> restore from backup folders"
Write-Host "     Or: dotnet restore (resets NuGet cache to originals)"