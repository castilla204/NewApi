# Script para solucionar pantalla en blanco en Android Studio

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Solución: Pantalla en Blanco" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$adbPath = "$env:USERPROFILE\adb\adb.exe"

# 1. Cerrar emuladores offline
Write-Host "1. Verificando emuladores..." -ForegroundColor Yellow
$devices = & $adbPath devices
$offlineDevices = $devices | Select-String "offline"

if ($offlineDevices) {
    Write-Host "⚠️  Hay emuladores offline. Cierra los emuladores no usados en Android Studio" -ForegroundColor Yellow
}

# 2. Obtener el emulador activo
$activeDevice = ($devices | Select-String "device" | Where-Object { $_ -notmatch "List of devices" } | Select-Object -First 1).ToString().Split("`t")[0]

if ($activeDevice) {
    Write-Host "✅ Usando dispositivo: $activeDevice" -ForegroundColor Green
    Write-Host ""
    
    # 3. Ver logs recientes de errores
    Write-Host "2. Buscando errores recientes..." -ForegroundColor Yellow
    Write-Host ""
    
    & $adbPath -s $activeDevice logcat -d *:E AndroidRuntime:E | Select-Object -First 20
    
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Soluciones a probar:" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "🔧 SOLUCIÓN 1: Limpiar y Reconstruir" -ForegroundColor Yellow
    Write-Host "   En Android Studio:" -ForegroundColor White
    Write-Host "   - Build > Clean Project" -ForegroundColor Gray
    Write-Host "   - Build > Rebuild Project" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "🔧 SOLUCIÓN 2: Verificar Capacitor Config" -ForegroundColor Yellow
    Write-Host "   Si usas Capacitor, verifica capacitor.config.ts/js:" -ForegroundColor White
    Write-Host "   - server.url debe apuntar a tu servidor de desarrollo" -ForegroundColor Gray
    Write-Host "   - Para emulador usa: http://10.0.2.2:4200" -ForegroundColor Gray
    Write-Host "   - Ejecuta: npx cap sync android" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "🔧 SOLUCIÓN 3: Verificar Servidor de Desarrollo" -ForegroundColor Yellow
    Write-Host "   Asegúrate de que tu servidor esté corriendo:" -ForegroundColor White
    Write-Host "   - Angular: ng serve" -ForegroundColor Gray
    Write-Host "   - React: npm start" -ForegroundColor Gray
    Write-Host "   - Vue: npm run serve" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "🔧 SOLUCIÓN 4: Verificar Logcat en Android Studio" -ForegroundColor Yellow
    Write-Host "   - Abre la pestaña 'Logcat' en la parte inferior" -ForegroundColor White
    Write-Host "   - Filtra por 'Error' o 'chromium'" -ForegroundColor White
    Write-Host "   - Busca errores relacionados con WebView o carga de URL" -ForegroundColor White
    Write-Host ""
    
    Write-Host "🔧 SOLUCIÓN 5: Reinstalar la App" -ForegroundColor Yellow
    Write-Host "   En Android Studio:" -ForegroundColor White
    Write-Host "   - Run > Edit Configurations" -ForegroundColor Gray
    Write-Host "   - Marca 'Uninstall apk before installing'" -ForegroundColor Gray
    Write-Host "   - O ejecuta: adb uninstall com.tu.paquete" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "🔧 SOLUCIÓN 6: Verificar AndroidManifest.xml" -ForegroundColor Yellow
    Write-Host "   Asegúrate de tener:" -ForegroundColor White
    Write-Host "   - <uses-permission android:name='android.permission.INTERNET' />" -ForegroundColor Gray
    Write-Host "   - android:usesCleartextTraffic='true' en <application>" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Para ver logs en tiempo real:" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Ejecuta: adb -s $activeDevice logcat | findstr /i 'error exception'" -ForegroundColor White
    Write-Host ""
}
else {
    Write-Host "❌ No hay dispositivos activos" -ForegroundColor Red
    Write-Host "Inicia un emulador o conecta tu dispositivo" -ForegroundColor Yellow
}
