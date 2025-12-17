# 🔧 Solución: Errores al Aplicar Migración de Países

## 🚨 Problema

El comando `dotnet ef migrations list` o `dotnet ef database update` falla porque el proyecto `DataLayer` no compila debido a errores preexistentes (no relacionados con la migración).

## ✅ Solución: Aplicar Migración Manualmente en PostgreSQL

Como la migración SQL es simple y los errores de compilación son preexistentes, puedes aplicar la migración directamente en PostgreSQL.

### Opción 1: Ejecutar SQL Directamente (Recomendado)

1. **Conectarte a PostgreSQL** (usando pgAdmin, DBeaver, psql, o cualquier cliente SQL)

2. **Ejecutar el script SQL** que está en `APLICAR_MIGRACION_PAISES_SQL.sql`:

```sql
-- Agregar campo Country a ExpertProfiles
ALTER TABLE "ExpertProfiles"
ADD COLUMN IF NOT EXISTS "Country" text NULL;

-- Agregar campo ExpertCountry a SearchHires
ALTER TABLE "SearchHires"
ADD COLUMN IF NOT EXISTS "ExpertCountry" text NULL;
```

3. **Verificar que se aplicó correctamente:**

```sql
SELECT 
    table_name,
    column_name,
    data_type,
    is_nullable
FROM information_schema.columns
WHERE (table_name = 'ExpertProfiles' AND column_name = 'Country')
   OR (table_name = 'SearchHires' AND column_name = 'ExpertCountry')
ORDER BY table_name, column_name;
```

**Resultado esperado:**
```
table_name      | column_name    | data_type | is_nullable
----------------+----------------+-----------+-------------
ExpertProfiles  | Country        | text      | YES
SearchHires     | ExpertCountry  | text      | YES
```

### Opción 2: Usar psql desde la Terminal

```powershell
# Conectarte a PostgreSQL (ajusta los parámetros según tu configuración)
psql -h localhost -U tu_usuario -d tu_base_de_datos

# Luego ejecutar:
ALTER TABLE "ExpertProfiles" ADD COLUMN IF NOT EXISTS "Country" text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "ExpertCountry" text NULL;

# Verificar:
\dt ExpertProfiles
\d ExpertProfiles
\d SearchHires
```

### Opción 3: Marcar la Migración como Aplicada (Después de Ejecutar SQL)

Si ejecutaste el SQL manualmente, necesitas registrar la migración en la tabla `__EFMigrationsHistory`:

```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20250114120000_AddCountryToExpertProfileAndSearchHire', '10.0.0')
ON CONFLICT DO NOTHING;
```

Esto le dice a Entity Framework que la migración ya está aplicada.

## 🔍 Verificación Post-Migración

### 1. Verificar Columnas en PostgreSQL

```sql
SELECT 
    table_name,
    column_name,
    data_type,
    is_nullable,
    column_default
FROM information_schema.columns
WHERE (table_name = 'ExpertProfiles' AND column_name = 'Country')
   OR (table_name = 'SearchHires' AND column_name = 'ExpertCountry');
```

### 2. Probar la Funcionalidad

Una vez aplicada la migración:

1. **Registrar un nuevo experto** con coordenadas
   - El sistema debería detectar automáticamente el país
   - Verificar en la BD: `SELECT "Country" FROM "ExpertProfiles" WHERE "Id" = [id_del_experto];`

2. **Actualizar ubicación de un experto**
   - Cambiar las coordenadas
   - Verificar que `Country` se actualiza

3. **Crear una contratación**
   - Verificar que `ExpertCountry` se guarda: `SELECT "ExpertCountry" FROM "SearchHires" WHERE "Id" = [id_contratacion];`

## ⚠️ Notas Importantes

1. **Los errores de compilación son preexistentes** y no están relacionados con esta migración
2. **La migración SQL es segura** - solo agrega columnas nullable, no modifica datos existentes
3. **Los campos serán NULL** para expertos y contrataciones existentes hasta que se actualicen
4. **No hay riesgo de pérdida de datos** - solo se agregan columnas nuevas

## 🐛 Si Algo Sale Mal

### Error: "column already exists"
Si la columna ya existe, no hay problema. El `IF NOT EXISTS` debería prevenir esto, pero si ocurre, simplemente ignóralo.

### Error: "permission denied"
Asegúrate de tener permisos de ALTER TABLE en la base de datos. Usa un usuario con privilegios suficientes.

### Error: "table does not exist"
Verifica que los nombres de las tablas sean correctos (con mayúsculas/minúsculas):
- `"ExpertProfiles"` (con comillas, case-sensitive)
- `"SearchHires"` (con comillas, case-sensitive)

## ✅ Checklist

- [ ] Ejecutar SQL manualmente en PostgreSQL
- [ ] Verificar que las columnas existen
- [ ] Registrar migración en `__EFMigrationsHistory` (opcional pero recomendado)
- [ ] Probar registro de nuevo experto
- [ ] Probar actualización de ubicación
- [ ] Probar creación de contratación
- [ ] Verificar que los DTOs devuelven los campos

---

**¡Listo!** Una vez aplicada la migración manualmente, el sistema funcionará correctamente. 🚀








