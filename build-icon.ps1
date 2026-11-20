$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src  = Join-Path $root 'tools\MakeIcon.cs'
$exe  = Join-Path $root 'tools\MakeIcon.exe'
$ico  = Join-Path $root 'periphora.ico'
& $csc /nologo /target:exe /out:$exe /r:System.dll /r:System.Drawing.dll $src
if ($LASTEXITCODE -ne 0) { throw 'MakeIcon compile failed' }
& $exe $ico
Write-Host ("icon -> {0}  ({1:N0} bytes)" -f $ico, (Get-Item $ico).Length)
