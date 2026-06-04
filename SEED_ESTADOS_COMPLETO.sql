-- ============================================================================
-- SEED COMPLETO DEL SISTEMA DE ESTADOS CENTRALIZADO (fuente de verdad)
-- ============================================================================
-- Reproduce el estado CORRECTO verificado en la BD viva (2026-05-25) incluyendo
-- todos los arreglos hechos a mano esta sesion. Las MIGRACIONES divergen de la BD
-- viva, asi que si recreas/redesplegas la BD ejecuta ESTE script para no perder:
--   * estados de cita intermedios (awaiting_report, report_sent)  -> flujo normal
--   * estados de timers (no_proposal, no_response, no_report, completed_without_approval, expert_second)
--   * fix de 1a cancelacion (by_client/by_expert: NO finalizan, mapping inactivo)
--   * configs de dinero + mappings
-- IDEMPOTENTE y CORRECTIVO: seguro en BD nueva o existente (INSERT WHERE NOT EXISTS
-- + UPDATE para forzar los flags correctos aunque la fila ya exista).
-- Politica de dinero confirmada por el usuario (5%): completed 0/95/5, cancelled 100/0/0,
-- dispute_resolved_client 90/8/2, dispute_resolved_expert 0/95/5.
-- ============================================================================

-- ---------- 1) SearchHireStatus (11) ----------
INSERT INTO "SystemStatuses" ("StatusType","StatusName","StatusValue","DisplayName","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT v."StatusType",v."StatusName",v."StatusValue",v."DisplayName",v."fin",true,v."sort",now(),now()
FROM (VALUES
  ('SearchHireStatus','Pending','pending','Pendiente',false,1),
  ('SearchHireStatus','AwaitingClientDecision','awaiting_client_decision','Esperando Decision del Cliente',false,2),
  ('SearchHireStatus','Disputed','disputed','En Disputa',true,3),
  ('SearchHireStatus','Completed','completed','Completado',true,4),
  ('SearchHireStatus','Cancelled','cancelled','Cancelado',true,5),
  ('SearchHireStatus','TransferFailed','transfer_failed','Transferencia Fallida',true,6),
  ('SearchHireStatus','DisputeResolvedClient','dispute_resolved_client','Disputa Resuelta a Favor del Cliente',true,7),
  ('SearchHireStatus','DisputeResolvedExpert','dispute_resolved_expert','Disputa Resuelta a Favor del Experto',true,8),
  ('SearchHireStatus','CancelledByClientNoProposal','cancelled_by_client_no_proposal','Cancelado por Cliente No Propone',true,9),
  ('SearchHireStatus','CancelledByExpertNoResponse','cancelled_by_expert_no_response','Cancelado por Experto No Responde',true,10),
  ('SearchHireStatus','CancelledByExpertNoReport','cancelled_by_expert_no_report','Cancelado por Experto No Envia Reporte',true,11)
) AS v("StatusType","StatusName","StatusValue","DisplayName","fin","sort")
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" s WHERE s."StatusValue"=v."StatusValue" AND s."StatusType"=v."StatusType");

-- ---------- 2) AppointmentStatus (17 + 3 opcionales cosmeticos) ----------
INSERT INTO "SystemStatuses" ("StatusType","StatusName","StatusValue","DisplayName","IsFinalizationStatus","IsActive","SortOrder","CreatedAt","UpdatedAt")
SELECT v."StatusType",v."StatusName",v."StatusValue",v."DisplayName",v."fin",true,v."sort",now(),now()
FROM (VALUES
  ('AppointmentStatus','AwaitingAppointment','awaiting_appointment','Esperando Cita',false,1),
  ('AppointmentStatus','AppointmentProposed','appointment_proposed','Cita Propuesta',false,2),
  ('AppointmentStatus','AppointmentConfirmed','appointment_confirmed','Cita Confirmada',false,3),
  ('AppointmentStatus','AppointmentRejected','appointment_rejected','Cita Rechazada',false,4),
  -- 1a cancelacion: NO finaliza (reprogramable)
  ('AppointmentStatus','AppointmentCancelledByClient','appointment_cancelled_by_client','Cancelado por Cliente',false,5),
  ('AppointmentStatus','AppointmentCancelledByClientSecond','appointment_cancelled_by_client_second','Cancelado por Cliente (Segunda)',true,6),
  ('AppointmentStatus','AppointmentCancelledByExpert','appointment_cancelled_by_expert','Cancelado por Experto',false,7),
  ('AppointmentStatus','AppointmentCancelledByNoResponse','appointment_cancelled_by_no_response','Cancelado por Falta de Respuesta',true,8),
  ('AppointmentStatus','AppointmentCancelledByExpertRejection','appointment_cancelled_by_expert_rejection','Cancelado por Rechazo del Experto',true,9),
  ('AppointmentStatus','AppointmentCompleted','appointment_completed','Cita Completada',true,10),
  -- estados de timers
  ('AppointmentStatus','AppointmentCancelledByClientNoProposal','appointment_cancelled_by_client_no_proposal','Cancelado por Cliente No Propone',true,11),
  ('AppointmentStatus','AppointmentCancelledByExpertNoResponse','appointment_cancelled_by_expert_no_response','Cancelado por Experto No Responde',true,12),
  ('AppointmentStatus','AppointmentCancelledByNoReport','appointment_cancelled_by_no_report','Cancelado por Falta de Reporte',true,13),
  ('AppointmentStatus','AppointmentCompletedWithoutClientApproval','appointment_completed_without_client_approval','Completado sin Aprobacion del Cliente',true,14),
  ('AppointmentStatus','AppointmentCancelledByExpertSecond','appointment_cancelled_by_expert_second','Cancelado por Experto (Segunda)',true,15),
  -- intermedios del flujo normal (sin ellos el camino feliz esta muerto)
  ('AppointmentStatus','AppointmentAwaitingReport','appointment_awaiting_report','Esperando Reporte',false,16),
  ('AppointmentStatus','AppointmentReportSent','appointment_report_sent','Reporte Enviado',false,17),
  -- cosmeticos / fallback (display de la cita en borrado de cuenta + fallback timer)
  ('AppointmentStatus','AppointmentCancelledByClientAccountDelete','appointment_cancelled_by_client_account_delete','Cancelado por Borrado de Cuenta (Cliente)',true,18),
  ('AppointmentStatus','AppointmentCancelledByExpertAccountDelete','appointment_cancelled_by_expert_account_delete','Cancelado por Borrado de Cuenta (Experto)',true,19),
  ('AppointmentStatus','AppointmentCompletedAuto','appointment_completed_auto','Completado Automaticamente',true,20)
) AS v("StatusType","StatusName","StatusValue","DisplayName","fin","sort")
WHERE NOT EXISTS (SELECT 1 FROM "SystemStatuses" s WHERE s."StatusValue"=v."StatusValue" AND s."StatusType"=v."StatusType");

