# Script para aplicar la migracion de paises directamente en PostgreSQL
# Ejecutar: .\aplicar-migracion-paises.ps1

Write-Host "Aplicando migracion: AddCountryToExpertProfileAndSearchHire" -ForegroundColor Cyan
Write-Host ""

# Intentar obtener la cadena de conexion desde appsettings.json
$appsettingsPath = "appsettings.Development.json"
$connectionString = $null

if (Test-Path $appsettingsPath) {
    $appsettings = Get-Content $appsettingsPath | ConvertFrom-Json
    $connectionString = $appsettings.ConnectionStrings.PostgresConnection
}

# Si no hay connection string, usar valores por defecto
if ([string]::IsNullOrEmpty($connectionString)) {
    Write-Host "No se encontro connection string en appsettings.Development.json" -ForegroundColor Yellow
    Write-Host "Usando valores por defecto para desarrollo..." -ForegroundColor Yellow
    
    $dbHost = "localhost"
    $dbPort = "5433"
    $dbUsername = "admin"
    $dbName = "atrapo"
    
    # Intentar obtener la contraseña desde variables de entorno
    $dbPassword = $env:POSTGRES_PASSWORD
    
    if ([string]::IsNullOrEmpty($dbPassword)) {
        Write-Host ""
        Write-Host "ERROR: No se encontro POSTGRES_PASSWORD en variables de entorno" -ForegroundColor Red
        Write-Host ""
        Write-Host "Opciones:" -ForegroundColor Yellow
        Write-Host "1. Configurar POSTGRES_PASSWORD como variable de entorno" -ForegroundColor Yellow
        Write-Host "2. Ejecutar el SQL manualmente en PostgreSQL usando el archivo: APLICAR_MIGRACION_PAISES_SQL.sql" -ForegroundColor Yellow
        exit 1
    }
    
    $connectionString = "Host=$dbHost;Port=$dbPort;Username=$dbUsername;Password=$dbPassword;Database=$dbName"
}

# Extraer componentes de la connection string
$hostMatch = [regex]::Match($connectionString, "Host=([^;]+)")
$portMatch = [regex]::Match($connectionString, "Port=([^;]+)")
$userMatch = [regex]::Match($connectionString, "Username=([^;]+)")
$passMatch = [regex]::Match($connectionString, "Password=([^;]+)")
$dbMatch = [regex]::Match($connectionString, "Database=([^;]+)")

$dbHost = if ($hostMatch.Success) { $hostMatch.Groups[1].Value } else { "localhost" }
$dbPort = if ($portMatch.Success) { $portMatch.Groups[1].Value } else { "5432" }
$dbUser = if ($userMatch.Success) { $userMatch.Groups[1].Value } else { "admin" }
$dbPass = if ($passMatch.Success) { $passMatch.Groups[1].Value } else { "" }
$dbName = if ($dbMatch.Success) { $dbMatch.Groups[1].Value } else { "atrapo" }

Write-Host "Configuracion de conexion:" -ForegroundColor Cyan
Write-Host "   Host: $dbHost"
Write-Host "   Port: $dbPort"
Write-Host "   Database: $dbName"
Write-Host "   Username: $dbUser"
Write-Host ""

# Verificar si psql esta disponible
$psqlPath = Get-Command psql -ErrorAction SilentlyContinue

if ($psqlPath) {
    Write-Host "psql encontrado, ejecutando SQL..." -ForegroundColor Green
    Write-Host ""
    
    # Configurar variable de entorno PGPASSWORD
    $env:PGPASSWORD = $dbPass
    
    # SQL a ejecutar
    $sql1 = 'ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "Country" text NULL;'
    $sql2 = 'ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "ExpertCountry" text NULL;'
    
    # Ejecutar SQL
    try {
        Write-Host "Ejecutando migracion..." -ForegroundColor Cyan
        $result1 = & psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -c $sql1 2>&1
        $result2 = & psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -c $sql2 2>&1
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "Migracion aplicada exitosamente!" -ForegroundColor Green
            Write-Host ""
            
            # Verificar columnas
            Write-Host "Verificando columnas..." -ForegroundColor Cyan
            $verifySql = 'SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE (table_name = ''ExpertProfiles'' AND column_name = ''Country'') OR (table_name = ''SearchHires'' AND column_name = ''ExpertCountry'') ORDER BY table_name, column_name;'
            $verifyResult = & psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -c $verifySql 2>&1
            Write-Host $verifyResult
            Write-Host ""
            
            # Registrar en __EFMigrationsHistory
            Write-Host "Registrando migracion en __EFMigrationsHistory..." -ForegroundColor Cyan
            $registerSql = 'INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES (''20250114120000_AddCountryToExpertProfileAndSearchHire'', ''10.0.0'') ON CONFLICT DO NOTHING;'
            $registerResult = & psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -c $registerSql 2>&1
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Migracion registrada en __EFMigrationsHistory" -ForegroundColor Green
            } else {
                Write-Host "No se pudo registrar la migracion (puede que ya este registrada)" -ForegroundColor Yellow
            }
        } else {
            Write-Host ""
            Write-Host "ERROR al aplicar la migracion:" -ForegroundColor Red
            Write-Host $result1
            Write-Host $result2
            exit 1
        }
    }
    catch {
        Write-Host ""
        Write-Host "ERROR al ejecutar psql: $_" -ForegroundColor Red
        Write-Host ""
        Write-Host "Ejecuta el SQL manualmente usando el archivo: APLICAR_MIGRACION_PAISES_SQL.sql" -ForegroundColor Yellow
        exit 1
    }
    finally {
        # Limpiar variable de entorno
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "psql no esta disponible en el PATH" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Opciones:" -ForegroundColor Yellow
    Write-Host "1. Instalar PostgreSQL Client Tools" -ForegroundColor Yellow
    Write-Host "2. Ejecutar el SQL manualmente usando pgAdmin, DBeaver, o cualquier cliente PostgreSQL" -ForegroundColor Yellow
    Write-Host "3. Usar el archivo: APLICAR_MIGRACION_PAISES_SQL.sql" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "SQL a ejecutar:" -ForegroundColor Cyan
    Write-Host ""
    Write-Host 'ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "Country" text NULL;'
    Write-Host 'ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "ExpertCountry" text NULL;'
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "Migracion completada!" -ForegroundColor Green
