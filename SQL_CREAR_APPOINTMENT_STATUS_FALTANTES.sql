-- ============================================================================
-- ESTADOS AppointmentStatus FALTANTES (requeridos por los timers)
-- ============================================================================
-- Los timers (ProcessAppointmentTimerAsync) buscan estas filas AppointmentStatus
-- y, si NO existen, la rama entera se salta (if (statusRow != null) {...}) → el
-- timer "expira" sin hacer nada → el hire queda ATASCADO y el dinero retenido.
--
-- La BD viva (2026-05-25) tiene 10 AppointmentStatus pero NO estas 5. Sin ellas:
--   proposal / response / expert_report  → no-op total (hire atascado)
--   client_decision                      → parcial (dinero vía fallback, cita desync)
--
-- NO se siembran StatusConfigurations aquí a propósito: el dinero lo resuelve
-- SystemStatusService.GetDefaultMapping (hardcoded) hacia estados SearchHire que
-- YA tienen config:
--   *_no_proposal / *_no_response / *_no_report  → Cancelled (100/0/0, cliente reembolsado)
--   completed_without_client_approval            → Completed (0/95/5, experto cobra)
-- Esto evita inventar porcentajes. (Política opcional: añadir StatusMappings si se
-- quiere penalizar al cliente que no propone o que la plataforma retenga su 5% en
-- el no-report — decisión de negocio, no incluida aquí.)
--
-- Idempotente (INSERT ... SELECT ... WHERE NOT EXISTS). Id lo asigna el identity.
-- ============================================================================

INSERT INTO "SystemStatuses"
    ("StatusType","StatusName","StatusValue","DisplayName","Description","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT 'AppointmentStatus','AppointmentCancelledByClientNoProposal','appointment_cancelled_by_client_no_proposal',
       'Cancelado por Cliente No Propone','Cliente no propuso cita en 24h (lado cita)',true,true,11,now(),now()
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" WHERE "StatusValue"='appointment_cancelled_by_client_no_proposal' AND "StatusType"='AppointmentStatus');

INSERT INTO "SystemStatuses"
    ("StatusType","StatusName","StatusValue","DisplayName","Description","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT 'AppointmentStatus','AppointmentCancelledByExpertNoResponse','appointment_cancelled_by_expert_no_response',
       'Cancelado por Experto No Responde','Experto no respondió a propuesta en 24h (lado cita)',true,true,12,now(),now()
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" WHERE "StatusValue"='appointment_cancelled_by_expert_no_response' AND "StatusType"='AppointmentStatus');

INSERT INTO "SystemStatuses"
    ("StatusType","StatusName","StatusValue","DisplayName","Description","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT 'AppointmentStatus','AppointmentCancelledByNoReport','appointment_cancelled_by_no_report',
       'Cancelado por Falta de Reporte','Experto no envió reporte en 24h (lado cita)',true,true,13,now(),now()
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" WHERE "StatusValue"='appointment_cancelled_by_no_report' AND "StatusType"='AppointmentStatus');

INSERT INTO "SystemStatuses"
    ("StatusType","StatusName","StatusValue","DisplayName","Description","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT 'AppointmentStatus','AppointmentCompletedWithoutClientApproval','appointment_completed_without_client_approval',
       'Completado sin Aprobación del Cliente','Cliente no respondió en 24h; completado a favor del experto (lado cita)',true,true,14,now(),now()
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" WHERE "StatusValue"='appointment_completed_without_client_approval' AND "StatusType"='AppointmentStatus');

INSERT INTO "SystemStatuses"
    ("StatusType","StatusName","StatusValue","DisplayName","Description","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT 'AppointmentStatus','AppointmentCancelledByExpertSecond','appointment_cancelled_by_expert_second',
       'Cancelado por Experto (Segunda)','Segunda cancelación del experto (lado cita)',true,true,15,now(),now()
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" WHERE "StatusValue"='appointment_cancelled_by_expert_second' AND "StatusType"='AppointmentStatus');
