# 🔧 Cómo aplicar la migración de StripeMode

## Problema
La columna `StripeMode` no existe en la tabla `SystemSettings`, causando el error:
```
42703: column s.StripeMode does not exist
```

## Solución: Ejecutar SQL directamente

### Opción 1: Desde pgAdmin (Recomendado si lo tienes instalado)

1. Abre **pgAdmin**
2. Conéctate a tu base de datos PostgreSQL
3. Haz clic derecho en la base de datos `atrapo` → **Query Tool**
4. Copia y pega el siguiente SQL:

```sql
DO $$ 
BEGIN
    -- Add StripeMode column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeMode') THEN
        ALTER TABLE "SystemSettings" 
        ADD COLUMN "StripeMode" character varying(20) NOT NULL DEFAULT 'production';
        RAISE NOTICE 'Columna StripeMode agregada';
    ELSE
        RAISE NOTICE 'Columna StripeMode ya existe';
    END IF;

    -- Add StripeModeChangedAt column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeModeChangedAt') THEN
        ALTER TABLE "SystemSettings" 
        ADD COLUMN "StripeModeChangedAt" timestamp with time zone NULL;
        RAISE NOTICE 'Columna StripeModeChangedAt agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedAt ya existe';
    END IF;

    -- Add StripeModeChangedByUserId column
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                  WHERE table_name = 'SystemSettings' AND column_name = 'StripeModeChangedByUserId') THEN
        ALTER TABLE "SystemSettings" 
        ADD COLUMN "StripeModeChangedByUserId" integer NULL;
        RAISE NOTICE 'Columna StripeModeChangedByUserId agregada';
    ELSE
        RAISE NOTICE 'Columna StripeModeChangedByUserId ya existe';
    END IF;
END $$;
```

5. Haz clic en **Execute** (F5)

### Opción 2: Desde DBeaver o cualquier cliente SQL

1. Abre tu cliente SQL (DBeaver, DataGrip, etc.)
2. Conéctate a PostgreSQL
3. Ejecuta el SQL del archivo: `add-stripe-mode-columns.sql`

### Opción 3: Desde la aplicación (Endpoint temporal)

Si prefieres, puedo crear un endpoint temporal en la aplicación que ejecute este SQL. Solo necesitas llamar a:
```
POST /api/Admin/apply-stripe-migration
```

### Verificación

Después de ejecutar el SQL, verifica que las columnas se agregaron:

```sql
SELECT column_name, data_type, is_nullable, column_default
FROM information_schema.columns 
WHERE table_name = 'SystemSettings' 
AND column_name LIKE 'Stripe%'
ORDER BY column_name;
```

Deberías ver:
- `StripeMode` (varchar(20), NOT NULL, default: 'production')
- `StripeModeChangedAt` (timestamp with time zone, NULLABLE)
- `StripeModeChangedByUserId` (integer, NULLABLE)

## Después de aplicar

Una vez aplicada la migración, el endpoint `/api/Admin/stripe/mode` debería funcionar correctamente.

---

**Nota:** El archivo `add-stripe-mode-columns.sql` contiene el SQL listo para ejecutar.






