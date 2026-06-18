ALTER TABLE "SearchServices" ADD COLUMN IF NOT EXISTS "InspectionTemplateConfig" text NULL;
ALTER TABLE "SearchServices" ADD COLUMN IF NOT EXISTS "InspectionTemplatePdfUrl" text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "InspectionTemplatePdfUrlSnapshot" text NULL;