-- ---------- 2b) CORRECTIVO: 1a cancelacion NO es finalizacion (aunque la fila ya exista) ----------
UPDATE "SystemStatuses" SET "IsFinalizationStatus"=false, "UpdatedAt"=now()
WHERE "StatusType"='AppointmentStatus'
  AND "StatusValue" IN ('appointment_cancelled_by_client','appointment_cancelled_by_expert')
  AND "IsFinalizationStatus"<>false;

-- ---------- 3) StatusConfigurations (7, globales, keyed por SearchHireStatus) ----------
INSERT INTO "StatusConfigurations" ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT s."Id",NULL,NULL,v."cp",v."ep",v."pp",true,now(),now()
FROM (VALUES
  ('completed',0,95,5),
  ('cancelled',100,0,0),
  ('dispute_resolved_client',90,8,2),
  ('dispute_resolved_expert',0,95,5),
  ('cancelled_by_client_no_proposal',0,100,0),
  ('cancelled_by_expert_no_response',100,0,0),
  ('cancelled_by_expert_no_report',95,0,5)
) AS v("sv","cp","ep","pp")
JOIN "SystemStatuses" s ON s."StatusValue"=v."sv" AND s."StatusType"='SearchHireStatus'
WHERE NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId"=s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- ---------- 3b) StatusConfigurations ligadas a AppointmentStatus (cancelaciones por TIMER) ----------
-- CRÍTICO: los timers llaman a ProcessMoneyDistributionAsync con el valor AppointmentStatus.
-- La config debe estar ligada a la fila AppointmentStatus o la 1ª búsqueda falla y cae a un
-- fallback roto (el enum SearchHireStatus no tiene estos estados granulares → resolvía a
-- 'cancelled' 100/0/0 para TODOS). Sin esto: no_proposal pagaba 100/0/0 (experto 0%) y
-- no_report pagaba 100/0/0 (plataforma 0%). NO cambia el estado final del hire, solo el dinero.
INSERT INTO "StatusConfigurations" ("StatusId","CategoryId","ServiceTypeCategoryId","ClientPercentage","ExpertPercentage","PlatformPercentage","IsActive","CreatedAt","UpdatedAt")
SELECT s."Id",NULL,NULL,v."cp",v."ep",v."pp",true,now(),now()
FROM (VALUES
  ('appointment_cancelled_by_client_no_proposal',   0, 100, 0),  -- culpa del cliente -> experto 100%
  ('appointment_cancelled_by_expert_no_response', 100,   0, 0),  -- culpa del experto -> cliente 100%
  ('appointment_cancelled_by_no_report',           95,   0, 5),  -- culpa del experto -> cliente 95% / plataforma 5%
  ('appointment_completed_without_client_approval', 0,  95, 5),  -- auto-aprobado a favor del experto -> 95% / 5%
  -- 🛡️ Round 28 MUD-AI: cliente cancela 2ª vez (tras 1ª permitida) en cita confirmada.
  -- ANTES: caía a mapping → 'cancelled' → 100/0/0 → cliente recupera 100% sin haber
  -- prestado servicio el experto. EXPLOIT: cliente paga, propone, expert confirma,
  -- cancela 2×, recupera todo el dinero, expert pierde su slot reservado.
  -- AHORA: cliente al fault → expert 95% / platform 5% (mismo que approve-without-decision).
  ('appointment_cancelled_by_client_second',        0,  95, 5),
  -- expert cancela 2× con cita confirmada (cliente sin culpa) → cliente 100%
  ('appointment_cancelled_by_expert_second',      100,   0, 0)
) AS v("sv","cp","ep","pp")
JOIN "SystemStatuses" s ON s."StatusValue"=v."sv" AND s."StatusType"='AppointmentStatus'
WHERE NOT EXISTS (SELECT 1 FROM "StatusConfigurations" sc WHERE sc."StatusId"=s."Id" AND sc."CategoryId" IS NULL AND sc."ServiceTypeCategoryId" IS NULL);

