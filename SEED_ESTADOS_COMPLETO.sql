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
  ('AppointmentStatus','AppointmentCompletedAuto','appointment_completed_auto','Completado Automaticamente',true,20),
  -- 🗓️ Cancelacion escalonada por antelacion (Fase D). Todas finalizan.
  ('AppointmentStatus','AppointmentCancelledByClientGt24h','appointment_cancelled_by_client_gt24h','Cancelado por Cliente >24h',true,21),
  ('AppointmentStatus','AppointmentCancelledByClient6to24h','appointment_cancelled_by_client_6to24h','Cancelado por Cliente 6-24h',true,22),
  ('AppointmentStatus','AppointmentCancelledByClientLt6h','appointment_cancelled_by_client_lt6h','Cancelado por Cliente <6h',true,23),
  ('AppointmentStatus','AppointmentCancelledByExpertStrike','appointment_cancelled_by_expert_strike','Cancelado por Experto',true,24)
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
  -- 🛡️ V8 FIX: 'pending' replica el reparto del camino feliz (0/95/5) para que el snapshot
  -- contractual capturado en HandlePendingHireCompleted deje de grabarse NULL. Sin esta fila,
  -- ClientPercentageSnapshot/Expert/Platform quedaban siempre null → RefundService nunca usaba
  -- el snapshot y la protección contra cambios retroactivos de % por admin no funcionaba.
  -- El guard isCancellationStatus en RefundService impide que esto afecte a cancelaciones.
  ('pending',0,95,5),
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
  ('appointment_cancelled_by_expert_second',      100,   0, 0),
  -- 🗓️ Cancelacion escalonada (Fase D): reparto por antelacion + actor.
  ('appointment_cancelled_by_client_gt24h',       100,   0, 0),  -- cliente >24h (con cupo N) -> reembolso integro
  ('appointment_cancelled_by_client_6to24h',       50,  50, 0),  -- cliente 6-24h (o >24h sin cupo) -> 50/50
  ('appointment_cancelled_by_client_lt6h',          0, 100, 0),  -- cliente <6h / no-show -> experto cobra todo
  ('appointment_cancelled_by_expert_strike',      100,   0, 0)   -- experto cancela -> cliente reembolso integro + strike
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
  -- 🧹 LEGACY (inactivo): estos dos estados pertenecen al sistema antiguo de proponer/aceptar/rechazar,
  -- que está retirado (#if false en AppointmentController/AppointmentService y SearchHireController).
  -- Ningún código vivo los produce: 'expert_rejection' solo se asignaba en RejectAppointmentAsync (#if false),
  -- y 'no_response' está marcado [DEPRECATED] en el enum. Se desactivan para no ensuciar el panel de mapeos.
  -- Si por datos antiguos alguna cita quedó en estos estados, el hire ya está finalizado y, ante cualquier
  -- re-evaluación, SystemStatusService.GetDefaultMapping (switch C#, líneas ~488-489) los resuelve igual a 'cancelled'.
  ('appointment_cancelled_by_expert_rejection','cancelled',false),
  ('appointment_cancelled_by_no_response','cancelled',false),
  ('appointment_completed','completed',true),   -- FIX #3: cita completada -> hire 'completed' (antes apuntaba mal a 'awaiting_client_decision', un estado NO final)
  -- 🗓️ Cancelacion escalonada (Fase D): todas mapean a hire 'cancelled' (activo = finaliza).
  ('appointment_cancelled_by_client_gt24h','cancelled',true),
  ('appointment_cancelled_by_client_6to24h','cancelled',true),
  ('appointment_cancelled_by_client_lt6h','cancelled',true),
  ('appointment_cancelled_by_expert_strike','cancelled',true)
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

-- ---------- 4c) CORRECTIVO: mapeos LEGACY del sistema antiguo de propuesta -> INACTIVOS (aunque ya existan) ----------
-- 'expert_rejection' y 'no_response' son estados muertos (flujo proponer/rechazar en #if false; 'no_response'
-- además [DEPRECATED]). El INSERT de arriba no los actualiza si ya existían, así que aquí se fuerza IsActive=false.
-- Inofensivo: GetDefaultMapping (C#) sigue resolviéndolos a 'cancelled' si hiciera falta.
UPDATE "StatusMappings" SET "IsActive"=false
WHERE "SourceStatusId" IN (
  SELECT "Id" FROM "SystemStatuses"
  WHERE "StatusType"='AppointmentStatus'
    AND "StatusValue" IN ('appointment_cancelled_by_expert_rejection','appointment_cancelled_by_no_response')
) AND "IsActive"<>false;
