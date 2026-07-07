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

$args = @(
  "/target:winexe",
  "/out:$dir\ClaudeQuotaMonitor.exe",
  "/win32icon:$dir\icon.ico",
  "/win32manifest:$dir\app.manifest",
  "/platform:x64",
  "/optimize+",
  "/nologo"
) + $refArgs + @("$dir\Program.cs")

Write-Host "Compiling..." -ForegroundColor Cyan
& $csc $args
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }
Write-Host "OK -> ClaudeQuotaMonitor.exe" -ForegroundColor Green
Write-Host "Make sure these sit next to the exe: dashboard.html, icon.ico, WebView2Loader.dll, Microsoft.Web.WebView2.*.dll"
