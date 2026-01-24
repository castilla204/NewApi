# Script para verificar la instalación de ADB y conexión del dispositivo

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Verificación de ADB" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Verificar si ADB está instalado
$adbPath = "$env:USERPROFILE\adb\adb.exe"
if (Test-Path $adbPath) {
    Write-Host "✅ ADB encontrado en: $adbPath" -ForegroundColor Green
    Write-Host ""
    
    # Mostrar versión
    Write-Host "Versión de ADB:" -ForegroundColor Yellow
    & $adbPath version
    Write-Host ""
    
    # Verificar dispositivos conectados
    Write-Host "Dispositivos conectados:" -ForegroundColor Yellow
    & $adbPath devices
    Write-Host ""
    
    # Instrucciones
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Instrucciones para conectar tu Samsung S10e:" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "1. En tu Samsung S10e:" -ForegroundColor White
    Write-Host "   - Ajustes > Acerca del teléfono" -ForegroundColor Gray
    Write-Host "   - Toca 'Número de compilación' 7 veces" -ForegroundColor Gray
    Write-Host "   - Ajustes > Opciones de desarrollador" -ForegroundColor Gray
    Write-Host "   - Activa 'Depuración USB'" -ForegroundColor Gray
    Write-Host ""
    Write-Host "2. Conecta el teléfono por USB" -ForegroundColor White
    Write-Host ""
    Write-Host "3. Ejecuta de nuevo este script para verificar" -ForegroundColor White
    Write-Host ""
}
else {
    Write-Host "❌ ADB no encontrado" -ForegroundColor Red
    Write-Host "Ejecuta primero: .\instalar_adb_simple.ps1" -ForegroundColor Yellow
}
