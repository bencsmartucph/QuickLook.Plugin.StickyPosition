# Builds a logging copy of the plugin and installs it into the Store QuickLook:
# Plugin.cs is copied to a temp folder, WndProc is wired to scripts\Debug.cs, and
# every placement message is written to Public\Documents\sp-debug.log.
# Plugin.cs itself is not changed. Restarts QuickLook (open and pinned previews close).
#   .\scripts\build-debug.ps1            install the logging build
#   .\scripts\build-debug.ps1 -Restore   put the normal build back
param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$name = 'QuickLook.Plugin.StickyPosition'
$pkg = Get-AppxPackage -Name '21090PaddyXu.QuickLook'
if (-not $pkg) { throw 'QuickLook Store app not found.' }
$dest = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Roaming\pooi.moe\QuickLook\QuickLook.Plugin\$name"

function Restart-QuickLook($copy) {
    Get-Process QuickLook -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    & $copy
    Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!Main"
}

if ($Restore) {
    & (Join-Path $repo 'build.ps1') | Out-Null
    Restart-QuickLook { Copy-Item (Join-Path $repo "bin\$name.dll") $dest -Force }
    'Normal build reinstalled; QuickLook restarted.'
    return
}

$tmp = Join-Path $env:TEMP "$name-debug"
New-Item -ItemType Directory -Force $tmp | Out-Null
$src = [IO.File]::ReadAllText((Join-Path $repo 'Plugin.cs'))
$head = '(private static IntPtr WndProc\(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled\)\s*\{)'
$tail = '(\r?\n            return IntPtr\.Zero;\r?\n        \})'
if ($src -notmatch $head -or $src -notmatch $tail) { throw 'WndProc has changed shape; update the anchors in build-debug.ps1.' }
$src = $src -replace $head, '$1 var dbgBefore = msg == 0x0046 ? Dbg.Pos(lParam) : null;'
$src = $src -replace $tail, ("`n            try { Dbg.Message(hwnd, msg, wParam, lParam, dbgBefore); } catch { }" + '$1')
[IO.File]::WriteAllText((Join-Path $tmp 'Plugin.cs'), $src)

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$common = Join-Path $pkg.InstallLocation 'Package\QuickLook.Common.dll'
& $csc /nologo /target:library /optimize+ /out:"$tmp\$name.dll" `
    /reference:"$common" `
    /reference:"$wpf\PresentationFramework.dll" `
    /reference:"$wpf\PresentationCore.dll" `
    /reference:"$wpf\WindowsBase.dll" `
    /reference:System.Xaml.dll `
    (Join-Path $tmp 'Plugin.cs') (Join-Path $PSScriptRoot 'Debug.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

Restart-QuickLook { Copy-Item "$tmp\$name.dll" $dest -Force }
$log = Join-Path ([Environment]::GetFolderPath('CommonDocuments')) 'sp-debug.log'
"Logging build installed; QuickLook restarted. Log: $log"
'Run .\scripts\build-debug.ps1 -Restore when done.'
