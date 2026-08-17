#!/bin/bash
#
# swap-vulkan-dlls.sh
#
# Downloads the latest llama.cpp Windows Vulkan build and swaps the native
# DLLs across all projects: NuGet cache (source of truth), ECAssistantTUI,
# ECAssistantConsole, ECAssistantCore, ECSQL, and all test projects.
#
# ⚠️  ABI RISK: LLamaSharp's C# bindings call native functions by name/signature.
#     If llama.cpp changed function signatures between the bundled version and
#     the latest, you may get a different crash. Always test after swapping.
#
# Usage:
#   ./swap-vulkan-dlls.sh
#
# The script auto-discovers all vulkan/ directories under:
#   - NuGet cache (~/.nuget/packages/llamasharp.backend.vulkan.windows/)
#   - ~/Agent/ECAssistant/
#   - ~/Agent/ECSQL/
#
# To restore originals: the NuGet cache backup folder has the originals.
# Re-run 'dotnet restore' to fully reset from NuGet (overwrites all bin/ copies).

set -euo pipefail

# ── Config ──
LLAMA_VERSION="b10472"  # Latest as of Aug 17, 2026 — update if newer exists
DOWNLOAD_URL="https://github.com/ggml-org/llama.cpp/releases/download/${LLAMA_VERSION}/llama-${LLAMA_VERSION}-bin-win-vulkan-x64.zip"
TEMP_DIR=$(mktemp -d)

# DLLs to swap
DLLS=(ggml.dll ggml-base.dll ggml-vulkan.dll llama.dll mtmd.dll)

# ── Find all target directories ──
NUGET_VULKAN_DIR="$HOME/.nuget/packages/llamasharp.backend.vulkan.windows/0.27.0/LLamaSharpRuntimes/win-x64/native/vulkan"

echo "╔══════════════════════════════════════════════════════════╗"
echo "║  LLAMA.CPP VULKAN DLL SWAPPER (ALL PROJECTS)            ║"
echo "║  Replaces LLamaSharp 0.27.0 native DLLs with latest     ║"
echo "╠══════════════════════════════════════════════════════════╣"
echo "║  Source: llama.cpp ${LLAMA_VERSION} (latest)               "
echo "║                                                          ║"
echo "║  Targets:                                                ║"
echo "║  1. NuGet cache (source of truth — survives dotnet build) ║"
echo "║  2. ECAssistantTUI bin/ (Debug + Release)                 ║"
echo "║  3. ECAssistantConsole bin/                              ║"
echo "║  4. ECAssistantCore bin/ + Tests bin/                     ║"
echo "║  5. ECSQL UI bin/ + Tests bin/                           ║"
echo "║                                                          ║"
echo "║  ⚠️  ABI RISK: Test after swap. Restore = dotnet restore. ║"
echo "╚══════════════════════════════════════════════════════════╝"
echo ""

# ── Download ──
echo "⬇️  Downloading llama.cpp ${LLAMA_VERSION} Vulkan build..."
ZIP_FILE="${TEMP_DIR}/llama-vulkan.zip"
curl -L -o "${ZIP_FILE}" "${DOWNLOAD_URL}" --fail 2>&1 || {
    echo "❌ Download failed for ${LLAMA_VERSION}."
    echo "   Check https://github.com/ggml-org/llama.cpp/releases for the latest"
    echo "   version that includes a 'bin-win-vulkan-x64.zip' asset."
    echo ""
    echo "   Not all releases have Vulkan builds. Look for one that does."
    exit 1
}

# ── Extract ──
echo "📦 Extracting..."
EXTRACT_DIR="${TEMP_DIR}/extracted"
mkdir -p "${EXTRACT_DIR}"
if command -v unzip &>/dev/null; then
    unzip -q "${ZIP_FILE}" -d "${EXTRACT_DIR}"
elif command -v 7z &>/dev/null; then
    7z x "${ZIP_FILE}" -o"${EXTRACT_DIR}" -y >/dev/null
else
    echo "❌ Need 'unzip' or '7z' to extract. Install: brew install unzip"
    exit 1
fi

# ── Find the DLLs in the extracted archive ──
NEW_DLL_DIR=$(find "${EXTRACT_DIR}" -name "ggml-vulkan.dll" -exec dirname {} \; | head -1)

