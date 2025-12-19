# 📋 Instrucciones para Aplicar la Migración

## ✅ Migración Creada

**Archivo:** `Migrations/20251217150000_AddGeospatialIndexesToExpertProfiles.cs`

**Índices a crear:**
1. `IX_ExpertProfiles_Latitude_Longitude` - Índice compuesto
2. `IX_ExpertProfiles_Latitude` - Índice individual en Latitude
3. `IX_ExpertProfiles_Longitude` - Índice individual en Longitude

## 🚀 Aplicar Migración

### **Opción 1: Usando Entity Framework (Recomendado)**

```bash
# Desde el directorio del proyecto
dotnet ef database update --context AppDbContext
```

### **Opción 2: SQL Directo (Si EF no funciona)**

Si por alguna razón no puedes usar EF, ejecuta este SQL directamente en PostgreSQL:

```sql
-- Índice compuesto en (Latitude, Longitude)
CREATE INDEX "IX_ExpertProfiles_Latitude_Longitude" 
ON "ExpertProfiles" ("Latitude", "Longitude")
WHERE "Latitude" IS NOT NULL AND "Latitude" != '' AND "Longitude" IS NOT NULL AND "Longitude" != '';

-- Índice individual en Latitude
CREATE INDEX "IX_ExpertProfiles_Latitude" 
ON "ExpertProfiles" ("Latitude")
WHERE "Latitude" IS NOT NULL AND "Latitude" != '';

-- Índice individual en Longitude
CREATE INDEX "IX_ExpertProfiles_Longitude" 
ON "ExpertProfiles" ("Longitude")
WHERE "Longitude" IS NOT NULL AND "Longitude" != '';
```

## ✅ Verificar que se Aplicó Correctamente

```sql
-- Verificar índices creados
SELECT 
    indexname, 
    indexdef 
FROM pg_indexes 
WHERE tablename = 'ExpertProfiles' 
AND indexname LIKE '%Latitude%' OR indexname LIKE '%Longitude%';
```

Deberías ver 3 índices:
- `IX_ExpertProfiles_Latitude_Longitude`
- `IX_ExpertProfiles_Latitude`
- `IX_ExpertProfiles_Longitude`

## 🔍 Verificar Rendimiento

Después de aplicar los índices, las consultas con bounds deberían ser más rápidas:

```sql
-- Probar consulta con EXPLAIN para ver si usa los índices
EXPLAIN ANALYZE
SELECT * FROM "ExpertProfiles"
WHERE "Latitude" >= '40.3' AND "Latitude" <= '40.5'
AND "Longitude" >= '-3.8' AND "Longitude" <= '-3.6';
```

Si ves `Index Scan` en el resultado, los índices están funcionando correctamente.

## ⚠️ Nota

Si el proceso está bloqueando el archivo DLL (como vimos antes), puedes:
1. Detener la aplicación/debugger
2. Aplicar la migración
3. Reiniciar la aplicación

O usar la Opción 2 (SQL directo) que no requiere compilar.

