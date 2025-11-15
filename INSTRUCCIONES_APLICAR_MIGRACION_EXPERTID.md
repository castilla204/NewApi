# Instrucciones para Aplicar Migración: MakeExpertIdNullableInSearchHires

## 🎯 Objetivo
Hacer que `ExpertId` sea nullable en la tabla `SearchHires` para permitir la anonimización completa durante la eliminación de cuentas.

## ✅ Estado Actual
- ✅ **Modelo C#**: `ExpertId` ya es `int?` (nullable) en `SearchHire.cs`
- ✅ **Migración creada**: `20251115203709_MakeExpertIdNullableInSearchHires.cs`
- ❌ **Base de datos**: `ExpertId` todavía tiene restricción NOT NULL

## 📋 Pasos para Aplicar la Migración

### 1. Verificar que la migración existe
La migración ya está creada en:
```
Migrations/20251115203709_MakeExpertIdNullableInSearchHires.cs
```

### 2. Aplicar la migración a la base de datos

**Opción A: Usando dotnet ef (Recomendado)**
```powershell
cd "C:\Users\Diego\OneDrive - Educacyl\Escritorio\App\newApi"
dotnet ef database update
```

**Opción B: Verificar migraciones pendientes primero**
```powershell
dotnet ef migrations list
```

**Opción C: Aplicar solo esta migración específica**
```powershell
dotnet ef database update MakeExpertIdNullableInSearchHires
```

### 3. Verificar que se aplicó correctamente

**Opción A: Verificar en PostgreSQL**
```sql
SELECT 
    column_name, 
    data_type, 
    is_nullable
FROM information_schema.columns
WHERE table_name = 'SearchHires' 
  AND column_name = 'ExpertId';
```

**Resultado esperado:**
- `is_nullable` debe ser `YES` o `true`

**Opción B: Verificar con EF Core**
```powershell
dotnet ef migrations list
```
Debe mostrar que `MakeExpertIdNullableInSearchHires` está aplicada.

## 🔍 Verificación Post-Migración

### 1. Probar eliminación de cuenta
Una vez aplicada la migración, prueba eliminar una cuenta de un usuario que sea experto en alguna contratación:

```http
POST /api/AccountDeletion/delete
{
  "reason": "Test de eliminación"
}
```

### 2. Verificar logs
Busca en los logs que no aparezca el warning:
```
"ExpertId cannot be anonymized - NOT NULL constraint"
```

### 3. Verificar en base de datos
Después de eliminar una cuenta de experto, verifica que `ExpertId` sea NULL:

```sql
SELECT 
    sh."Id",
    sh."ClientId",
    sh."ExpertId",
    sh."Amount"
FROM "SearchHires" sh
WHERE sh."ExpertId" IS NULL
  AND sh."UpdatedAt" > NOW() - INTERVAL '1 hour';
```

## ⚠️ Notas Importantes

1. **Backup**: Antes de aplicar la migración, considera hacer un backup de la base de datos
2. **Downtime**: Esta migración es rápida (solo cambia una restricción), pero es recomendable aplicarla en un momento de bajo tráfico
3. **Rollback**: Si necesitas revertir, usa:
   ```powershell
   dotnet ef database update [nombre_migracion_anterior]
   ```

## ✅ Después de Aplicar la Migración

Una vez aplicada la migración:
- ✅ `ExpertId` será nullable en la base de datos
- ✅ La anonimización de `ExpertId` funcionará correctamente
- ✅ El warning en los logs desaparecerá
- ✅ La eliminación de cuentas funcionará al 100%

## 🐛 Si Algo Sale Mal

### Error: "Migration already applied"
Si la migración ya está aplicada, no hay problema. Verifica con:
```powershell
dotnet ef migrations list
```

### Error: "Foreign key constraint"
Si hay un error de foreign key, verifica que no haya datos inconsistentes:
```sql
SELECT COUNT(*) 
FROM "SearchHires" 
WHERE "ExpertId" IS NOT NULL 
  AND "ExpertId" NOT IN (SELECT "Id" FROM "Users");
```

### Error: "Column does not exist"
Si el error dice que la columna no existe, verifica el nombre de la tabla:
```sql
SELECT column_name 
FROM information_schema.columns 
WHERE table_name = 'SearchHires';
```

## 📝 Resumen

1. ✅ Migración creada: `20251115203709_MakeExpertIdNullableInSearchHires.cs`
2. ⏳ **Pendiente**: Aplicar migración con `dotnet ef database update`
3. ✅ Código ya está preparado para manejar el caso (con try-catch)
4. ✅ Después de aplicar: Eliminación de cuentas funcionará al 100%