if [ -z "${NEW_DLL_DIR}" ] || [ ! -d "${NEW_DLL_DIR}" ]; then
    echo "❌ Could not find ggml-vulkan.dll in the extracted archive."
    echo "   Archive contents:"
    find "${EXTRACT_DIR}" -type f | head -30
    exit 1
fi

echo "✅ Found DLLs in: ${NEW_DLL_DIR}"
echo ""

# ── List what we're swapping ──
echo "📋 New DLLs:"
for dll in "${DLLS[@]}"; do
    if [ -f "${NEW_DLL_DIR}/${dll}" ]; then
        NEW_SIZE=$(stat -f%z "${NEW_DLL_DIR}/${dll}" 2>/dev/null || stat -c%s "${NEW_DLL_DIR}/${dll}" 2>/dev/null || echo "?")
        echo "   ${dll}: ${NEW_SIZE} bytes"
    else
        echo "   ${dll}: ⚠️ not found in new build"
    fi
done
echo ""

# ── Function to swap DLLs in a target directory ──
swap_dir() {
    local target="$1"
    local label="$2"

    if [ ! -d "${target}" ]; then
        return 0  # Skip non-existent dirs
    fi

    # Check if it has at least one of our DLLs
    if [ ! -f "${target}/ggml-vulkan.dll" ]; then
        return 0  # Not a vulkan dir
    fi

    echo "🔄 [${label}] ${target}"

    # Backup
    local backup="${target}/backup-llamasharp-0.27.0"
    if [ ! -d "${backup}" ]; then
        mkdir -p "${backup}"
        for dll in "${DLLS[@]}"; do
            if [ -f "${target}/${dll}" ]; then
                cp "${target}/${dll}" "${backup}/${dll}"
            fi
        done
        echo "   💾 Backed up to: ${backup}"
    else
        echo "   💾 Backup already exists: ${backup} (skipping backup)"
    fi

    # Swap
    for dll in "${DLLS[@]}"; do
        if [ -f "${NEW_DLL_DIR}/${dll}" ] && [ -f "${target}/${dll}" ]; then
            cp "${NEW_DLL_DIR}/${dll}" "${target}/${dll}"
            echo "   ✅ ${dll}"
        elif [ -f "${NEW_DLL_DIR}/${dll}" ] && [ ! -f "${target}/${dll}" ]; then
            # New DLL that wasn't in old set — add it
            cp "${NEW_DLL_DIR}/${dll}" "${target}/${dll}"
            echo "   ✨ ${dll} (new)"
        elif [ ! -f "${NEW_DLL_DIR}/${dll}" ] && [ -f "${target}/${dll}" ]; then
            echo "   ⚠️ ${dll} not in new build — keeping old"
        fi
    done
    echo ""
}

# ── Swap all targets ──

# 1. NuGet cache (MOST IMPORTANT — this is the source of truth)
swap_dir "${NUGET_VULKAN_DIR}" "NuGet cache"

# 2. ECAssistant projects
for dir in $(find "$HOME/Agent/ECAssistant" -path "*/runtimes/win-x64/native/vulkan" -type d 2>/dev/null | sort -u); do
    # Extract a short label from the path
    label=$(echo "${dir}" | sed "s|$HOME/Agent/ECAssistant/||" | sed 's|/runtimes/win-x64/native/vulkan||')
    swap_dir "${dir}" "${label}"
done

# 3. ECSQL projects
for dir in $(find "$HOME/Agent/ECSQL" -path "*/runtimes/win-x64/native/vulkan" -type d 2>/dev/null | sort -u); do
    label=$(echo "${dir}" | sed "s|$HOME/Agent/ECSQL/||" | sed 's|/runtimes/win-x64/native/vulkan||')
    swap_dir "${dir}" "ECSQL/${label}"
done

# ── Copy Core/TUI output DLLs to Console + ECSQL lib folders ──
# Console and ECSQL reference pre-built Core/TUI DLLs from lib/ folders.
# After building Core + TUI, copy the fresh output so they pick up
# ModelLoadException and other new classes.
echo "📦 Copying Core/TUI output DLLs to lib/ folders..."

CORE_DLL=""
TUI_DLL=""

