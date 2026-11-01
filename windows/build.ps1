# Build Keyboard Switch with the C# compiler that ships with Windows (.NET Framework 4.8).
#   .\build.ps1                  dist\KeyboardSwitch.exe
#   .\build.ps1 -Test            also build and run the unit tests
#   .\build.ps1 -E2E             also build dist\E2E.exe (real input against a test window)
#   .\build.ps1 -Version 1.2.0   set the file version (the release workflow passes the tag)
param([switch]$Test, [switch]$E2E, [string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x compiler not found: $csc" }
New-Item -ItemType Directory -Force dist | Out-Null

$common = @('/nologo', '/codepage:65001', '/optimize+', '/warn:4',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll')

function Compile($out, $target, $sources, $extra = @()) {
    & $csc @common "/target:$target" "/out:$out" @extra @sources
    if ($LASTEXITCODE -ne 0) { throw "Compiling $out failed" }
}

$app = Get-ChildItem src\*.cs | ForEach-Object FullName
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'" }
    $info = Join-Path (Resolve-Path dist) 'AssemblyInfo.cs'
    (Get-Content src\AssemblyInfo.cs -Raw) -replace '"1\.0\.0\.0"', "`"$Version.0`"" | Set-Content -Encoding utf8 $info
    $app = @($app | Where-Object { (Split-Path $_ -Leaf) -ne 'AssemblyInfo.cs' }) + $info
}
Compile 'dist\KeyboardSwitch.exe' 'winexe' $app @('/win32icon:assets\icon.ico', '/win32manifest:src\app.manifest',
    '/resource:assets\icon.ico,KeyboardSwitch.icon.ico')
'Built dist\KeyboardSwitch.exe {0} ({1:N0} KB)' -f (Get-Item dist\KeyboardSwitch.exe).VersionInfo.FileVersion, ((Get-Item dist\KeyboardSwitch.exe).Length / 1KB)

if ($Test) {
    Compile 'dist\Tests.exe' 'exe' @('src\Converter.cs', 'src\Engine.cs', 'tests\Fakes.cs', 'tests\Tests.cs')
    & dist\Tests.exe tests\fixtures\layouts.json
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

if ($E2E) {
    $lib = $app | Where-Object { (Split-Path $_ -Leaf) -ne 'Program.cs' }
    Compile 'dist\E2E.exe' 'exe' ($lib + 'tests\E2E.cs') @('/resource:assets\icon.ico,KeyboardSwitch.icon.ico')
    'Built dist\E2E.exe'
}
