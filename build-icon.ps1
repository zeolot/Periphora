$ErrorActionPreference = 'Stop'

# Rebuilds every icon the project ships from tools\MakeIcon.cs, so the taskbar
# icon, the README logo and the Linux window icon can never drift apart.

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src  = Join-Path $root 'tools\MakeIcon.cs'
$exe  = Join-Path $root 'tools\MakeIcon.exe'

& $csc /nologo /target:exe /optimize+ /out:$exe /r:System.dll /r:System.Drawing.dll $src
if ($LASTEXITCODE -ne 0) { throw 'MakeIcon compile failed' }

# Windows shell icon, embedded by build.ps1 and by Periphora.Linux.csproj.
& $exe (Join-Path $root 'periphora.ico') | Out-Null

# Same mark at the sizes the repo and the desktop entry need.
& $exe (Join-Path $root 'docs\icon.png') 512 | Out-Null
& $exe (Join-Path $root 'linux\Assets\periphora.png') 256 | Out-Null

# Upstream's original icon file, kept in step for anyone who builds it directly.
& $exe (Join-Path $root 'app.ico') | Out-Null

foreach ($f in 'periphora.ico', 'app.ico', 'docs\icon.png', 'linux\Assets\periphora.png') {
    $p = Join-Path $root $f
    Write-Host ("{0,-28} {1,9:N0} bytes" -f $f, (Get-Item $p).Length)
}
