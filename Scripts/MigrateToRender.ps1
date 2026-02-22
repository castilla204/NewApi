# Script de Migración de Supabase a Render PostgreSQL
# Uso: .\Scripts\MigrateToRender.ps1

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Migración de Supabase a Render PostgreSQL" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Credenciales de Supabase (via variables de entorno)
$supabaseHost = $env:SUPABASE_DB_HOST
$supabasePort = if ($env:SUPABASE_DB_PORT) { [int]$env:SUPABASE_DB_PORT } else { 5432 }
$supabaseUser = $env:SUPABASE_DB_USER
$supabasePassword = $env:SUPABASE_DB_PASSWORD
$supabaseDatabase = if ($env:SUPABASE_DB_NAME) { $env:SUPABASE_DB_NAME } else { "postgres" }

# Credenciales de Render (via variables de entorno)
$renderHost = $env:RENDER_DB_HOST
$renderPort = if ($env:RENDER_DB_PORT) { [int]$env:RENDER_DB_PORT } else { 5432 }
$renderUser = $env:RENDER_DB_USER
$renderPassword = $env:RENDER_DB_PASSWORD
$renderDatabase = $env:RENDER_DB_NAME

foreach ($required in @(
    @{ Name = "SUPABASE_DB_HOST"; Value = $supabaseHost },
    @{ Name = "SUPABASE_DB_USER"; Value = $supabaseUser },
    @{ Name = "SUPABASE_DB_PASSWORD"; Value = $supabasePassword },
    @{ Name = "RENDER_DB_HOST"; Value = $renderHost },
    @{ Name = "RENDER_DB_USER"; Value = $renderUser },
    @{ Name = "RENDER_DB_PASSWORD"; Value = $renderPassword },
    @{ Name = "RENDER_DB_NAME"; Value = $renderDatabase }
)) {
    if ([string]::IsNullOrWhiteSpace($required.Value)) {
        Write-Host "Falta la variable de entorno $($required.Name)" -ForegroundColor Red
        exit 1
    }
}

# Archivos
$dumpFile = "supabase_dump.dump"
$sqlFile = "backup_supabase.sql"

Write-Host "Paso 1: Exportar datos de Supabase..." -ForegroundColor Yellow
Write-Host ""

# Verificar que pg_dump esté disponible
try {
    $pgDumpVersion = pg_dump --version
    Write-Host "✅ pg_dump encontrado: $pgDumpVersion" -ForegroundColor Green
} catch {
    Write-Host "❌ ERROR: pg_dump no está instalado o no está en el PATH" -ForegroundColor Red
    Write-Host "   Instala PostgreSQL Client Tools desde: https://www.postgresql.org/download/" -ForegroundColor Yellow
    exit 1
}

# Preguntar formato
Write-Host ""
Write-Host "Selecciona el formato de exportación:" -ForegroundColor Cyan
Write-Host "  1. Custom (-Fc) - Recomendado (comprimido, más rápido)" -ForegroundColor White
Write-Host "  2. SQL (-f) - Alternativa (texto plano, más compatible)" -ForegroundColor White
$formatChoice = Read-Host "Opción (1 o 2)"

if ($formatChoice -eq "1") {
    # Formato Custom
    Write-Host ""
    Write-Host "Exportando en formato Custom..." -ForegroundColor Yellow
    $env:PGPASSWORD = $supabasePassword
    
    pg_dump -Fc -v --schema=public `
        -h $supabaseHost `
        -p $supabasePort `
        -U $supabaseUser `
        -d $supabaseDatabase `
        -f $dumpFile
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Exportación completada: $dumpFile" -ForegroundColor Green
        $useCustomFormat = $true
    } else {
        Write-Host "❌ Error en la exportación" -ForegroundColor Red
        exit 1
    }
} else {
    # Formato SQL
    Write-Host ""
    Write-Host "Exportando en formato SQL..." -ForegroundColor Yellow
    $env:PGPASSWORD = $supabasePassword
    
    pg_dump -v --schema=public `
        -h $supabaseHost `
        -p $supabasePort `
        -U $supabaseUser `
        -d $supabaseDatabase `
        --clean --if-exists `
        -f $sqlFile
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Exportación completada: $sqlFile" -ForegroundColor Green
        $useCustomFormat = $false
    } else {
        Write-Host "❌ Error en la exportación" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "Paso 2: Importar datos a Render PostgreSQL..." -ForegroundColor Yellow
Write-Host ""

# Confirmar antes de importar
Write-Host "⚠️  ADVERTENCIA: Esto sobrescribirá los datos existentes en Render PostgreSQL" -ForegroundColor Red
$confirm = Read-Host "¿Continuar? (s/N)"

if ($confirm -ne "s" -and $confirm -ne "S") {
    Write-Host "Migración cancelada" -ForegroundColor Yellow
    exit 0
}

if ($useCustomFormat) {
    # Restaurar formato Custom
    Write-Host "Restaurando desde formato Custom..." -ForegroundColor Yellow
    $env:PGPASSWORD = $renderPassword
    
    pg_restore -v -d $renderDatabase --no-owner --no-acl --clean `
        -h $renderHost `
        -p $renderPort `
        -U $renderUser `
        $dumpFile
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Importación completada" -ForegroundColor Green
    } else {
        Write-Host "❌ Error en la importación" -ForegroundColor Red
        exit 1
    }
} else {
    # Restaurar formato SQL
    Write-Host "Restaurando desde formato SQL..." -ForegroundColor Yellow
    $env:PGPASSWORD = $renderPassword
    
    psql `
        -h $renderHost `
        -p $renderPort `
        -U $renderUser `
        -d $renderDatabase `
        -f $sqlFile
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Importación completada" -ForegroundColor Green
    } else {
        Write-Host "❌ Error en la importación" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "Paso 3: Verificar conexión..." -ForegroundColor Yellow
Write-Host ""

$env:PGPASSWORD = $renderPassword
$version = psql -h $renderHost -p $renderPort -U $renderUser -d $renderDatabase -t -c "SELECT version();"

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Conexión exitosa a Render PostgreSQL" -ForegroundColor Green
    Write-Host "   Versión: $($version.Trim())" -ForegroundColor Gray
} else {
    Write-Host "⚠️  No se pudo verificar la conexión" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "✅ Migración completada" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Próximos pasos:" -ForegroundColor Yellow
Write-Host "  1. Ejecuta: dotnet ef database update" -ForegroundColor White
Write-Host "  2. Verifica que la aplicación funcione correctamente" -ForegroundColor White
Write-Host "  3. Configura las credenciales de Supabase para Realtime" -ForegroundColor White
Write-Host ""
