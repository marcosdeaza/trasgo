<#
  Builds dist\trasgo.exe with the Roslyn compiler against the .NET Framework 4.8 that ships with Windows.
  No Visual Studio, no .NET SDK: the first run downloads the compiler package from nuget.org (~20 MB) and caches it.

    .\build.ps1            # release build
    .\build.ps1 -Test      # build + selftest
#>
param([switch]$Test, [switch]$Debug)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA 'Trasgo-build'
$roslynVersion = '5.9.0'
$csc = Join-Path $cache "roslyn-$roslynVersion\tasks\net472\csc.exe"

if (-not (Test-Path $csc)) {
    Write-Host "Descargando compilador Roslyn $roslynVersion (una sola vez)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = Join-Path $cache "roslyn-$roslynVersion.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$roslynVersion/microsoft.net.compilers.toolset.$roslynVersion.nupkg" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $cache "roslyn-$roslynVersion") -Force
    Remove-Item $zip
}

$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Security.dll',
          'System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','WPF\System.Speech.dll') | ForEach-Object { "/r:" + (Join-Path $fx $_) }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$ico = Join-Path $root 'assets\trasgo.ico'
$opts = @('/nologo', '/target:winexe', '/platform:anycpu', '/langversion:latest', '/utf8output', '/codepage:65001', '/nowarn:1701,1702',
          "/win32manifest:$(Join-Path $root 'src\app.manifest')", "/out:$(Join-Path $dist 'trasgo.exe')")
if ($Debug) { $opts += @('/debug:portable', '/define:DEBUG') } else { $opts += @('/optimize+', '/debug-') }
if (Test-Path $ico) { $opts += "/win32icon:$ico" }
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName

# build at low priority so it never steals frames from a game
$rsp = Join-Path $env:TEMP "trasgo-build-$PID.rsp"
($opts + $refs + ($src | ForEach-Object { '"' + $_ + '"' })) | Set-Content $rsp -Encoding UTF8
$p = Start-Process -FilePath $csc -ArgumentList "@`"$rsp`"" -NoNewWindow -PassThru -RedirectStandardOutput "$rsp.out" -RedirectStandardError "$rsp.err"
try { $p.PriorityClass = 'BelowNormal' } catch {}
$p.WaitForExit()
Get-Content "$rsp.out", "$rsp.err" -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object { Write-Host $_ }
$code = $p.ExitCode
Remove-Item $rsp, "$rsp.out", "$rsp.err" -ErrorAction SilentlyContinue
if ($code -ne 0) { throw "Compilación fallida ($code)" }

if (-not (Test-Path $ico)) {
    # first build: generate the icon from the sprite and rebuild with it
    New-Item -ItemType Directory -Force (Split-Path $ico) | Out-Null
    & (Join-Path $dist 'trasgo.exe') icon $ico | Out-Host
    & $PSCommandPath @PSBoundParameters
    return
}
$size = [math]::Round((Get-Item (Join-Path $dist 'trasgo.exe')).Length / 1KB)
Write-Host "OK  dist\trasgo.exe  ($size KB)" -ForegroundColor Green
if ($Test) { & (Join-Path $dist 'trasgo.exe') selftest | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'selftest falló' } }
