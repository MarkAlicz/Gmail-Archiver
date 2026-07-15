#!/usr/bin/env bash
# ============================================================
#  IVolt Gmail IMAP Archiver - Dependency Installer (bash)
#  Installs/restores NuGet packages. No build is performed.
#  Note: the app itself is Windows-only (DPAPI); this script
#  exists for CI or cross-platform restore convenience.
# ============================================================
set -euo pipefail

PROJECT_PATH="${1:-}"

# Pinned set (must match the .csproj). Format: "Id Version".
PACKAGES=(
  "MailKit 4.7.1.1"
  "Newtonsoft.Json 13.0.3"
  "Lucene.Net 4.8.0-beta00016"
  "Lucene.Net.Analysis.Common 4.8.0-beta00016"
  "Lucene.Net.QueryParser 4.8.0-beta00016"
  "PdfPig 0.1.9"
  "DocumentFormat.OpenXml 3.1.0"
  "System.Security.Cryptography.ProtectedData 8.0.0"
)

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
ok()   { printf '    \033[32mOK  %s\033[0m\n' "$1"; }
warn() { printf '    \033[33m!   %s\033[0m\n' "$1"; }
err()  { printf '    \033[31mx   %s\033[0m\n' "$1"; }

echo
echo "============================================================"
echo "  IVolt Gmail IMAP Archiver - Dependency Installer (bash)"
echo "  (install + restore only; no build)"
echo "============================================================"
echo

# 1. Verify dotnet
step "Checking .NET SDK"
if ! command -v dotnet >/dev/null 2>&1; then
  err "'dotnet' not found on PATH. Install the .NET 8 SDK."
  exit 1
fi
ok "dotnet $(dotnet --version)"
if ! dotnet --list-sdks | grep -qE '^[89]\.|^[1-9][0-9]+\.'; then
  err "No .NET 8+ SDK found."
  dotnet --list-sdks | sed 's/^/        /'
  exit 1
fi
ok "A .NET 8+ SDK is installed."

# 2. Locate project
step "Locating the project file"
if [[ -z "$PROJECT_PATH" ]]; then
  PROJECT_PATH="$(find . -name 'Email_Archiver.csproj' -print -quit 2>/dev/null || true)"
  if [[ -z "$PROJECT_PATH" ]]; then
    count=$(find . -maxdepth 1 -name '*.csproj' | wc -l | tr -d ' ')
    if [[ "$count" == "1" ]]; then PROJECT_PATH="$(find . -maxdepth 1 -name '*.csproj')"; fi
  fi
fi
if [[ -z "$PROJECT_PATH" || ! -f "$PROJECT_PATH" ]]; then
  err "No .csproj found. Pass the path as the first argument."
  exit 1
fi
ok "Project: $PROJECT_PATH"

# 3. Add missing / mismatched packages
step "Reconciling required packages"
added=0
for entry in "${PACKAGES[@]}"; do
  id="${entry% *}"; ver="${entry##* }"
  if grep -q "Include=\"$id\"" "$PROJECT_PATH" && grep -q "Version=\"$ver\"" "$PROJECT_PATH"; then
    ok "$id $ver already declared."
    continue
  fi
  warn "Declaring $id $ver."
  if dotnet add "$PROJECT_PATH" package "$id" --version "$ver" --no-restore >/dev/null 2>&1; then
    ok "Declared $id $ver."; added=$((added+1))
  else
    err "Failed to add $id."
  fi
done
[[ $added -eq 0 ]] && ok "All required packages already declared."

# 4. Restore
step "Restoring the dependency graph (dotnet restore)"
if dotnet restore "$PROJECT_PATH"; then
  ok "Restore completed."
else
  err "Restore failed."
  exit 1
fi

echo
echo "============================================================"
echo "  Dependency installation complete. No build performed."
echo "  Open the project and build when ready."
echo "============================================================"
echo