-- ---------- 4) StatusMappings (appointment -> hire) ----------
INSERT INTO "StatusMappings" ("SourceStatusId","TargetStatusId","IsActive","CreatedAt")
SELECT src."Id",tgt."Id",v."act",now()
FROM (VALUES
  ('appointment_cancelled_by_client','cancelled',false),         -- 1a cancelacion: inactivo (no finaliza)
  ('appointment_cancelled_by_client_second','cancelled',true),
  ('appointment_cancelled_by_expert','cancelled',false),         -- 1a cancelacion: inactivo
  ('appointment_cancelled_by_expert_rejection','cancelled',true),
  ('appointment_cancelled_by_no_response','cancelled',true),
  ('appointment_completed','completed',true)   -- FIX #3: cita completada -> hire 'completed' (antes apuntaba mal a 'awaiting_client_decision', un estado NO final)
) AS v("src","tgt","act")
JOIN "SystemStatuses" src ON src."StatusValue"=v."src" AND src."StatusType"='AppointmentStatus'
JOIN "SystemStatuses" tgt ON tgt."StatusValue"=v."tgt" AND tgt."StatusType"='SearchHireStatus'
WHERE NOT EXISTS (SELECT 1 FROM "StatusMappings" m WHERE m."SourceStatusId"=src."Id" AND m."TargetStatusId"=tgt."Id");

-- ---------- 4b) CORRECTIVO: 1a cancelacion mapping INACTIVO (aunque ya exista) ----------
UPDATE "StatusMappings" SET "IsActive"=false
WHERE "SourceStatusId" IN (
  SELECT "Id" FROM "SystemStatuses"
  WHERE "StatusType"='AppointmentStatus'
    AND "StatusValue" IN ('appointment_cancelled_by_client','appointment_cancelled_by_expert')
) AND "IsActive"<>false;
