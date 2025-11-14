# 🔄 **MIGRACIÓN: Soft Delete y ClientId Nullable**

## 📋 **RESUMEN**

Esta migración implementa soft delete para usuarios y hace ClientId nullable en SearchHires para permitir anonimización completa.

## 🎯 **CAMBIOS EN MODELOS**

### **1. User**
- ✅ `IsDeleted`: `bool` (default: false) - Campo nuevo para soft delete
- ✅ `DeletedAt`: `DateTime?` (nullable) - Timestamp de eliminación
- ✅ Query Filter: Excluye automáticamente usuarios con `IsDeleted = true`

### **2. SearchHire**
- ✅ `ClientId`: `int` → `int?` (nullable)
- ✅ `DeleteBehavior`: `Restrict` → `SetNull` (para permitir anonimización)

## 📝 **COMANDOS DE MIGRACIÓN**

```bash
# 1. Crear migración
dotnet ef migrations add AddSoftDeleteAndClientIdNullable

# 2. Revisar migración generada (verificar que los cambios sean correctos)

# 3. Aplicar migración
dotnet ef database update
```

## ⚠️ **NOTAS IMPORTANTES**

1. **Query Filter en User**: 
   - Los usuarios con `IsDeleted = true` serán excluidos automáticamente de todas las queries
   - Para acceder a usuarios eliminados, usar `.IgnoreQueryFilters()`
   - Ejemplo: `_context.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId)`

2. **ClientId Nullable**:
   - Los datos existentes tienen `ClientId` con valores, así que la migración debería ser segura
   - Después de la migración, `ClientId` puede ser `NULL` para contrataciones anonimizadas

3. **Soft Delete vs Hard Delete**:
   - **Soft Delete**: Marca `IsDeleted = true` y `DeletedAt = DateTime.UtcNow`
   - **Hard Delete**: Eliminación física (ya no se usa en producción)
   - Soft delete permite recuperación y cumplimiento legal

4. **Testing**: Probar en desarrollo antes de producción:
   - Verificar que usuarios eliminados no aparecen en queries normales
   - Verificar que `.IgnoreQueryFilters()` permite acceder a usuarios eliminados
   - Probar eliminación de cuenta con datos reales
   - Verificar que SearchHires se anonimizan correctamente (ClientId y ExpertId)

5. **Rollback**: Si necesitas revertir:
   - Crear migración que elimine `IsDeleted` y `DeletedAt`
   - Hacer `ClientId` NOT NULL de nuevo
   - **ADVERTENCIA**: Esto puede fallar si hay datos NULL existentes

## ✅ **VERIFICACIÓN POST-MIGRACIÓN**

```sql
-- Verificar que los campos existen
SELECT column_name, is_nullable, data_type 
FROM information_schema.columns 
WHERE table_name = 'Users' 
  AND column_name IN ('IsDeleted', 'DeletedAt');

SELECT column_name, is_nullable, data_type 
FROM information_schema.columns 
WHERE table_name = 'SearchHires' 
  AND column_name = 'ClientId';

-- Verificar que no hay usuarios eliminados pre-existentes (debería retornar 0)
SELECT COUNT(*) FROM "Users" WHERE "IsDeleted" = true;

-- Verificar que no hay SearchHires con ClientId NULL pre-existentes (debería retornar 0)
SELECT COUNT(*) FROM "SearchHires" WHERE "ClientId" IS NULL;
```

## 🔒 **SEGURIDAD**

- ✅ Soft delete permite recuperación en caso de error
- ✅ Query filter previene acceso accidental a usuarios eliminados
- ✅ ClientId nullable permite anonimización completa (GDPR compliant)
- ✅ El código de eliminación ya está actualizado para usar soft delete

## 📊 **IMPACTO**

- **Usuarios**: Ahora se marcan como eliminados en lugar de eliminarse físicamente
- **SearchHires**: ClientId puede ser NULL para contrataciones anonimizadas
- **Queries**: Usuarios eliminados no aparecen en queries normales (query filter)
- **Performance**: Mínimo impacto - solo agrega una condición WHERE automática

