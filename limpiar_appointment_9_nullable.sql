-- Limpiar valores temporales del Appointment 9 ahora que los campos son nullable
UPDATE "Appointments" 
SET 
    "ProposedDate" = NULL,
    "ProposedTime" = NULL,
    "Location" = NULL
WHERE "Id" = 9 
  AND ("Location" = 'Pendiente de propuesta' OR "ProposedDate" IS NOT NULL);

-- Verificar que quedó correcto
SELECT 
    "Id",
    "ProposedDate",
    "ProposedTime",
    "Location",
    "StatusId"
FROM "Appointments"
WHERE "Id" = 9;
