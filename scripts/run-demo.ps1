# run-demo.ps1
#
# 1. Opreste orice server WPF care ruleaza
# 2. Porneste serverul WPF cu --autostart
# 3. Asteapta ca portul sa fie LISTENING
# 4. Lanseaza 3 masini prin COM
# 5. Opreste serverul la final (sau cu -KeepServer)

param(
    [string]$BindHost = "127.0.0.1",
    [int]$Port = 8888,
    [int]$DurationSec = 30,
    [string]$ServerExe = "",       # daca gol, il cauta in bin\Debug
    [switch]$KeepServer,           # nu opri serverul la final
    [switch]$Headless              # nu porni WPF deloc, doar server headless prin COM
)

$ErrorActionPreference = "Stop"

$scriptRoot   = $PSScriptRoot
$solutionRoot = Split-Path $scriptRoot -Parent
$progId = "NffiTracking.Tracker"

if ([string]::IsNullOrWhiteSpace($ServerExe)) {
    $ServerExe = Join-Path $solutionRoot "NffiTrackingSystem.Server\bin\Debug\net8.0-windows\NffiTrackingSystem.Server.exe"
}

function Write-Step { param([string]$Msg) Write-Host "`n>>> $Msg" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Msg) Write-Host "    OK: $Msg" -ForegroundColor Green }
function Write-Warn { param([string]$Msg) Write-Host "    !  $Msg" -ForegroundColor Yellow }

# ============================================================
# Helper reflection pentru COM
# ============================================================
if (-not ("ComHelper" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Reflection;
public static class ComHelper {
    public static object Invoke(object comObj, string method, params object[] args) {
        return comObj.GetType().InvokeMember(method,
            BindingFlags.InvokeMethod, null, comObj, args ?? new object[0]);
    }
}
"@
}
function Invoke-ComMethod {
    param([Parameter(Mandatory)]$ComObject,
          [Parameter(Mandatory)][string]$Method,
          [object[]]$Arguments = @())
    return [ComHelper]::Invoke($ComObject, $Method, $Arguments)
}

function Test-PortListening {
    param([string]$TargetHost, [int]$TargetPort, [int]$TimeoutMs = 300)
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ar  = $tcp.BeginConnect($TargetHost, $TargetPort, $null, $null)
        $ok  = $ar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
        if ($ok -and $tcp.Connected) { $tcp.Close(); return $true }
        $tcp.Close()
        return $false
    } catch { return $false }
}

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host " NFFI Demo - Server + COM" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# ============================================================
# 1. Opreste servere WPF vechi
# ============================================================
Write-Step "Curat procese vechi"
$old = Get-Process -Name "NffiTrackingSystem.Server" -ErrorAction SilentlyContinue
if ($old) {
    $old | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Ok "Am oprit $($old.Count) proces(e) vechi"
    Start-Sleep -Milliseconds 500
} else {
    Write-Ok "Niciun server vechi"
}

# ============================================================
# 2. Porneste server WPF (sau headless)
# ============================================================
$serverProc = $null

if ($Headless) {
    Write-Step "Mod headless (fara WPF)"
} else {
    Write-Step "Pornesc server WPF"

    if (-not (Test-Path $ServerExe)) {
        throw "Nu gasesc $ServerExe. Ruleaza Build in Visual Studio mai intai."
    }

    $serverProc = Start-Process -FilePath $ServerExe `
        -ArgumentList "--autostart", "$Port" `
        -PassThru
    Write-Ok "Pornit: $ServerExe (PID $($serverProc.Id), port $Port)"

    # Asteapta ca portul sa fie listening (max 15 secunde)
    $waited = 0
    while ($waited -lt 15000) {
        if (Test-PortListening -TargetHost "127.0.0.1" -TargetPort $Port) { break }
        Start-Sleep -Milliseconds 200
        $waited += 200
    }

    if (Test-PortListening -TargetHost "127.0.0.1" -TargetPort $Port) {
        Write-Ok "Serverul asculta pe portul $Port (dupa $waited ms)"
    } else {
        Write-Warn "Serverul nu a deschis portul in 15s. Continui oricum..."
    }
}

