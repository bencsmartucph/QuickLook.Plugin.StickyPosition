# Drives a running QuickLook through a list of preview steps, with no
# synthesised input: a magenta backdrop window is made foreground on a chosen
# monitor (QuickLook opens a new preview on the foreground window's monitor),
# and previews are toggled over QuickLook's own pipe. Captures show only the
# preview against magenta, never other windows.
#
#   .\scripts\preview-steps.ps1 -Steps 'm2,t:C:\x\test.md,tree,cap:C:\tmp\a.png,c'
#
# Steps, comma-separated:
#   m<N>         backdrop over monitor N (1 = primary) and make it foreground
#   t:<file>     toggle a preview of file (waits 2.5 s)
#   c            close the preview
#   tree         print the preview window and its children: rect and DPI
#   cap:<png>    capture the preview window
#   mv:<x>/<y>   move the open preview to x,y (physical pixels)
#   w:<ms>       wait
# Monitors are listed first, primary first, in physical pixels.
param([Parameter(Mandatory = $true)][string]$Steps)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Core
Add-Type -Path (Join-Path $PSScriptRoot 'Monitors.cs')
[QL]::Aware()

$monitors = [QL]::Monitors()
for ($i = 0; $i -lt $monitors.Count; $i++) {
    $m = $monitors[$i]
    'monitor {0}: {1},{2} {3}x{4} dpi {5}{6}' -f ($i + 1), $m.Work.l, $m.Work.t, ($m.Work.r - $m.Work.l), ($m.Work.b - $m.Work.t), $m.Dpi, $(if ($m.Primary) { ' primary' } else { '' })
}
$ql = Get-Process QuickLook -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ql) { throw 'QuickLook is not running.' }
$qlpid = [uint32]$ql.Id
$pipe = 'QuickLook.App.Pipe.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Send($msg, $path) {
    $c = New-Object IO.Pipes.NamedPipeClientStream('.', $pipe, [IO.Pipes.PipeDirection]::Out)
    $c.Connect(3000)
    $w = New-Object IO.StreamWriter($c)
    $w.WriteLine('QuickLook.App.PipeMessages.' + $msg + '|' + $path + '|')
    $w.Flush()
    $c.Dispose()
}

function Capture($out) {
    $h = [QL]::Viewer($qlpid)
    if ($h -eq [IntPtr]::Zero) { Write-Host "cap: no preview open"; return }
    $r = New-Object QL+RECT
    [QL]::GetWindowRect($h, [ref]$r) | Out-Null
    $b = New-Object Drawing.Bitmap ($r.r - $r.l), ($r.b - $r.t)
    $g = [Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen($r.l, $r.t, 0, 0, $b.Size)
    $b.Save($out)
    Write-Host "cap $out"
}

$f = New-Object Windows.Forms.Form
$f.Text = 'StickyPosition test'
$f.StartPosition = 'Manual'
$f.FormBorderStyle = 'None'
$f.BackColor = [Drawing.Color]::Magenta
$f.ShowInTaskbar = $false
function Cover($n) {
    $m = $monitors[$n - 1]
    $f.Location = New-Object Drawing.Point($m.Work.l, $m.Work.t)
    $f.Size = New-Object Drawing.Size(($m.Work.r - $m.Work.l), ($m.Work.b - $m.Work.t))
}
$queue = New-Object Collections.Queue
foreach ($s in $Steps.Split(',')) { $queue.Enqueue($s.Trim()) }
# A new window gets focus when it first opens, so open it on the first monitor used.
$first = @($queue | Where-Object { $_ -match '^m\d+$' } | Select-Object -First 1)
Cover $(if ($first) { [int]$first[0].Substring(1) } else { 1 })

$t = New-Object Windows.Forms.Timer
$t.Interval = 300
$t.add_Tick({
    if ($queue.Count -eq 0) { $t.Stop(); $f.Close(); return }
    $s = [string]$queue.Dequeue()
    $t.Interval = 700
    try {
        if ($s -match '^m(\d+)$') {
            Cover ([int]$Matches[1])
            # Windows refuses focus to a background window while you are typing
            # (focus-stealing protection). Retry briefly; a preview toggled now
            # would open on the wrong monitor, so stop instead of guessing.
            for ($i = 0; $i -lt 10 -and [QL]::GetForegroundWindow() -ne $f.Handle; $i++) {
                $f.Activate()
                [QL]::SetForegroundWindow($f.Handle) | Out-Null
                Start-Sleep -Milliseconds 300
            }
            if ([QL]::GetForegroundWindow() -ne $f.Handle) {
                Write-Host "stopped: the backdrop on monitor $($Matches[1]) could not take focus (Windows blocks it while you type). Run again when idle."
                $queue.Clear()
            }
        }
        elseif ($s.StartsWith('t:')) { Send 'Toggle' $s.Substring(2); $t.Interval = 2500 }
        elseif ($s -eq 'c') { Send 'Close' ''; $t.Interval = 1000 }
        elseif ($s -eq 'tree') {
            $h = [QL]::Viewer($qlpid)
            if ($h -eq [IntPtr]::Zero) { Write-Host 'tree: no preview open' } else { Write-Host ([QL]::Tree($h)) }
        }
        elseif ($s.StartsWith('cap:')) { Capture $s.Substring(4) }
        elseif ($s -match '^mv:(-?\d+)/(-?\d+)$') {
            [QL]::SetWindowPos([QL]::Viewer($qlpid), [IntPtr]::Zero, [int]$Matches[1], [int]$Matches[2], 0, 0, 0x15) | Out-Null
            $t.Interval = 1500
        }
        elseif ($s -match '^w:(\d+)$') { $t.Interval = [int]$Matches[1] }
        else { Write-Host "unknown step: $s" }
    }
    catch { Write-Host "step $s failed: $_" }
})
$f.add_Shown({ $f.Activate(); $t.Start() })
[Windows.Forms.Application]::Run($f)
