# Guía de Pruebas: Tipo de Servicio "Revisión presencial"

Esta guía te ayudará a probar el tipo de servicio "Revisión presencial" en la API desplegada en Azure.

## 🌐 URL Base de la API
```
https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net
```

---

## 📋 Endpoints Disponibles

### 1. Obtener Todos los Tipos de Servicio (Público - Sin Autenticación)

**Endpoint:** `GET /api/ServiceType`  
**Autenticación:** No requerida  
**Descripción:** Obtiene todos los tipos de servicio activos, incluyendo "Revisión presencial"

#### Ejemplo con cURL:
```bash
curl -X GET "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/api/ServiceType" \
  -H "Content-Type: application/json"
```

#### Ejemplo con PowerShell:
```powershell
$response = Invoke-RestMethod -Uri "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/api/ServiceType" -Method GET -ContentType "application/json"
$response | ConvertTo-Json -Depth 10
```

#### Ejemplo con JavaScript (fetch):
```javascript
fetch('https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/api/ServiceType')
  .then(response => response.json())
  .then(data => {
    console.log('Tipos de servicio:', data);
    // Buscar "Revisión presencial"
    const revisionPresencial = data.data.find(st => st.name === 'Revisión presencial');
    console.log('Revisión presencial:', revisionPresencial);
  });
```

#### Respuesta Esperada:
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "name": "Revisión presencial",
      "description": "Servicio de revisión presencial de productos o servicios",
      "serviceTypeCategoryId": 2,
      "serviceTypeCategoryName": "Revisión",
      "position": 1,
      "isActive": true,
      "requiresAppointment": true,
      "createdAt": "2026-01-03T16:38:03.575968Z",
      "updatedAt": "2026-01-03T16:38:03.575968Z"
    },
    // ... otros tipos de servicio
  ],
  "count": 1,
  "message": "Service types retrieved successfully"
}
```

---

### 2. Obtener Tipo de Servicio Específico por ID

**Endpoint:** `GET /api/ServiceType/{id}`  
**Autenticación:** Requerida (JWT Token)  
**Descripción:** Obtiene un tipo de servicio específico por su ID

#### Ejemplo con cURL:
```bash
# Primero obtén un token JWT (desde el endpoint de login)
TOKEN="tu_token_jwt_aqui"

curl -X GET "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/api/ServiceType/1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json"
```

#### Ejemplo con PowerShell:
```powershell
$token = "tu_token_jwt_aqui"
$headers = @{
    "Authorization" = "Bearer $token"
    "Content-Type" = "application/json"
}

$response = Invoke-RestMethod -Uri "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/api/ServiceType/1" `
  -Method GET -Headers $headers
$response | ConvertTo-Json -Depth 10
```

---

### 3. Endpoint Público Alternativo

**Endpoint:** `GET /api/ServiceType/public`  
**Autenticación:** No requerida  
**Descripción:** Versión pública simplificada del endpoint de tipos de servicio

#### Ejemplo con cURL:
```bash
curl -X GET "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/api/ServiceType/public" \
  -H "Content-Type: application/json"
```

---

## ✅ Verificación Rápida

### Script PowerShell Completo para Verificar:

```powershell
# Configurar URL base
$baseUrl = "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net"

Write-Host "🔍 Verificando tipo de servicio 'Revisión presencial'..." -ForegroundColor Cyan

# Obtener todos los tipos de servicio
try {
    $response = Invoke-RestMethod -Uri "$baseUrl/api/ServiceType" -Method GET -ContentType "application/json"
    
    if ($response.success) {
        Write-Host "✅ Conexión exitosa con la API" -ForegroundColor Green
        Write-Host "📊 Total de tipos de servicio: $($response.count)" -ForegroundColor Yellow
        
        # Buscar "Revisión presencial"
        $revisionPresencial = $response.data | Where-Object { $_.name -eq "Revisión presencial" }
        
        if ($revisionPresencial) {
            Write-Host "`n✅ 'Revisión presencial' encontrado:" -ForegroundColor Green
            Write-Host "   ID: $($revisionPresencial.id)" -ForegroundColor White
            Write-Host "   Nombre: $($revisionPresencial.name)" -ForegroundColor White
            Write-Host "   Descripción: $($revisionPresencial.description)" -ForegroundColor White
            Write-Host "   Categoría: $($revisionPresencial.serviceTypeCategoryName) (ID: $($revisionPresencial.serviceTypeCategoryId))" -ForegroundColor White
            Write-Host "   Requiere Cita: $($revisionPresencial.requiresAppointment)" -ForegroundColor White
            Write-Host "   Activo: $($revisionPresencial.isActive)" -ForegroundColor White
            Write-Host "   Posición: $($revisionPresencial.position)" -ForegroundColor White
        } else {
            Write-Host "`n❌ 'Revisión presencial' NO encontrado en la lista" -ForegroundColor Red
            Write-Host "Tipos de servicio disponibles:" -ForegroundColor Yellow
            $response.data | ForEach-Object { Write-Host "   - $($_.name) (ID: $($_.id))" -ForegroundColor Gray }
        }
    } else {
        Write-Host "❌ Error en la respuesta: $($response.message)" -ForegroundColor Red
    }
} catch {
    Write-Host "❌ Error al conectar con la API: $($_.Exception.Message)" -ForegroundColor Red
}
```

---

## 🧪 Pruebas Adicionales

### Verificar que el Tipo de Servicio se Puede Usar en Servicios

El tipo de servicio "Revisión presencial" debería estar disponible cuando se crean o actualizan servicios. Para verificar esto:

1. **Crear un servicio con este tipo:**
   - Endpoint: `POST /api/SearchService`
   - Requiere autenticación y rol de Experto
   - En el body, incluir `serviceTypeId: 1` (ID de "Revisión presencial")

2. **Verificar en búsquedas:**
   - Los servicios con tipo "Revisión presencial" deberían aparecer en las búsquedas
   - Endpoint: `GET /api/Search` (con filtros apropiados)

---

## 🔧 Solución de Problemas

### Si el tipo de servicio no aparece:

1. **Verificar en la base de datos directamente:**
   ```sql
   SELECT * FROM "ServiceTypes" WHERE "Name" = 'Revisión presencial';
   ```

2. **Verificar que está activo:**
   ```sql
   SELECT "Id", "Name", "IsActive" FROM "ServiceTypes" WHERE "Name" = 'Revisión presencial';
   ```

3. **Si no existe, ejecutar el script SQL:**
   - Archivo: `add_revision_presencial_service_type.sql`
   - Ejecutar en la base de datos de producción

### Si la API no responde:

1. Verificar que la API está desplegada y funcionando:
   ```bash
   curl -X GET "https://inspeccionoapi-cgh5amebepbje7dz.spaincentral-01.azurewebsites.net/health"
   ```

2. Verificar logs de Azure App Service para errores

---

## 📝 Notas Importantes

- El tipo de servicio "Revisión presencial" tiene `requiresAppointment: true`, lo que significa que requiere cita presencial
- Está asociado a la categoría "Revisión" (ServiceTypeCategoryId = 2)
- El endpoint `/api/ServiceType` es público (no requiere autenticación) para facilitar la exploración de servicios

---

## 🚀 Próximos Pasos

Una vez verificado que el tipo de servicio existe:

1. **Probar creación de servicios** con este tipo de servicio
2. **Verificar en el frontend** que aparece en los selectores
3. **Probar flujo completo** de contratación con este tipo de servicio
4. **Verificar que las citas** se crean correctamente cuando `requiresAppointment = true`