# ============================================================
# 3. Creeaza obiectul COM
# ============================================================
Write-Step "Conectare COM"
$tracker = $null
try {
    $tracker = New-Object -ComObject $progId
    $version = Invoke-ComMethod -ComObject $tracker -Method "GetVersion"
    Write-Ok "COM: $version"
} catch {
    Write-Host "COM NU este accesibil: $($_.Exception.Message)" -ForegroundColor Red
    if ($serverProc -and -not $serverProc.HasExited) {
        $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    exit 1
}

# ============================================================
# 4. Pornire server in COM (daca headless)
#     sau doar lansare masini (daca WPF)
# ============================================================
try {
    if ($Headless) {
        Invoke-ComMethod -ComObject $tracker -Method "StartServer" `
            -Arguments @($Port, ".\com_demo.db") | Out-Null
        Write-Ok "Server headless pe portul $Port"
    }

    $vehicles = @(
        @{ UnitId = "MASINA_01"; LatA = 44.4268; LonA = 26.1025; LatB = 45.6427; LonB = 25.5887 },
        @{ UnitId = "MASINA_02"; LatA = 46.7712; LonA = 23.6236; LatB = 45.6427; LonB = 25.5887 },
        @{ UnitId = "MASINA_03"; LatA = 47.1585; LonA = 27.6014; LatB = 45.6427; LonB = 25.5887 }
    )

    Write-Step "Lansez $($vehicles.Count) masini catre $BindHost`:$Port"
    foreach ($v in $vehicles) {
        Invoke-ComMethod -ComObject $tracker -Method "AddVehicle" `
            -Arguments @($BindHost, $Port, $v.UnitId,
                         $v.LatA, $v.LonA, $v.LatB, $v.LonB,
                         100, $DurationSec) | Out-Null
        Write-Host ("      + {0}  ({1}, {2}) -> ({3}, {4})" -f `
            $v.UnitId, $v.LatA, $v.LonA, $v.LatB, $v.LonB) -ForegroundColor Green
    }

    if (-not $Headless) {
        Write-Host "`n    Uita-te la fereastra WPF: 3 masini pe harta." -ForegroundColor Magenta
    }

    # ============================================================
    # 5. Asteptare
    # ============================================================
    Write-Host "`n    Apasa Ctrl+C pentru oprire sau asteapta finalul." -ForegroundColor Yellow
    $startTime  = Get-Date
    $maxWaitSec = 600

    while ($true) {
        for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Milliseconds 100 }
        $elapsed = [int]((Get-Date) - $startTime).TotalSeconds
        $active  = @(Invoke-ComMethod -ComObject $tracker -Method "GetActiveVehicles")

        if ($active.Count -eq 0) {
            Write-Host "`n    Toate masinile au ajuns in $elapsed s" -ForegroundColor Green
            break
        }
        if ($elapsed -ge $maxWaitSec) {
            Write-Host "`n    Timeout dupa $maxWaitSec s" -ForegroundColor Red
            break
        }
        if ($elapsed % 5 -eq 0) {
            Write-Host ("    t={0,3}s  active: {1}" -f $elapsed, ($active -join ", ")) -ForegroundColor DarkGray
        }
    }
}
finally {
    Write-Host "`n    Cleanup..." -ForegroundColor Yellow

    if ($tracker -ne $null) {
        try { Invoke-ComMethod -ComObject $tracker -Method "StopAll" | Out-Null } catch { }
        try { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($tracker) | Out-Null } catch { }
    }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()

    if ($serverProc -and -not $serverProc.HasExited) {
        if ($KeepServer) {
            Write-Ok "Server WPF ramane pornit (PID $($serverProc.Id)) - inchide-l manual"
        } else {
            Write-Host "    Opresc serverul WPF (PID $($serverProc.Id))..." -ForegroundColor DarkGray
            $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue
            Write-Ok "Server oprit."
        }
    }

    Write-Ok "Done."
}

Write-Host "`n=====================================================" -ForegroundColor Cyan
Write-Host " Gata." -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Cyan