-- Script para aplicar la migración de HangfireJobId a AppointmentTimers
-- Ejecutar este script directamente en PostgreSQL

-- Agregar columna HangfireJobId si no existe
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 
        FROM information_schema.columns 
        WHERE table_name = 'AppointmentTimers' 
        AND column_name = 'HangfireJobId'
    ) THEN
        ALTER TABLE "AppointmentTimers" 
        ADD COLUMN "HangfireJobId" character varying(255);
        
        RAISE NOTICE 'Columna HangfireJobId agregada exitosamente';
    ELSE
        RAISE NOTICE 'La columna HangfireJobId ya existe';
    END IF;
END $$;

-- Verificar que se agregó correctamente
SELECT column_name, data_type, character_maximum_length, is_nullable
FROM information_schema.columns
WHERE table_name = 'AppointmentTimers' AND column_name = 'HangfireJobId';

