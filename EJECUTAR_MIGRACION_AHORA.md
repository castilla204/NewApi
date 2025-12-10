# 🚀 Ejecutar Migración de Países - Instrucciones

## ✅ Migración Lista

La migración está creada y lista para aplicar. Ejecuta el siguiente SQL en PostgreSQL:

## 📝 SQL a Ejecutar

```sql
-- 1. Agregar campo Country a ExpertProfiles
ALTER TABLE "ExpertProfiles"
ADD COLUMN IF NOT EXISTS "Country" text NULL;

-- 2. Agregar campo ExpertCountry a SearchHires
ALTER TABLE "SearchHires"
ADD COLUMN IF NOT EXISTS "ExpertCountry" text NULL;

-- 3. Verificar que se crearon correctamente
SELECT 
    table_name,
    column_name,
    data_type,
    is_nullable
FROM information_schema.columns
WHERE (table_name = 'ExpertProfiles' AND column_name = 'Country')
   OR (table_name = 'SearchHires' AND column_name = 'ExpertCountry')
ORDER BY table_name, column_name;

-- 4. (Opcional) Registrar la migración en EF
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20250114120000_AddCountryToExpertProfileAndSearchHire', '10.0.0')
ON CONFLICT DO NOTHING;
```

## 🔧 Cómo Ejecutarlo

### Opción 1: Usando psql (Terminal)

```powershell
# Si tienes psql instalado y configurado
psql -h localhost -p 5433 -U admin -d atrapo -f APLICAR_MIGRACION_PAISES_SQL.sql
```

### Opción 2: Usando pgAdmin

1. Abre pgAdmin
2. Conéctate a tu base de datos
3. Abre Query Tool (clic derecho en la base de datos → Query Tool)
4. Copia y pega el SQL de arriba
5. Ejecuta (F5)

### Opción 3: Usando DBeaver o cualquier cliente SQL

1. Conéctate a PostgreSQL
2. Abre el archivo `APLICAR_MIGRACION_PAISES_SQL.sql`
3. Ejecuta el SQL

### Opción 4: Desde el script PowerShell

Si configuras la variable de entorno `POSTGRES_PASSWORD`:

```powershell
$env:POSTGRES_PASSWORD = "tu_contraseña"
.\aplicar-migracion-paises.ps1
```

## ✅ Verificación

Después de ejecutar el SQL, deberías ver en el resultado de la verificación:

```
table_name      | column_name    | data_type | is_nullable
----------------+----------------+-----------+-------------
ExpertProfiles  | Country        | text      | YES
SearchHires     | ExpertCountry  | text      | YES
```

## 📋 Archivos Disponibles

- `APLICAR_MIGRACION_PAISES_SQL.sql` - SQL completo listo para ejecutar
- `aplicar-migracion-paises.ps1` - Script PowerShell (requiere POSTGRES_PASSWORD)
- `Migrations/20250114120000_AddCountryToExpertProfileAndSearchHire.cs` - Migración EF Core

---

**¡Ejecuta el SQL y la migración estará aplicada!** 🎉




