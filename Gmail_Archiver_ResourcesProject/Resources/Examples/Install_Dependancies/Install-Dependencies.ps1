<#
.SYNOPSIS
    Installs and reconciles all NuGet dependencies for the IVolt Gmail IMAP Archiver.
    Does NOT build. Leaves the project ready for you to compile in your IDE.

.DESCRIPTION
    - Verifies the .NET SDK (>= 8.0) is installed and on PATH.
    - Locates the .csproj (auto-detects or use -ProjectPath).
    - Ensures every required PackageReference exists at the pinned version,
      adding any that are missing via 'dotnet add package'.
    - Runs 'dotnet restore' to download and reconcile the full dependency graph.
    - Reports a clear pass/fail summary. No build is performed.

.PARAMETER ProjectPath
    Path to the .csproj (or a folder containing exactly one). If omitted, the
    script searches the current directory tree for Email_Archiver.csproj.

.PARAMETER Offline
    Restore from local NuGet caches only (no network). Use on air-gapped machines
    where packages are already cached.

.EXAMPLE
    .\Install-Dependencies.ps1
.EXAMPLE
    .\Install-Dependencies.ps1 -ProjectPath .\Email_Archiver.csproj
#>

[CmdletBinding()]
param(
    [string]$ProjectPath,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'

# ---- Pinned dependency set (must match the .csproj) ---------------------
# Order matters only for readability; restore resolves the full graph.
$Packages = @(
    @{ Id = 'MailKit';                                     Version = '4.7.1.1'          },
    @{ Id = 'Newtonsoft.Json';                             Version = '13.0.3'           },
    @{ Id = 'Lucene.Net';                                  Version = '4.8.0-beta00016'  },
    @{ Id = 'Lucene.Net.Analysis.Common';                  Version = '4.8.0-beta00016'  },
    @{ Id = 'Lucene.Net.QueryParser';                      Version = '4.8.0-beta00016'  },
    @{ Id = 'PdfPig';                                      Version = '0.1.9'            },
    @{ Id = 'DocumentFormat.OpenXml';                      Version = '3.1.0'            },
    @{ Id = 'System.Security.Cryptography.ProtectedData';  Version = '8.0.0'            }
)

function Write-Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    !   $msg" -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    x   $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host "  IVolt Gmail IMAP Archiver - Dependency Installer" -ForegroundColor White
Write-Host "  (install + restore only; no build)" -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host ""

# ---- 1. Verify the .NET SDK --------------------------------------------
Write-Step "Checking .NET SDK"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Err2 "The 'dotnet' CLI was not found on PATH."
    Write-Host  "        Install the .NET 8 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Gray
    exit 1
}
try {
    $sdkVersion = (& dotnet --version).Trim()
    Write-Ok "dotnet CLI version $sdkVersion"
} catch {
    Write-Err2 "Could not run 'dotnet --version': $($_.Exception.Message)"
    exit 1
}

# Require an installed SDK major >= 8.
$sdks = (& dotnet --list-sdks) 2>$null
$has8 = $false
foreach ($line in $sdks) {
    if ($line -match '^\s*(\d+)\.') { if ([int]$Matches[1] -ge 8) { $has8 = $true } }
}
if ($has8) { Write-Ok "A .NET 8+ SDK is installed." }
else {
    Write-Err2 "No .NET 8.0+ SDK found. Installed SDKs:"
    $sdks | ForEach-Object { Write-Host "        $_" -ForegroundColor Gray }
    Write-Host  "        Install the .NET 8 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Gray
    exit 1
}

# ---- 2. Locate the project ---------------------------------------------
Write-Step "Locating the project file"
if (-not $ProjectPath) {
    $candidates = Get-ChildItem -Path (Get-Location) -Recurse -Filter 'Email_Archiver_Lib.csproj' -ErrorAction SilentlyContinue
    if (-not $candidates -or $candidates.Count -eq 0) {
        # Fall back to any single .csproj in the current directory.
        $candidates = Get-ChildItem -Path (Get-Location) -Filter '*.csproj' -ErrorAction SilentlyContinue
    }
    if (-not $candidates -or $candidates.Count -eq 0) {
        Write-Err2 "No .csproj found. Run this from the project folder or pass -ProjectPath."
        exit 1
    }
    if ($candidates.Count -gt 1) {
        Write-Err2 "Multiple .csproj files found; specify one with -ProjectPath:"
        $candidates | ForEach-Object { Write-Host "        $($_.FullName)" -ForegroundColor Gray }
        exit 1
    }
    $ProjectPath = $candidates[0].FullName
}
if (-not (Test-Path $ProjectPath)) {
    Write-Err2 "Project not found: $ProjectPath"
    exit 1
}
$ProjectPath = (Resolve-Path $ProjectPath).Path
Write-Ok "Project: $ProjectPath"

# ---- 3. Read existing PackageReferences --------------------------------
Write-Step "Reading existing package references"
[xml]$proj = Get-Content $ProjectPath
$existing = @{}
$refNodes = $proj.SelectNodes('//PackageReference')
foreach ($n in $refNodes) {
    if ($n.Include) { $existing[$n.Include] = $n.Version }
}
if ($existing.Count -gt 0) {
    foreach ($k in $existing.Keys) { Write-Host "    - $k ($($existing[$k]))" -ForegroundColor Gray }
} else {
    Write-Warn2 "No PackageReferences currently declared; all will be added."
}

# ---- 4. Add any missing / mismatched packages --------------------------
Write-Step "Reconciling required packages"
$added = 0
foreach ($pkg in $Packages) {
    $id  = $pkg.Id
    $ver = $pkg.Version
    $have = $existing.ContainsKey($id)
    $verMatch = $have -and ($existing[$id] -eq $ver)

    if ($verMatch) {
        Write-Ok "$id $ver already declared."
        continue
    }

    if ($have) {
        Write-Warn2 "$id is at $($existing[$id]); pinning to $ver."
    } else {
        Write-Warn2 "$id missing; adding $ver."
    }

    $args = @('add', $ProjectPath, 'package', $id, '--version', $ver, '--no-restore')
    try {
        & dotnet @args | Out-Null
        Write-Ok "Declared $id $ver."
        $added++
    } catch {
        Write-Err2 "Failed to add ${id}: $($_.Exception.Message)"
    }
}
if ($added -eq 0) { Write-Ok "All required packages already declared at pinned versions." }

# ---- 5. Restore ---------------------------------------------------------
Write-Step "Restoring the dependency graph (dotnet restore)"
$restoreArgs = @('restore', $ProjectPath)
if ($Offline) { $restoreArgs += '--source'; $restoreArgs += (Join-Path $env:USERPROFILE '.nuget\packages') }
try {
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore exited with code $LASTEXITCODE." }
    Write-Ok "Restore completed."
} catch {
    Write-Err2 "Restore failed: $($_.Exception.Message)"
    Write-Host  "        If offline, pre-populate the NuGet cache and re-run with -Offline." -ForegroundColor Gray
    exit 1
}

# ---- 6. Summary ---------------------------------------------------------
Write-Host ""
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host "  Dependency installation complete." -ForegroundColor White
Write-Host "  Packages are restored. No build was performed (by design)." -ForegroundColor Gray
Write-Host "  Open the project in your IDE and build when ready." -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor DarkCyan
Write-Host ""
Write-Host "Restored packages:" -ForegroundColor White
foreach ($pkg in $Packages) { Write-Host "  - $($pkg.Id) $($pkg.Version)" -ForegroundColor Gray }
Write-Host ""
exit 0
