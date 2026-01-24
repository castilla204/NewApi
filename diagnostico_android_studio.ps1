# Script de diagnóstico para problemas con Android Studio
# Pantalla en blanco al ejecutar la app

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Diagnóstico Android Studio - Pantalla en Blanco" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$adbPath = "$env:USERPROFILE\adb\adb.exe"

if (-not (Test-Path $adbPath)) {
    Write-Host "❌ ADB no encontrado en: $adbPath" -ForegroundColor Red
    exit 1
}

# 1. Verificar dispositivos conectados
Write-Host "1. Verificando dispositivos conectados..." -ForegroundColor Yellow
$devices = & $adbPath devices
Write-Host $devices
Write-Host ""

# 2. Verificar si hay procesos de la app corriendo
Write-Host "2. Verificando procesos de la aplicación..." -ForegroundColor Yellow
$packages = & $adbPath shell pm list packages | Select-String -Pattern "capacitor|android"
if ($packages) {
    Write-Host "Paquetes encontrados:" -ForegroundColor Green
    $packages | ForEach-Object { Write-Host "  $_" -ForegroundColor White }
} else {
    Write-Host "⚠️  No se encontraron paquetes relacionados" -ForegroundColor Yellow
}
Write-Host ""

# 3. Ver errores recientes en Logcat
Write-Host "3. Buscando errores en Logcat (últimos 30 segundos)..." -ForegroundColor Yellow
Write-Host "   (Presiona Ctrl+C para detener)" -ForegroundColor Gray
Write-Host ""

# Limpiar logcat primero
& $adbPath logcat -c | Out-Null

# Capturar errores
Write-Host "Errores encontrados:" -ForegroundColor Cyan
& $adbPath logcat *:E AndroidRuntime:E -d | Select-Object -First 30

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Soluciones comunes:" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Verifica Logcat en Android Studio:" -ForegroundColor Yellow
Write-Host "   - Abre la pestaña 'Logcat' en la parte inferior" -ForegroundColor White
Write-Host "   - Filtra por 'Error' o 'AndroidRuntime'" -ForegroundColor White
Write-Host ""
Write-Host "2. Verifica la consola de ejecución (Run):" -ForegroundColor Yellow
Write-Host "   - Revisa si hay errores de compilación o instalación" -ForegroundColor White
Write-Host ""
Write-Host "3. Revisa el archivo MainActivity:" -ForegroundColor Yellow
Write-Host "   - Asegúrate de que esté cargando la URL correcta" -ForegroundColor White
Write-Host ""
Write-Host "4. Limpia y reconstruye el proyecto:" -ForegroundColor Yellow
Write-Host "   - Build > Clean Project" -ForegroundColor White
Write-Host "   - Build > Rebuild Project" -ForegroundColor White
Write-Host ""
Write-Host "5. Verifica la configuración de Capacitor:" -ForegroundColor Yellow
Write-Host "   - Revisa capacitor.config.ts/js" -ForegroundColor White
Write-Host "   - Verifica que server.url esté configurado correctamente" -ForegroundColor White
Write-Host ""
