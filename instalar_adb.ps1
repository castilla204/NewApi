# Script de instalación automática de ADB para Samsung S10e
# Ejecutar como Administrador: PowerShell > clic derecho > "Ejecutar como administrador"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Instalación de ADB para Samsung S10e" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Verificar si se ejecuta como administrador
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "⚠️  Este script requiere permisos de administrador" -ForegroundColor Yellow
    Write-Host "Por favor, ejecuta PowerShell como administrador y vuelve a ejecutar este script" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Presiona cualquier tecla para salir..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

# Directorio de instalación
$adbPath = "C:\adb"
$tempPath = "$env:TEMP\adb_install"

# Crear directorios
Write-Host "📁 Creando directorios..." -ForegroundColor Green
New-Item -ItemType Directory -Force -Path $adbPath | Out-Null
New-Item -ItemType Directory -Force -Path $tempPath | Out-Null

# Función para descargar archivos
function Download-File {
    param(
        [string]$Url,
        [string]$OutputPath
    )
    try {
        Write-Host "⬇️  Descargando: $Url" -ForegroundColor Yellow
        Invoke-WebRequest -Uri $Url -OutFile $OutputPath -UseBasicParsing -ErrorAction Stop
        Write-Host "✅ Descarga completada" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "❌ Error al descargar: $_" -ForegroundColor Red
        return $false
    }
}

# Paso 1: Descargar Android SDK Platform Tools
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Paso 1: Descargando Android SDK Platform Tools" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$platformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
$platformToolsZip = "$tempPath\platform-tools.zip"

if (Download-File -Url $platformToolsUrl -OutputPath $platformToolsZip) {
    Write-Host "📦 Extrayendo Platform Tools..." -ForegroundColor Yellow
    
    # Extraer ZIP
    Expand-Archive -Path $platformToolsZip -DestinationPath $tempPath -Force
    
    # Copiar archivos a C:\adb
    $extractedPath = "$tempPath\platform-tools"
    if (Test-Path $extractedPath) {
        Copy-Item -Path "$extractedPath\*" -Destination $adbPath -Recurse -Force
        Write-Host "✅ Platform Tools instalado en: $adbPath" -ForegroundColor Green
    }
    else {
        Write-Host "❌ Error: No se encontró la carpeta platform-tools después de extraer" -ForegroundColor Red
    }
}

# Paso 2: Descargar e instalar Samsung USB Drivers
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Paso 2: Instalando Samsung USB Drivers" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Intentar descargar desde el sitio oficial de Samsung
$samsungDriverUrl = "https://developer.samsung.com/downloads/file/version/1.7.50.0/Android%20USB%20Driver%20for%20Windows.exe"
$samsungDriverExe = "$tempPath\Samsung_USB_Driver.exe"

Write-Host "ℹ️  Nota: Los drivers de Samsung pueden requerir descarga manual" -ForegroundColor Yellow
Write-Host "   URL: https://developer.samsung.com/mobile/android-usb-driver.html" -ForegroundColor Yellow

if (Download-File -Url $samsungDriverUrl -OutputPath $samsungDriverExe) {
    Write-Host "🔧 Instalando Samsung USB Drivers..." -ForegroundColor Yellow
    Write-Host "   (Esto puede tardar unos minutos)" -ForegroundColor Yellow
    
    Start-Process -FilePath $samsungDriverExe -ArgumentList "/S" -Wait -NoNewWindow
    Write-Host "✅ Drivers de Samsung instalados" -ForegroundColor Green
}
else {
    Write-Host "⚠️  No se pudo descargar automáticamente. Por favor descarga manualmente:" -ForegroundColor Yellow
    Write-Host "   https://developer.samsung.com/mobile/android-usb-driver.html" -ForegroundColor Yellow
}

# Paso 3: Agregar ADB al PATH del sistema
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Paso 3: Agregando ADB al PATH" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$currentPath = [Environment]::GetEnvironmentVariable("Path", "Machine")
if ($currentPath -notlike "*$adbPath*") {
    Write-Host "➕ Agregando $adbPath al PATH del sistema..." -ForegroundColor Yellow
    $newPath = $currentPath + ";$adbPath"
    [Environment]::SetEnvironmentVariable("Path", $newPath, "Machine")
    Write-Host "✅ ADB agregado al PATH" -ForegroundColor Green
    
    # Actualizar PATH en la sesión actual
    $env:Path += ";$adbPath"
}
else {
    Write-Host "✅ ADB ya está en el PATH" -ForegroundColor Green
}

# Paso 4: Verificar instalación
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Paso 4: Verificando instalación" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Verificar que adb.exe existe
if (Test-Path "$adbPath\adb.exe") {
    Write-Host "✅ adb.exe encontrado" -ForegroundColor Green
    
    # Intentar ejecutar adb version
    try {
        $adbVersion = & "$adbPath\adb.exe" version
        Write-Host ""
        Write-Host "Versión de ADB instalada:" -ForegroundColor Cyan
        Write-Host $adbVersion -ForegroundColor White
    }
    catch {
        Write-Host "⚠️  No se pudo ejecutar adb version" -ForegroundColor Yellow
    }
}
else {
    Write-Host "❌ Error: adb.exe no encontrado en $adbPath" -ForegroundColor Red
}

# Limpiar archivos temporales
Write-Host ""
Write-Host "🧹 Limpiando archivos temporales..." -ForegroundColor Yellow
Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue

# Instrucciones finales
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "✅ Instalación completada" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "📱 Próximos pasos en tu Samsung S10e:" -ForegroundColor Yellow
Write-Host "   1. Ve a: Ajustes > Acerca del teléfono" -ForegroundColor White
Write-Host "   2. Toca 'Número de compilación' 7 veces" -ForegroundColor White
Write-Host "   3. Ve a: Ajustes > Opciones de desarrollador" -ForegroundColor White
Write-Host "   4. Activa 'Depuración USB'" -ForegroundColor White
Write-Host "   5. Conecta el teléfono por USB" -ForegroundColor White
Write-Host ""
Write-Host "🔍 Para verificar la conexión, ejecuta:" -ForegroundColor Yellow
Write-Host "   adb devices" -ForegroundColor White
Write-Host ""
Write-Host "⚠️  IMPORTANTE: Reinicia PowerShell/CMD para que el PATH se actualice" -ForegroundColor Yellow
Write-Host ""
Write-Host "Presiona cualquier tecla para salir..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
