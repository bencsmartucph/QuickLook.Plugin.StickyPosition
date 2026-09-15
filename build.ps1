# Builds QuickLook.Plugin.StickyPosition with the C# compiler that ships with the
# .NET Framework (no SDK needed), against the QuickLook.Common.dll of your
# installed QuickLook, and packages it as a .qlplugin.
#   .\build.ps1            build bin\ and dist\QuickLook.Plugin.StickyPosition.qlplugin
#   .\build.ps1 -Install   also copy it into QuickLook's plugin folder (then restart QuickLook)
#   .\build.ps1 -QuickLookDir 'D:\QuickLook'   portable QuickLook instead of the Store app
param([switch]$Install, [string]$QuickLookDir)
$ErrorActionPreference = 'Stop'
$name = 'QuickLook.Plugin.StickyPosition'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'

if ($QuickLookDir) {
    $common = Join-Path $QuickLookDir 'QuickLook.Common.dll'
    $plugins = Join-Path $QuickLookDir 'UserData\QuickLook.Plugin'
} else {
    $pkg = Get-AppxPackage -Name '21090PaddyXu.QuickLook'
    if (-not $pkg) { throw 'QuickLook Store app not found; pass -QuickLookDir for a portable install.' }
    $common = Join-Path $pkg.InstallLocation 'Package\QuickLook.Common.dll'
    $plugins = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Roaming\pooi.moe\QuickLook\QuickLook.Plugin"
}

$bin = Join-Path $PSScriptRoot 'bin'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $bin, $dist | Out-Null
& $csc /nologo /target:library /optimize+ /out:"$bin\$name.dll" `
    /reference:"$common" `
    /reference:"$wpf\PresentationFramework.dll" `
    /reference:"$wpf\PresentationCore.dll" `
    /reference:"$wpf\WindowsBase.dll" `
    /reference:System.Xaml.dll `
    (Join-Path $PSScriptRoot 'Plugin.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot 'QuickLook.Plugin.Metadata.config') $bin -Force

# A .qlplugin is a zip of the plugin folder's contents.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pack = Join-Path $dist "$name.qlplugin"
if (Test-Path $pack) { Remove-Item $pack }
[IO.Compression.ZipFile]::CreateFromDirectory($bin, $pack)
"Built $pack"

if ($Install) {
    $dest = Join-Path $plugins $name
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$bin\*" $dest -Force
    "Installed to $dest -- exit and relaunch QuickLook to load it."
}
