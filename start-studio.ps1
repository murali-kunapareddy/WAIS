#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$RootDir   = $PSScriptRoot
$BridgeDir = Join-Path $RootDir "wa-bridge"
$AppDir    = Join-Path $RootDir "InviteApp"
$AppUrl    = "http://127.0.0.1:5050"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "   WhatsApp Invite Studio - Windows 11 Launcher       " -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

# 1. Launch wa-bridge (node bridge.js on port 3001) in a separate window
Write-Host "[1/3] Starting WhatsApp Bridge (http://127.0.0.1:3001)..." -ForegroundColor Yellow
Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/k title WA-Bridge (Port 3001) && node bridge.js" `
    -WorkingDirectory $BridgeDir `
    -WindowStyle Normal

# 2. Launch InviteApp (dotnet run on port 5050) in a separate window
Write-Host "[2/3] Starting InviteApp Backend ($AppUrl)..." -ForegroundColor Yellow
Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/k title InviteApp Backend (Port 5050) && dotnet run --urls $AppUrl" `
    -WorkingDirectory $AppDir `
    -WindowStyle Normal

# 3. Wait briefly for the web server to bind and open http://127.0.0.1:5050 in the default browser
Write-Host "[3/3] Waiting for InviteApp to listen on $AppUrl..." -ForegroundColor Yellow
$MaxAttempts = 30
$Attempt = 0
while ($Attempt -lt $MaxAttempts) {
    $Attempt++
    try {
        $TcpClient = New-Object System.Net.Sockets.TcpClient
        $AsyncResult = $TcpClient.BeginConnect("127.0.0.1", 5050, $null, $null)
        $Wait = $AsyncResult.AsyncWaitHandle.WaitOne(500, $false)
        if ($Wait -and $TcpClient.Connected) {
            $TcpClient.Close()
            break
        }
        $TcpClient.Close()
    } catch {
        # Keep waiting until server is ready
    }
    Start-Sleep -Milliseconds 500
}

Write-Host "Opening $AppUrl in default browser..." -ForegroundColor Green
Start-Process $AppUrl