# Find Release builds first, fall back to Debug
for candidate in \
    "$HOME/Agent/ECAssistant/ECAssistantCore/bin/Release/net8.0/ECAssistant.Core.dll" \
    "$HOME/Agent/ECAssistant/ECAssistantCore/bin/Debug/net8.0/ECAssistant.Core.dll"; do
    if [ -f "${candidate}" ]; then
        CORE_DLL="${candidate}"
        break
    fi
done

for candidate in \
    "$HOME/Agent/ECAssistant/ECAssistantTUI/bin/Release/net8.0/ECAssistant.TUI.dll" \
    "$HOME/Agent/ECAssistant/ECAssistantTUI/bin/Debug/net8.0/ECAssistant.TUI.dll" do
    if [ -f "${candidate}" ]; then
        TUI_DLL="${candidate}"
        break
    fi
done

if [ -z "${CORE_DLL}" ]; then
    echo "   ⚠️ ECAssistant.Core.dll not found — build Core first: dotnet build ECAssistantCore/ECAssistant.Core.csproj -c Release"
fi
if [ -z "${TUI_DLL}" ]; then
    echo "   ⚠️ ECAssistant.TUI.dll not found — build TUI first: dotnet build ECAssistantTUI/ECAssistant.TUI.csproj -c Release"
fi

# Console lib/
CONSOLE_LIB="$HOME/Agent/ECAssistant/ECAssistantConsole/lib"
if [ -d "${CONSOLE_LIB}" ] && [ -n "${CORE_DLL}" ]; then
    cp "${CORE_DLL}" "${CONSOLE_LIB}/ECAssistant.Core.dll"
    echo "   ✅ Console/lib/ECAssistant.Core.dll"
fi
if [ -d "${CONSOLE_LIB}" ] && [ -n "${TUI_DLL}" ]; then
    cp "${TUI_DLL}" "${CONSOLE_LIB}/ECAssistant.TUI.dll"
    echo "   ✅ Console/lib/ECAssistant.TUI.dll"
fi

# ECSQL lib/
ECSQL_LIB="$HOME/Agent/ECSQL/src/EcsSql.UI/lib"
if [ -d "${ECSQL_LIB}" ] && [ -n "${CORE_DLL}" ]; then
    cp "${CORE_DLL}" "${ECSQL_LIB}/ECAssistant.Core.dll"
    echo "   ✅ ECSQL/lib/ECAssistant.Core.dll"
fi
if [ -d "${ECSQL_LIB}" ] && [ -n "${TUI_DLL}" ]; then
    cp "${TUI_DLL}" "${ECSQL_LIB}/ECAssistant.TUI.dll"
    echo "   ✅ ECSQL/lib/ECAssistant.TUI.dll"
fi

echo ""

# ── Cleanup ──
echo "🧹 Cleaning up..."
rm -rf "${TEMP_DIR}"
echo ""

echo "╔══════════════════════════════════════════════════════════╗"
echo "║  ✅ DLL SWAP COMPLETE                                     ║"
echo "╠══════════════════════════════════════════════════════════╣"
echo "║  All Vulkan DLLs replaced across all projects.          ║"
echo "║                                                          ║"
echo "║  What was swapped:                                       ║"
echo "║  • NuGet cache (survives 'dotnet restore')               ║"
echo "║  • ECAssistantTUI (Debug + Release)                      ║"
echo "║  • ECAssistantConsole                                   ║"
echo "║  • ECAssistantCore + Tests                              ║"
echo "║  • ECSQL UI + Tests                                      ║"
echo "║  • Core/TUI output → Console + ECSQL lib/ folders         ║"
echo "║                                                          ║"
echo "║  Next steps:                                             ║"
echo "║  1. Rebuild Core + TUI (dotnet build -c Release)          ║"
echo "║     — DLLs from NuGet cache propagate to bin/             ║"
echo "║  2. Re-run this script to copy Core/TUI → lib/ folders   ║"
echo "║  3. Copy bin/Release to Windows                          ║"
echo "║  4. Set gpu_layers > 0 in appsettings.json               ║"
echo "║  5. Test: short prompt first, then longer one            ║"
echo "║  6. If crash → 'dotnet restore' resets to originals      ║"
echo "║                                                          ║"
echo "║  Restore originals:                                      ║"
echo "║  dotnet restore (overwrites NuGet cache + bin output)    ║"
echo "╚══════════════════════════════════════════════════════════╝"