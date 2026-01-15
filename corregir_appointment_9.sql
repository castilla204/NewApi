-- Corregir Appointment 9 con valores temporales válidos
UPDATE "Appointments" 
SET 
    "ProposedDate" = CURRENT_DATE,
    "ProposedTime" = '00:00:00'::time,
    "Location" = 'Pendiente de propuesta'
WHERE "Id" = 9 
  AND ("ProposedDate" = '-infinity'::timestamp OR "Location" = '' OR "Location" IS NULL);

-- Verificar que se corrigió
SELECT 
    "Id",
    "ProposedDate",
    "ProposedTime",
    "Location",
    "StatusId"
FROM "Appointments"
WHERE "Id" = 9;
