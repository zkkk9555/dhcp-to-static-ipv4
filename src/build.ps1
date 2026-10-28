# Build DHCP改静态IPV4.exe from Program.cs (run: powershell -ExecutionPolicy Bypass -File build.ps1)
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$out = Join-Path (Split-Path -Parent $dir) 'DHCP改静态IPV4.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /target:winexe /platform:anycpu /codepage:65001 /optimize+ /win32manifest:"$dir\app.manifest" /win32icon:"$dir\app.ico" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$out" "$dir\Program.cs"
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED'; exit 1 }
Write-Host ('BUILD OK -> ' + $out)
