-- F3 FIX: índice único parcial IX_AppointmentTimers_Appt_Type_Active
--
-- Contexto: AppDbContext.cs líneas ~657-660 declara este índice en OnModelCreating,
-- pero NO existe en ninguna migración EF (debería estar en la migración
-- 20250917200411_AddAppointmentSystem.cs pero solo se añadieron índices simples).
-- En prod se creó manualmente; este script lo recrea de forma idempotente.
--
-- Garantiza: como máximo UN timer activo (IsExpired=false) por (AppointmentId, TimerType).
-- Sin esto, una race condition en CreateTimer podría duplicar entradas activas y los
-- watchdogs procesarían el mismo timer dos veces.
--
-- IDEMPOTENTE: usa IF NOT EXISTS, seguro de re-ejecutar.

CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppointmentTimers_Appt_Type_Active"
    ON "AppointmentTimers" ("AppointmentId", "TimerType")
    WHERE "IsExpired" = false;

-- Verificación:
-- SELECT indexname, indexdef FROM pg_indexes WHERE indexname = 'IX_AppointmentTimers_Appt_Type_Active';
