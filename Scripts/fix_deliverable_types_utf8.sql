-- Corrige acentos en catálogo de entregables (UTF-8)
BEGIN;

UPDATE "DeliverableTypes"
SET
  "DisplayName" = 'Informe PDF',
  "Description" = 'Informe detallado en formato PDF con hallazgos y fotos.',
  "UpdatedAt" = NOW()
WHERE "Name" = 'PDF';

UPDATE "DeliverableTypes"
SET
  "DisplayName" = 'Vídeo de inspección',
  "Description" = 'Vídeo grabado durante la revisión presencial.',
  "UpdatedAt" = NOW()
WHERE "Name" = 'Video';

COMMIT;

SELECT "Id", "Name", "DisplayName", "Description" FROM "DeliverableTypes" ORDER BY "SortOrder";
