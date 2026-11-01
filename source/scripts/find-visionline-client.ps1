# Разведка VisionLine на ПК стойки регистрации.
# Запускать НА ФРОНТ-ДЕСКЕ (где Logus пишет ключи) под администратором:
#   powershell -ExecutionPolicy Bypass -File .\find-visionline-client.ps1
#
# Показывает:
#   1. Доступность сервера VisionLine (192.168.4.55) и его портов
#   2. Какой ПРОЦЕСС держит соединение с PMS-портом 4000 — это и есть
#      локальный клиент записи ключей (кандидат на установку на киоск)
#   3. Открытые listen-порты локальных клиентов (REST/SOAP мосты)

$ErrorActionPreference = 'SilentlyContinue'

$vlServer = '192.168.4.55'
$ports = @(4000, 27015, 3001, 7799, 7788, 135)

Write-Host "=== 1. Порты сервера VisionLine ($vlServer) ==="
foreach ($p in $ports) {
    $c = New-Object Net.Sockets.TcpClient
    $r = $c.BeginConnect($vlServer, $p, $null, $null)
    $ok = $r.AsyncWaitHandle.WaitOne(2500)
    if ($ok -and $c.Connected) { Write-Host "  ПОРТ $p : ОТКРЫТ" -ForegroundColor Green }
    else { Write-Host "  порт $p : нет" -ForegroundColor DarkGray }
    $c.Close()
}

Write-Host "`n=== 2. Кто соединён с ${vlServer}:4000 (PMS-порт) ==="
$conns = Get-NetTCPConnection -RemotePort 4000 -ErrorAction SilentlyContinue |
    Where-Object { $_.RemoteAddress -eq $vlServer -or $_.State -eq 'Established' }
foreach ($cn in $conns) {
    $proc = Get-Process -Id $cn.OwningProcess -ErrorAction SilentlyContinue
    "{0,-8} {1,-12} {2}" -f $cn.State, $proc.ProcessName, $proc.Path
}
if (-not $conns) {
    Write-Host "  Соединений сейчас нет. Откройте в Logus модуль записи ключей"
    Write-Host "  (или сделайте тестовую карту) и сразу перезапустите скрипт."
}

Write-Host "`n=== 3. Все TCP-соединения процесса(ов) Logus ==="
$logusProcs = Get-Process | Where-Object { $_.ProcessName -match 'logus|keylock|hms' }
foreach ($lp in $logusProcs) {
    Write-Host ("  Процесс: {0} (PID {1})  {2}" -f $lp.ProcessName, $lp.Id, $lp.Path)
    Get-NetTCPConnection -OwningProcess $lp.Id -State Established,Listen -ErrorAction SilentlyContinue |
        ForEach-Object { "     {0} {1}:{2} -> {3}:{4}" -f $_.State, $_.LocalAddress, $_.LocalPort, $_.RemoteAddress, $_.RemotePort }
}

Write-Host "`n=== 4. Локальные listen-порты (кандидаты на HTTP-мосты энкодера) ==="
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -notin @('::1') } |
    Group-Object LocalPort | ForEach-Object {
        $port = [int]$_.Name
        if ($port -gt 1000) {
            $pid2 = ($_.Group | Select-Object -First 1).OwningProcess
            $pn = (Get-Process -Id $pid2 -ErrorAction SilentlyContinue).ProcessName
            "  :{0,-6} {1}" -f $port, $pn
        }
    }

Write-Host "`nГотово. Пришлите весь вывод разработчику киоска."
