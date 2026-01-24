# Script para instalar drivers USB de Samsung
# Requiere ejecutarse como Administrador

Write-Host "Instalando drivers USB de Samsung..." -ForegroundColor Green

# Verificar si es administrador
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "⚠️  Este script requiere permisos de administrador" -ForegroundColor Yellow
    Write-Host "Ejecuta PowerShell como administrador y vuelve a ejecutar este script" -ForegroundColor Yellow
    exit 1
}

$tempPath = "$env:TEMP\samsung_driver"
New-Item -ItemType Directory -Force -Path $tempPath | Out-Null

# URL del driver de Samsung (versión más reciente)
$driverUrl = "https://developer.samsung.com/downloads/file/version/1.7.50.0/Android%20USB%20Driver%20for%20Windows.exe"
$driverExe = "$tempPath\Samsung_USB_Driver.exe"

Write-Host "Descargando drivers de Samsung..." -ForegroundColor Yellow
try {
    Invoke-WebRequest -Uri $driverUrl -OutFile $driverExe -UseBasicParsing
    Write-Host "Instalando drivers (esto puede tardar unos minutos)..." -ForegroundColor Yellow
    Start-Process -FilePath $driverExe -ArgumentList "/S" -Wait -NoNewWindow
    Write-Host "✅ Drivers de Samsung instalados" -ForegroundColor Green
    Write-Host ""
    Write-Host "Reinicia el PC si es necesario, luego conecta tu Samsung S10e" -ForegroundColor Yellow
}
catch {
    Write-Host "❌ Error al descargar/instalar: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "Descarga manual desde:" -ForegroundColor Yellow
    Write-Host "https://developer.samsung.com/mobile/android-usb-driver.html" -ForegroundColor Cyan
}

Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue
