# Script simplificado de instalación de ADB
# Ejecutar en PowerShell (no requiere administrador para descargar)

Write-Host "Instalando ADB..." -ForegroundColor Green

# Directorio de instalación
$adbPath = "$env:USERPROFILE\adb"
$tempPath = "$env:TEMP\adb_install"

# Crear directorios
New-Item -ItemType Directory -Force -Path $adbPath | Out-Null
New-Item -ItemType Directory -Force -Path $tempPath | Out-Null

# Descargar Platform Tools
Write-Host "Descargando Android SDK Platform Tools..." -ForegroundColor Yellow
$platformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
$platformToolsZip = "$tempPath\platform-tools.zip"

try {
    Invoke-WebRequest -Uri $platformToolsUrl -OutFile $platformToolsZip -UseBasicParsing
    Write-Host "Extrayendo..." -ForegroundColor Yellow
    Expand-Archive -Path $platformToolsZip -DestinationPath $tempPath -Force
    Copy-Item -Path "$tempPath\platform-tools\*" -Destination $adbPath -Recurse -Force
    Write-Host "✅ ADB instalado en: $adbPath" -ForegroundColor Green
    
    # Agregar al PATH del usuario (no requiere admin)
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$adbPath*") {
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$adbPath", "User")
        $env:Path += ";$adbPath"
        Write-Host "✅ ADB agregado al PATH del usuario" -ForegroundColor Green
    }
    
    # Verificar
    & "$adbPath\adb.exe" version
    Write-Host ""
    Write-Host "✅ Instalación completada!" -ForegroundColor Green
    Write-Host "Ubicación: $adbPath" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Nota: Reinicia PowerShell para usar 'adb' desde cualquier lugar" -ForegroundColor Yellow
}
catch {
    Write-Host "❌ Error: $_" -ForegroundColor Red
}

# Limpiar
Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue
