param()
$ErrorActionPreference = 'SilentlyContinue'

$rows = @()
$conns = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue
if ($conns) {
    $groups = $conns | Group-Object OwningProcess | Sort-Object Count -Descending | Select-Object -First 15
    foreach ($g in $groups) {
        $proc = Get-Process -Id ([int]$g.Name) -ErrorAction SilentlyContinue
        $name = if ($proc) { $proc.ProcessName } else { '?' }
        $path = '?'
        try { if ($proc -and $proc.Path) { $path = $proc.Path } } catch { }
        $rows += [PSCustomObject]@{
            PID   = [int]$g.Name
            Proc  = $name
            Conn  = $g.Count
            Path  = $path
        }
    }
}

if ($rows.Count -eq 0) {
    Write-Output '(没有已建立的 TCP 连接)'
    exit 0
}

$rows | Format-Table -AutoSize | Out-String -Width 150 | Write-Output
