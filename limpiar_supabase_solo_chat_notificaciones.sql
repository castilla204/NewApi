-- Script para limpiar Supabase: Eliminar todo excepto tablas necesarias para chat y notificaciones
-- Supabase ahora solo se usa para:
-- - Chat en tiempo real: Conversations, Messages, MessageAttachments
-- - Notificaciones: Notifications
-- - Users (necesario para referencias)

-- ⚠️ IMPORTANTE: Este script elimina TODAS las demás tablas de Supabase
-- Asegúrate de que todos los datos estén en Render PostgreSQL antes de ejecutar

-- Eliminar tablas que ya no se usan en Supabase (ordenadas por dependencias)

-- Tablas relacionadas con contrataciones y búsquedas
DROP TABLE IF EXISTS "SearchHireDeliverables" CASCADE;
DROP TABLE IF EXISTS "Disputes" CASCADE;
DROP TABLE IF EXISTS "DisputeFiles" CASCADE;
DROP TABLE IF EXISTS "AppointmentTimers" CASCADE;
DROP TABLE IF EXISTS "Appointments" CASCADE;
DROP TABLE IF EXISTS "SearchHires" CASCADE;
DROP TABLE IF EXISTS "SearchResultsFiltered" CASCADE;
DROP TABLE IF EXISTS "SearchResults" CASCADE;
DROP TABLE IF EXISTS "SearchParameterPlatforms" CASCADE;
DROP TABLE IF EXISTS "SearchParameters" CASCADE;
DROP TABLE IF EXISTS "Searches" CASCADE;

-- Tablas relacionadas con servicios
DROP TABLE IF EXISTS "SearchServiceDeliverableTypes" CASCADE;
DROP TABLE IF EXISTS "SearchServiceFavorites" CASCADE;
DROP TABLE IF EXISTS "SearchServiceImages" CASCADE;
DROP TABLE IF EXISTS "SearchServices" CASCADE;
DROP TABLE IF EXISTS "ServiceTypeCategories" CASCADE;
DROP TABLE IF EXISTS "ServiceTypes" CASCADE;
DROP TABLE IF EXISTS "CategoryServiceTypeConfigs" CASCADE;
DROP TABLE IF EXISTS "Categories" CASCADE;
DROP TABLE IF EXISTS "DeliverableTypes" CASCADE;

-- Tablas relacionadas con usuarios y perfiles (excepto Users que se mantiene)
DROP TABLE IF EXISTS "ExpertAvailabilities" CASCADE;
DROP TABLE IF EXISTS "ExpertProfiles" CASCADE;
DROP TABLE IF EXISTS "UserMfaSettings" CASCADE;
DROP TABLE IF EXISTS "UserSettings" CASCADE;
DROP TABLE IF EXISTS "UserSubscriptions" CASCADE;
DROP TABLE IF EXISTS "RefreshTokens" CASCADE;

-- Tablas relacionadas con reviews
DROP TABLE IF EXISTS "ReviewImages" CASCADE;
DROP TABLE IF EXISTS "Reviews" CASCADE;

-- Tablas relacionadas con plataformas y anuncios
DROP TABLE IF EXISTS "PlatformCategoryMappings" CASCADE;
DROP TABLE IF EXISTS "Platforms" CASCADE;
DROP TABLE IF EXISTS "Ads" CASCADE;
DROP TABLE IF EXISTS "AIs" CASCADE;

-- Tablas relacionadas con sistema y configuración
DROP TABLE IF EXISTS "StatusConfigurations" CASCADE;
DROP TABLE IF EXISTS "StatusMappings" CASCADE;
DROP TABLE IF EXISTS "SystemStatuses" CASCADE;
DROP TABLE IF EXISTS "SystemSettings" CASCADE;
DROP TABLE IF EXISTS "SubscriptionPlans" CASCADE;

-- Tablas relacionadas con transacciones financieras
DROP TABLE IF EXISTS "FinancialTransactions" CASCADE;

-- Tablas relacionadas con logging
DROP TABLE IF EXISTS "Logs" CASCADE;
DROP TABLE IF EXISTS "LogTypes" CASCADE;
DROP TABLE IF EXISTS "Severities" CASCADE;

-- Tablas relacionadas con webhooks
DROP TABLE IF EXISTS "ProcessedWebhookEvents" CASCADE;

-- Tablas relacionadas con favoritos
DROP TABLE IF EXISTS "Likes" CASCADE;

-- Tabla de migraciones (se puede mantener o eliminar según necesidad)
-- DROP TABLE IF EXISTS "__EFMigrationsHistory" CASCADE;

-- Verificar tablas restantes (deberían quedar solo: Conversations, Messages, MessageAttachments, Notifications, Users)
SELECT table_name 
FROM information_schema.tables 
WHERE table_schema = 'public' 
AND table_type = 'BASE TABLE'
ORDER BY table_name;
