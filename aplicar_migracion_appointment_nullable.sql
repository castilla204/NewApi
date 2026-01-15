-- Migración para hacer nullable ProposedDate, ProposedTime y Location en Appointments
-- Estos campos se asignan cuando el cliente propone la cita, no al crearla

-- Hacer nullable ProposedDate
ALTER TABLE "Appointments" 
ALTER COLUMN "ProposedDate" DROP NOT NULL;

-- Hacer nullable ProposedTime
ALTER TABLE "Appointments" 
ALTER COLUMN "ProposedTime" DROP NOT NULL;

-- Hacer nullable Location
ALTER TABLE "Appointments" 
ALTER COLUMN "Location" DROP NOT NULL;

-- Verificar que se aplicó correctamente
SELECT 
    column_name,
    data_type,
    is_nullable
FROM information_schema.columns
WHERE table_name = 'Appointments'
  AND column_name IN ('ProposedDate', 'ProposedTime', 'Location')
ORDER BY column_name;
