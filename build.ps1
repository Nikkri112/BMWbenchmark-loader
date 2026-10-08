$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$src = Get-ChildItem "$PSScriptRoot\src" -Filter "*.cs" | ForEach-Object { $_.FullName }
$out = "$PSScriptRoot\BMWBenchmarkLoader.exe"
& $csc /nologo /o+ /target:exe /platform:anycpu /r:System.Management.dll /r:System.Drawing.dll /out:$out $src
if (Test-Path $out) { Write-Output "BUILD OK: $out" } else { Write-Output "BUILD FAILED"; exit 1 }
