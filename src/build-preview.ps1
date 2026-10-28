# Build a non-elevated preview exe (asInvoker) for UI screenshots
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$out = Join-Path $dir 'preview.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /target:winexe /platform:anycpu /codepage:65001 /optimize+ /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$out" "$dir\Program.cs"
if ($LASTEXITCODE -ne 0) { Write-Host 'PREVIEW BUILD FAILED'; exit 1 }
Write-Host ('PREVIEW OK -> ' + $out)
