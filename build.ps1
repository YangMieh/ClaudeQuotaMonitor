# Build Claude Quota Monitor (single exe) with csc.
# Run from the project folder:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }

$refs = @(
  "System.dll",
  "System.Core.dll",
  "System.Drawing.dll",
  "System.Windows.Forms.dll",
  "System.Net.Http.dll",
  "System.Security.dll",
  "System.Web.Extensions.dll",
  "System.Xml.dll",
  "$dir\Microsoft.Web.WebView2.Core.dll",
  "$dir\Microsoft.Web.WebView2.WinForms.dll"
)
$refArgs = $refs | ForEach-Object { "/r:$_" }

# Embed everything so the exe runs as a SINGLE file (dlls + html + icon extracted/loaded at runtime).
$resArgs = @(
  "/resource:$dir\dashboard.html,dashboard.html",
  "/resource:$dir\icon.ico,icon.ico",
  "/resource:$dir\WebView2Loader.dll,WebView2Loader.dll",
  "/resource:$dir\Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.Core.dll",
  "/resource:$dir\Microsoft.Web.WebView2.WinForms.dll,Microsoft.Web.WebView2.WinForms.dll"
)
# Google OAuth client id/secret: embedded from a local gitignored file (kept out of the public repo).
if (Test-Path "$dir\gauth.txt") { $resArgs += "/resource:$dir\gauth.txt,gauth.txt" }
else { Write-Host "note: gauth.txt not found — the 'Google login sync' button will be disabled in this build." -ForegroundColor Yellow }

$args = @(
  "/target:winexe",
  "/out:$dir\ClaudeQuotaMonitor.exe",
  "/win32icon:$dir\icon.ico",
  "/win32manifest:$dir\app.manifest",
  "/platform:x64",
  "/codepage:65001",
  "/optimize+",
  "/nologo"
) + $refArgs + $resArgs + @("$dir\Program.cs")

Write-Host "Compiling (single-exe, resources embedded)..." -ForegroundColor Cyan
& $csc $args
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }
Write-Host "OK -> ClaudeQuotaMonitor.exe (single file; dashboard.html/icon/WebView2 dlls all embedded)" -ForegroundColor Green
