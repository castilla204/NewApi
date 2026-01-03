# Script de Prueba: Tipo de Servicio "Revisión presencial"
# Este script verifica que el tipo de servicio existe en la API desplegada

# Configurar URL base de la API
$baseUrl = "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PRUEBA: Revision Presencial" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 1. Verificar que la API está disponible
Write-Host "1. Verificando disponibilidad de la API..." -ForegroundColor Yellow
try {
    $healthCheck = Invoke-RestMethod -Uri "$baseUrl/health" -Method GET -ErrorAction Stop
    Write-Host "   [OK] API disponible" -ForegroundColor Green
} catch {
    Write-Host "   [ADVERTENCIA] No se pudo verificar el health check, pero continuamos..." -ForegroundColor Yellow
}

# 2. Obtener todos los tipos de servicio
Write-Host "`n2. Obteniendo tipos de servicio..." -ForegroundColor Yellow
try {
    $response = Invoke-RestMethod -Uri "$baseUrl/api/ServiceType" -Method GET -ContentType "application/json" -ErrorAction Stop
    
    if ($response.success) {
        Write-Host "   [OK] Conexion exitosa con la API" -ForegroundColor Green
        Write-Host "   Total de tipos de servicio: $($response.count)" -ForegroundColor White
        
        # 3. Buscar "Revisión presencial"
        Write-Host "`n3. Buscando 'Revision presencial'..." -ForegroundColor Yellow
        $revisionPresencial = $response.data | Where-Object { $_.name -eq "Revisión presencial" }
        
        if ($revisionPresencial) {
            Write-Host "`n   [OK] 'Revision presencial' ENCONTRADO:" -ForegroundColor Green
            Write-Host "   ┌─────────────────────────────────────────┐" -ForegroundColor Gray
            Write-Host "   │ ID: $($revisionPresencial.id.ToString().PadRight(37))│" -ForegroundColor White
            Write-Host "   │ Nombre: $($revisionPresencial.name.PadRight(32))│" -ForegroundColor White
            Write-Host "   │ Descripción: $($revisionPresencial.description.Substring(0, [Math]::Min(25, $revisionPresencial.description.Length)).PadRight(25))│" -ForegroundColor White
            Write-Host "   │ Categoría: $($revisionPresencial.serviceTypeCategoryName.PadRight(30))│" -ForegroundColor White
            Write-Host "   │ Categoría ID: $($revisionPresencial.serviceTypeCategoryId.ToString().PadRight(28))│" -ForegroundColor White
            Write-Host "   │ Requiere Cita: $($revisionPresencial.requiresAppointment.ToString().PadRight(27))│" -ForegroundColor White
            Write-Host "   │ Activo: $($revisionPresencial.isActive.ToString().PadRight(33))│" -ForegroundColor White
            Write-Host "   │ Posición: $($revisionPresencial.position.ToString().PadRight(30))│" -ForegroundColor White
            Write-Host "   └─────────────────────────────────────────┘" -ForegroundColor Gray
            
            Write-Host "`n   Detalles completos:" -ForegroundColor Cyan
            $revisionPresencial | ConvertTo-Json -Depth 10 | Write-Host -ForegroundColor Gray
            
            Write-Host "`n[OK] PRUEBA EXITOSA: El tipo de servicio 'Revision presencial' esta disponible en la API" -ForegroundColor Green
        } else {
            Write-Host "`n   [ERROR] 'Revision presencial' NO encontrado en la lista" -ForegroundColor Red
            Write-Host "`n   Tipos de servicio disponibles:" -ForegroundColor Yellow
            $response.data | ForEach-Object { 
                Write-Host "      - $($_.name) (ID: $($_.id), Activo: $($_.isActive))" -ForegroundColor Gray 
            }
            Write-Host "`n[ERROR] PRUEBA FALLIDA: El tipo de servicio no existe o no esta activo" -ForegroundColor Red
            Write-Host "   Solucion: Ejecutar el script SQL 'add_revision_presencial_service_type.sql' en la base de datos" -ForegroundColor Yellow
        }
        
        # 4. Mostrar todos los tipos de servicio para referencia
        Write-Host "`n4. Resumen de todos los tipos de servicio:" -ForegroundColor Yellow
        $response.data | ForEach-Object {
            $status = if ($_.isActive) { "[ACTIVO]" } else { "[INACTIVO]" }
            Write-Host "   $status $($_.name) (ID: $($_.id)) - Categoria: $($_.serviceTypeCategoryName)" -ForegroundColor White
        }
        
    } else {
        Write-Host "   [ERROR] Error en la respuesta: $($response.message)" -ForegroundColor Red
    }
} catch {
    Write-Host "`n   [ERROR] Error al conectar con la API" -ForegroundColor Red
    Write-Host "   Mensaje: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "`n   Verifica que:" -ForegroundColor Yellow
    Write-Host "      - La API este desplegada y funcionando" -ForegroundColor Gray
    Write-Host "      - La URL sea correcta: $baseUrl" -ForegroundColor Gray
    Write-Host "      - No haya problemas de red o firewall" -ForegroundColor Gray
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  FIN DE LA PRUEBA" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

