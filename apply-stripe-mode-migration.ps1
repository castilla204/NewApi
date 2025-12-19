# Script para aplicar la migración de StripeMode a SystemSettings
# Este script ejecuta el SQL directamente en la base de datos

Write-Host "🔧 Aplicando migración: AddStripeModeToSystemSettings" -ForegroundColor Cyan

# Leer la cadena de conexión desde variables de entorno o usar valores por defecto
$dbHost = $env:POSTGRES_HOST
if ([string]::IsNullOrEmpty($dbHost)) {
    $dbHost = "localhost"
}

$dbPort = $env:POSTGRES_PORT
if ([string]::IsNullOrEmpty($dbPort)) {
    # Probar puertos comunes del túnel SSH
    $dbPort = "5435"
}

$dbUsername = $env:POSTGRES_USERNAME
if ([string]::IsNullOrEmpty($dbUsername)) {
    $dbUsername = "admin"
}

$dbPassword = $env:POSTGRES_PASSWORD
if ([string]::IsNullOrEmpty($dbPassword)) {
    Write-Host "⚠️  POSTGRES_PASSWORD no encontrada en variables de entorno" -ForegroundColor Yellow
    Write-Host "   Por favor, proporciona la contraseña manualmente o configura la variable de entorno" -ForegroundColor Yellow
    $dbPassword = Read-Host "Ingresa la contraseña de PostgreSQL" -AsSecureString
    $dbPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($dbPassword))
}

$dbName = $env:POSTGRES_DATABASE
if ([string]::IsNullOrEmpty($dbName)) {
    $dbName = "atrapo"
}

# Construir la cadena de conexión para psql
$env:PGPASSWORD = $dbPassword
$connectionString = "host=$dbHost port=$dbPort user=$dbUsername dbname=$dbName"

Write-Host "📊 Conectando a: ${dbHost}:${dbPort}/${dbName} como ${dbUsername}" -ForegroundColor Cyan

# SQL a ejecutar
$sql = @"
-- Add StripeMode columns to SystemSettings table
-- Migration: 20250120000000_AddStripeModeToSystemSettings

-- Verificar si las columnas ya existen antes de agregarlas
DO `$`$ 
BEGIN
    -- Add StripeMode column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeMode') THEN
        ALTER TABLE "SystemSettings" 
        ADD COLUMN "StripeMode" character varying(20) NOT NULL DEFAULT 'production';
        RAISE NOTICE 'Columna StripeMode agregada';
    ELSE
        RAISE NOTICE 'Columna StripeMode ya existe';
    END IF;

    -- Add StripeModeChangedAt column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeModeChangedAt') THEN
        ALTER TABLE "SystemSettings" 
        ADD COLUMN "StripeModeChangedAt" timestamp with time zone NULL;
        RAISE NOTICE 'Columna StripeModeChangedAt agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedAt ya existe';
    END IF;

    -- Add StripeModeChangedByUserId column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeModeChangedByUserId') THEN
        ALTER TABLE "SystemSettings" 
        ADD COLUMN "StripeModeChangedByUserId" integer NULL;
        RAISE NOTICE 'Columna StripeModeChangedByUserId agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedByUserId ya existe';
    END IF;
END `$`$;
"@

# Intentar ejecutar con psql
try {
    Write-Host "🚀 Ejecutando migración..." -ForegroundColor Green
    
    # Guardar SQL en archivo temporal
    $tempFile = [System.IO.Path]::GetTempFileName()
    $sql | Out-File -FilePath $tempFile -Encoding UTF8
    
    # Ejecutar psql
    $result = & psql $connectionString -f $tempFile 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Migración aplicada exitosamente!" -ForegroundColor Green
        Write-Host $result
    } else {
        Write-Host "❌ Error al ejecutar la migración:" -ForegroundColor Red
        Write-Host $result
        exit 1
    }
    
    # Limpiar archivo temporal
    Remove-Item $tempFile -ErrorAction SilentlyContinue
    
} catch {
    Write-Host "❌ Error: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "💡 Alternativa: Ejecuta el SQL manualmente en tu cliente de PostgreSQL:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host $sql
    exit 1
} finally {
    # Limpiar variable de entorno
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "✨ ¡Migración completada!" -ForegroundColor Green

