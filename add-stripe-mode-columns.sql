-- Add StripeMode columns to SystemSettings table
-- Ejecutar este script directamente en PostgreSQL

-- Verificar si las columnas ya existen antes de agregarlas
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



