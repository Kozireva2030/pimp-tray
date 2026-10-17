# Builds PimpTray.exe with the C# compiler that ships with .NET Framework 4.8 (part of every Windows 10/11).
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Out path\PimpTray.exe]
param([string]$Out)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc (.NET Framework 4.8 is required)" }
if (-not $Out) { $Out = Join-Path $here 'PimpTray.exe' }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /define:PUBLIC `
    "/out:$Out" "/win32icon:$here\pmp.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$fw\System.Web.Extensions.dll" `
    "$here\PimpTray.cs"
if ($LASTEXITCODE -ne 0) { throw "csc: $LASTEXITCODE" }
"OK $Out"
