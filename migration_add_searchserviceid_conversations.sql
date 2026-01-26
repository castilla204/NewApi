-- ✅ MIGRACIÓN: Agregar SearchServiceId a Conversations para conversaciones previas a contratar
-- Fecha: 2026-01-XX
-- Descripción: Permite crear conversaciones antes de contratar un servicio

-- Agregar SearchServiceId nullable a Conversations para conversaciones previas a contratar
ALTER TABLE "Conversations" 
ADD COLUMN IF NOT EXISTS "SearchServiceId" integer;

-- Hacer SearchHireId nullable para permitir conversaciones previas
ALTER TABLE "Conversations" 
ALTER COLUMN "SearchHireId" DROP NOT NULL;

-- Agregar foreign key a SearchServices
ALTER TABLE "Conversations"
ADD CONSTRAINT "FK_Conversations_SearchServices_SearchServiceId" 
FOREIGN KEY ("SearchServiceId") 
REFERENCES "SearchServices" ("Id") 
ON DELETE CASCADE;

-- Agregar índice para búsquedas eficientes
CREATE INDEX IF NOT EXISTS "IX_Conversations_SearchServiceId" 
ON "Conversations" ("SearchServiceId");

-- Agregar índice para SearchHireId si no existe
CREATE INDEX IF NOT EXISTS "IX_Conversations_SearchHireId" 
ON "Conversations" ("SearchHireId");

-- ✅ NOTA: Esta migración debe aplicarse en la base de datos principal (no en Supabase)
-- Supabase solo contiene tablas de chat y notificaciones para Realtime
