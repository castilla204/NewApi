# Script para aplicar la migración del campo Color a SystemStatuses
# Ejecutar este script en PowerShell como administrador

$connectionString = "Host=localhost;Database=newapi;Username=postgres;Password=tu_password_aqui"

try {
    # Crear conexión a PostgreSQL
    Add-Type -Path "C:\Program Files\PostgreSQL\*\bin\Npgsql.dll" -ErrorAction SilentlyContinue
    
    if (-not (Get-Module -Name Npgsql -ListAvailable)) {
        Write-Host "Instalando Npgsql..." -ForegroundColor Yellow
        Install-Package Npgsql -Force -Scope CurrentUser
    }
    
    Import-Module Npgsql
    
    $connection = New-Object Npgsql.NpgsqlConnection($connectionString)
    $connection.Open()
    
    Write-Host "✅ Conectado a la base de datos" -ForegroundColor Green
    
    # Agregar columna Color
    $addColumnQuery = @"
ALTER TABLE "SystemStatuses" ADD COLUMN IF NOT EXISTS "Color" character varying(20);
"@
    
    $command = New-Object Npgsql.NpgsqlCommand($addColumnQuery, $connection)
    $command.ExecuteNonQuery()
    Write-Host "✅ Columna Color agregada" -ForegroundColor Green
    
    # Actualizar colores para estados existentes
    $updateColorsQuery = @"
UPDATE "SystemStatuses" 
SET "Color" = CASE 
    WHEN "StatusValue" = 'pending' THEN '#FFA500'  -- Naranja para pendiente
    WHEN "StatusValue" = 'completed' THEN '#28A745'  -- Verde para completado
    WHEN "StatusValue" = 'cancelled' THEN '#DC3545'  -- Rojo para cancelado
    WHEN "StatusValue" = 'dispute_resolved_client' THEN '#17A2B8'  -- Azul para disputa resuelta
    WHEN "StatusValue" = 'appointment_proposed' THEN '#6F42C1'  -- Púrpura para propuesta
    WHEN "StatusValue" = 'appointment_confirmed' THEN '#20C997'  -- Verde azulado para confirmado
    WHEN "StatusValue" = 'appointment_rejected' THEN '#FD7E14'  -- Naranja oscuro para rechazado
    WHEN "StatusValue" = 'appointment_completed' THEN '#28A745'  -- Verde para completado
    WHEN "StatusValue" = 'appointment_cancelled' THEN '#DC3545'  -- Rojo para cancelado
    WHEN "StatusValue" = 'appointment_report_sent' THEN '#6610F2'  -- Púrpura para reporte enviado
    WHEN "StatusValue" = 'awaiting_appointment' THEN '#FFC107'  -- Amarillo para esperando cita
    WHEN "StatusValue" = 'expert_report_timeout' THEN '#E83E8C'  -- Rosa para timeout
    ELSE '#6C757D'  -- Gris por defecto
END
WHERE "Color" IS NULL;
"@
    
    $command = New-Object Npgsql.NpgsqlCommand($updateColorsQuery, $connection)
    $rowsAffected = $command.ExecuteNonQuery()
    Write-Host "✅ Colores actualizados para $rowsAffected registros" -ForegroundColor Green
    
    # Verificar resultados
    $verifyQuery = 'SELECT "StatusValue", "DisplayName", "Color" FROM "SystemStatuses" ORDER BY "StatusType", "SortOrder" LIMIT 10;'
    $command = New-Object Npgsql.NpgsqlCommand($verifyQuery, $connection)
    $reader = $command.ExecuteReader()
    
    Write-Host "`n📋 Estados con colores:" -ForegroundColor Cyan
    while ($reader.Read()) {
        $statusValue = $reader["StatusValue"]
        $displayName = $reader["DisplayName"]
        $color = $reader["Color"]
        Write-Host "  $statusValue - $displayName - $color" -ForegroundColor White
    }
    $reader.Close()
    
    $connection.Close()
    Write-Host "`n✅ Migración completada exitosamente" -ForegroundColor Green
    
} catch {
    Write-Host "❌ Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Asegúrate de que PostgreSQL esté ejecutándose y las credenciales sean correctas" -ForegroundColor Yellow
}



