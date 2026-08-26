#!/bin/bash
# ── ECAssistant Build Script ──
# Rebuilds all 4 projects in dependency order and syncs DLLs.
#
# Usage:
#   ./build.sh          — build all
#   ./build.sh --clean  — clean + build all
#   ./build.sh --no-llm — skip LLM server (saves time when only Core/TUI/Console changed)

set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
CORE="$ROOT/ECAssistantCore"
TUI="$ROOT/ECAssistantTUI"
LLM="$ROOT/ECAssistantLLM"
CONSOLE="$ROOT/ECAssistantConsole"

CLEAN=false
SKIP_LLM=false

for arg in "$@"; do
  case "$arg" in
    --clean)   CLEAN=true ;;
    --no-llm)  SKIP_LLM=true ;;
  esac
done

echo "═══════════════════════════════════════════════════════════════"
echo "  ECAssistant Build — $(date '+%Y-%m-%d %H:%M:%S')"
echo "═══════════════════════════════════════════════════════════════"
echo ""

# ── Step 1: Clean (optional) ──
if $CLEAN; then
  echo "🧹 Cleaning all projects..."
  dotnet clean "$CORE/ECAssistant.Core.csproj" --nologo -v q 2>&1 | tail -1
  dotnet clean "$TUI/ECAssistant.TUI.csproj" --nologo -v q 2>&1 | tail -1
  if ! $SKIP_LLM; then
    dotnet clean "$LLM/ECAssistant.LLM.csproj" --nologo -v q 2>&1 | tail -1
  fi
  dotnet clean "$CONSOLE/ECAssistantConsole.csproj" --nologo -v q 2>&1 | tail -1
  echo ""
fi

# ── Step 2: Build Core (no dependencies) ──
echo "📦 [1/4] Building ECAssistantCore..."
dotnet build "$CORE/ECAssistant.Core.csproj" --nologo -v q 2>&1 | tail -1
CORE_DLL="$CORE/bin/Debug/net8.0/ECAssistant.Core.dll"
if [ ! -f "$CORE_DLL" ]; then
  echo "❌ Core build failed — aborting."
  exit 1
fi
echo "   ✅ Core: $(stat -f '%Sm' "$CORE_DLL")"

# ── Step 3: Sync Core DLL to TUI lib ──
echo "🔄 [2/4] Syncing Core DLL to TUI/lib/..."
cp "$CORE_DLL" "$TUI/lib/ECAssistant.Core.dll"
echo "   ✅ TUI/lib/ECAssistant.Core.dll updated"

# ── Step 4: Build TUI (depends on Core) ──
echo "📦 [3/4] Building ECAssistantTUI..."
dotnet build "$TUI/ECAssistant.TUI.csproj" --nologo -v q 2>&1 | tail -1
TUI_DLL="$TUI/bin/Debug/net8.0/ECAssistant.TUI.dll"
if [ ! -f "$TUI_DLL" ]; then
  echo "❌ TUI build failed — aborting."
  exit 1
fi
echo "   ✅ TUI: $(stat -f '%Sm' "$TUI_DLL")"

# ── Step 5: Sync Core + TUI DLLs to Console lib ──
echo "🔄 [4/4] Syncing Core + TUI DLLs to Console/lib/..."
cp "$CORE_DLL" "$CONSOLE/lib/ECAssistant.Core.dll"
cp "$TUI_DLL" "$CONSOLE/lib/ECAssistant.TUI.dll"
echo "   ✅ Console/lib/ updated"

# ── Step 6: Build LLM (independent, skippable) ──
if ! $SKIP_LLM; then
  echo "📦 Building ECAssistantLLM..."
  dotnet build "$LLM/ECAssistant.LLM.csproj" --nologo -v q 2>&1 | tail -1
  echo "   ✅ LLM: $(stat -f '%Sm' "$LLM/bin/Debug/net8.0/ECAssistant.LLM.dll" 2>/dev/null || echo 'N/A')"
fi

# ── Step 7: Build Console (depends on Core + TUI) ──
echo "📦 Building ECAssistantConsole..."
dotnet build "$CONSOLE/ECAssistantConsole.csproj" --nologo -v q 2>&1 | tail -1
echo "   ✅ Console: $(stat -f '%Sm' "$CONSOLE/bin/Debug/net8.0/ecassistant.dll" 2>/dev/null || echo 'N/A')"

# ── Summary ──
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "  Build complete!"
echo ""
echo "  Run: cd $CONSOLE && dotnet run --no-build"
echo "═══════════════════════════════════════════════════════════════"