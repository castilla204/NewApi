START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928085430_RemoveAppointmentCompletedFromCategoryServiceTypeConfigs') THEN
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 1 AND "ServiceTypeCategoryId" = 1 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 1 AND "ServiceTypeCategoryId" = 2 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 1 AND "ServiceTypeCategoryId" = 3 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 2 AND "ServiceTypeCategoryId" = 1 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 2 AND "ServiceTypeCategoryId" = 2 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 2 AND "ServiceTypeCategoryId" = 3 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 3 AND "ServiceTypeCategoryId" = 1 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 3 AND "ServiceTypeCategoryId" = 2 AND "Status" = 'appointment_completed';
    DELETE FROM "CategoryServiceTypeConfigs"
    WHERE "CategoryId" = 3 AND "ServiceTypeCategoryId" = 3 AND "Status" = 'appointment_completed';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928085430_RemoveAppointmentCompletedFromCategoryServiceTypeConfigs') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928085430_RemoveAppointmentCompletedFromCategoryServiceTypeConfigs', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928090126_AddCompletedStatusToCategoryServiceTypeConfigs') THEN
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 1, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198487Z', TIMESTAMPTZ '2026-01-03T15:32:35.198488Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 2, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198488Z', TIMESTAMPTZ '2026-01-03T15:32:35.198488Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 3, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198488Z', TIMESTAMPTZ '2026-01-03T15:32:35.198488Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 1, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198489Z', TIMESTAMPTZ '2026-01-03T15:32:35.198489Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 2, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198489Z', TIMESTAMPTZ '2026-01-03T15:32:35.198491Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 3, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198491Z', TIMESTAMPTZ '2026-01-03T15:32:35.198491Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 1, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198491Z', TIMESTAMPTZ '2026-01-03T15:32:35.198491Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 2, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198492Z', TIMESTAMPTZ '2026-01-03T15:32:35.198492Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 3, 'completed', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.198492Z', TIMESTAMPTZ '2026-01-03T15:32:35.198492Z');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928090126_AddCompletedStatusToCategoryServiceTypeConfigs') THEN
    PERFORM setval(
        pg_get_serial_sequence('"CategoryServiceTypeConfigs"', 'Id'),
        GREATEST(
            (SELECT MAX("Id") FROM "CategoryServiceTypeConfigs") + 1,
            nextval(pg_get_serial_sequence('"CategoryServiceTypeConfigs"', 'Id'))),
        false);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928090126_AddCompletedStatusToCategoryServiceTypeConfigs') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928090126_AddCompletedStatusToCategoryServiceTypeConfigs', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE TABLE "SystemStatuses" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "StatusType" character varying(50) NOT NULL,
        "StatusName" character varying(50) NOT NULL,
        "StatusValue" character varying(50) NOT NULL,
        "DisplayName" character varying(100) NOT NULL,
        "Description" character varying(500),
        "IsActive" boolean NOT NULL,
        "SortOrder" integer NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SystemStatuses" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE TABLE "StatusConfigurations" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "StatusId" integer NOT NULL,
        "CategoryId" integer,
        "ServiceTypeCategoryId" integer,
        "ClientPercentage" numeric NOT NULL,
        "ExpertPercentage" numeric NOT NULL,
        "PlatformPercentage" numeric NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_StatusConfigurations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_StatusConfigurations_Categories_CategoryId" FOREIGN KEY ("CategoryId") REFERENCES "Categories" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_StatusConfigurations_ServiceTypeCategories_ServiceTypeCateg~" FOREIGN KEY ("ServiceTypeCategoryId") REFERENCES "ServiceTypeCategories" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_StatusConfigurations_SystemStatuses_StatusId" FOREIGN KEY ("StatusId") REFERENCES "SystemStatuses" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE TABLE "StatusMappings" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "SourceStatusId" integer NOT NULL,
        "TargetStatusId" integer NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_StatusMappings" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_StatusMappings_SystemStatuses_SourceStatusId" FOREIGN KEY ("SourceStatusId") REFERENCES "SystemStatuses" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_StatusMappings_SystemStatuses_TargetStatusId" FOREIGN KEY ("TargetStatusId") REFERENCES "SystemStatuses" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_StatusConfigurations_CategoryId" ON "StatusConfigurations" ("CategoryId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_StatusConfigurations_IsActive" ON "StatusConfigurations" ("IsActive");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_StatusConfigurations_ServiceTypeCategoryId" ON "StatusConfigurations" ("ServiceTypeCategoryId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE UNIQUE INDEX "IX_StatusConfigurations_StatusId_CategoryId_ServiceTypeCategor~" ON "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_StatusMappings_IsActive" ON "StatusMappings" ("IsActive");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE UNIQUE INDEX "IX_StatusMappings_SourceStatusId_TargetStatusId" ON "StatusMappings" ("SourceStatusId", "TargetStatusId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_StatusMappings_TargetStatusId" ON "StatusMappings" ("TargetStatusId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_SystemStatuses_IsActive" ON "SystemStatuses" ("IsActive");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE INDEX "IX_SystemStatuses_StatusType" ON "SystemStatuses" ("StatusType");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    CREATE UNIQUE INDEX "IX_SystemStatuses_StatusType_StatusValue" ON "SystemStatuses" ("StatusType", "StatusValue");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'Pending', 'pending', 'Pendiente', 'Contratación pendiente', TRUE, 1, TIMESTAMPTZ '2026-01-03T15:32:35.281122Z', TIMESTAMPTZ '2026-01-03T15:32:35.281123Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'AwaitingClientDecision', 'awaiting_client_decision', 'Esperando Decisión del Cliente', 'Esperando decisión del cliente', TRUE, 2, TIMESTAMPTZ '2026-01-03T15:32:35.281124Z', TIMESTAMPTZ '2026-01-03T15:32:35.281124Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'Disputed', 'disputed', 'En Disputa', 'En disputa', TRUE, 3, TIMESTAMPTZ '2026-01-03T15:32:35.281125Z', TIMESTAMPTZ '2026-01-03T15:32:35.281125Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'Completed', 'completed', 'Completado', 'Servicio completado', TRUE, 4, TIMESTAMPTZ '2026-01-03T15:32:35.281125Z', TIMESTAMPTZ '2026-01-03T15:32:35.281125Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'Cancelled', 'cancelled', 'Cancelado', 'Cancelado (genérico)', TRUE, 5, TIMESTAMPTZ '2026-01-03T15:32:35.281125Z', TIMESTAMPTZ '2026-01-03T15:32:35.281125Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'TransferFailed', 'transfer_failed', 'Transferencia Fallida', 'Transferencia fallida', TRUE, 6, TIMESTAMPTZ '2026-01-03T15:32:35.281127Z', TIMESTAMPTZ '2026-01-03T15:32:35.281127Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'DisputeResolvedClient', 'dispute_resolved_client', 'Disputa Resuelta a Favor del Cliente', 'Disputa resuelta a favor del cliente', TRUE, 7, TIMESTAMPTZ '2026-01-03T15:32:35.281127Z', TIMESTAMPTZ '2026-01-03T15:32:35.281127Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('SearchHireStatus', 'DisputeResolvedExpert', 'dispute_resolved_expert', 'Disputa Resuelta a Favor del Experto', 'Disputa resuelta a favor del experto', TRUE, 8, TIMESTAMPTZ '2026-01-03T15:32:35.281127Z', TIMESTAMPTZ '2026-01-03T15:32:35.281127Z');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AwaitingAppointment', 'awaiting_appointment', 'Esperando Cita', 'Esperando propuesta del cliente (48h)', TRUE, 1, TIMESTAMPTZ '2026-01-03T15:32:35.281129Z', TIMESTAMPTZ '2026-01-03T15:32:35.281129Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentProposed', 'appointment_proposed', 'Cita Propuesta', 'Cliente propuso cita', TRUE, 2, TIMESTAMPTZ '2026-01-03T15:32:35.28113Z', TIMESTAMPTZ '2026-01-03T15:32:35.28113Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentConfirmed', 'appointment_confirmed', 'Cita Confirmada', 'Experto confirmó', TRUE, 3, TIMESTAMPTZ '2026-01-03T15:32:35.28113Z', TIMESTAMPTZ '2026-01-03T15:32:35.28113Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentRejected', 'appointment_rejected', 'Cita Rechazada', 'Experto rechazó', TRUE, 4, TIMESTAMPTZ '2026-01-03T15:32:35.28113Z', TIMESTAMPTZ '2026-01-03T15:32:35.28113Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentCancelledByClient', 'appointment_cancelled_by_client', 'Cancelado por Cliente', 'Primera cancelación del cliente', TRUE, 5, TIMESTAMPTZ '2026-01-03T15:32:35.28113Z', TIMESTAMPTZ '2026-01-03T15:32:35.28113Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentCancelledByClientSecond', 'appointment_cancelled_by_client_second', 'Cancelado por Cliente (Segunda)', 'Segunda cancelación del cliente', TRUE, 6, TIMESTAMPTZ '2026-01-03T15:32:35.281131Z', TIMESTAMPTZ '2026-01-03T15:32:35.281131Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentCancelledByExpert', 'appointment_cancelled_by_expert', 'Cancelado por Experto', 'Experto cancela voluntariamente', TRUE, 7, TIMESTAMPTZ '2026-01-03T15:32:35.281131Z', TIMESTAMPTZ '2026-01-03T15:32:35.281132Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentCancelledByNoResponse', 'appointment_cancelled_by_no_response', 'Cancelado por Falta de Respuesta', 'Cliente no propuso en tiempo', TRUE, 8, TIMESTAMPTZ '2026-01-03T15:32:35.281132Z', TIMESTAMPTZ '2026-01-03T15:32:35.281132Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentCancelledByExpertRejection', 'appointment_cancelled_by_expert_rejection', 'Cancelado por Rechazo del Experto', 'Experto rechazó 2 veces', TRUE, 9, TIMESTAMPTZ '2026-01-03T15:32:35.281132Z', TIMESTAMPTZ '2026-01-03T15:32:35.281132Z');
    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
    VALUES ('AppointmentStatus', 'AppointmentCompleted', 'appointment_completed', 'Cita Completada', 'Cita realizada exitosamente', TRUE, 10, TIMESTAMPTZ '2026-01-03T15:32:35.281132Z', TIMESTAMPTZ '2026-01-03T15:32:35.281133Z');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN

                    INSERT INTO "StatusMappings" ("SourceStatusId", "TargetStatusId", "IsActive", "CreatedAt")
                    SELECT 
                        s1."Id" as SourceStatusId,
                        s2."Id" as TargetStatusId,
                        true as IsActive,
                        CURRENT_TIMESTAMP as CreatedAt
                    FROM "SystemStatuses" s1
                    CROSS JOIN "SystemStatuses" s2
                    WHERE s1."StatusValue" = 'appointment_completed' 
                    AND s2."StatusValue" = 'awaiting_client_decision'
                    
                    UNION ALL
                    
                    SELECT 
                        s1."Id" as SourceStatusId,
                        s2."Id" as TargetStatusId,
                        true as IsActive,
                        CURRENT_TIMESTAMP as CreatedAt
                    FROM "SystemStatuses" s1
                    CROSS JOIN "SystemStatuses" s2
                    WHERE s1."StatusValue" IN ('appointment_cancelled_by_client', 'appointment_cancelled_by_client_second', 
                                               'appointment_cancelled_by_expert', 'appointment_cancelled_by_no_response', 
                                               'appointment_cancelled_by_expert_rejection')
                    AND s2."StatusValue" = 'cancelled'
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN

                    INSERT INTO "StatusConfigurations" ("StatusId", "CategoryId", "ServiceTypeCategoryId", 
                                                       "ClientPercentage", "ExpertPercentage", "PlatformPercentage", 
                                                       "IsActive", "CreatedAt", "UpdatedAt")
                    SELECT 
                        ss."Id" as StatusId,
                        cstc."CategoryId",
                        cstc."ServiceTypeCategoryId",
                        cstc."ClientPercentage",
                        cstc."ExpertPercentage",
                        cstc."PlatformPercentage",
                        cstc."IsActive",
                        cstc."CreatedAt",
                        cstc."UpdatedAt"
                    FROM "CategoryServiceTypeConfigs" cstc
                    JOIN "SystemStatuses" ss ON ss."StatusValue" = cstc."Status"
                    WHERE cstc."Status" IN ('completed', 'dispute-resolved-client', 'dispute-resolved-expert', 'cancelled')
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    PERFORM setval(
        pg_get_serial_sequence('"SystemStatuses"', 'Id'),
        GREATEST(
            (SELECT MAX("Id") FROM "SystemStatuses") + 1,
            nextval(pg_get_serial_sequence('"SystemStatuses"', 'Id'))),
        false);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928091555_CreateCentralizedStatusSystem') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928091555_CreateCentralizedStatusSystem', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN
    ALTER TABLE "Appointments" ADD "StatusId" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN
    CREATE INDEX "IX_Appointments_StatusId" ON "Appointments" ("StatusId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN

                    UPDATE "Appointments" 
                    SET "StatusId" = (
                        SELECT ss."Id" 
                        FROM "SystemStatuses" ss 
                        WHERE ss."StatusType" = 'AppointmentStatus' 
                        AND ss."StatusValue" = "Appointments"."Status"
                    )
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN
    ALTER TABLE "Appointments" ALTER COLUMN "StatusId" SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN
    ALTER TABLE "Appointments" ADD CONSTRAINT "FK_Appointments_SystemStatuses_StatusId" FOREIGN KEY ("StatusId") REFERENCES "SystemStatuses" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN
    ALTER TABLE "Appointments" DROP COLUMN "Status";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092439_OptimizeAppointmentsTable') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928092439_OptimizeAppointmentsTable', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092539_RemoveRedundantConfigTables') THEN
    DROP TABLE "AppointmentStatusConfigs";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092539_RemoveRedundantConfigTables') THEN
    DROP TABLE "ServiceTypeCategoryConfigs";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092539_RemoveRedundantConfigTables') THEN
    DROP TABLE "CategoryServiceTypeConfigs";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928092539_RemoveRedundantConfigTables') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928092539_RemoveRedundantConfigTables', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus') THEN
    CREATE TABLE "DeliverableTypes" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "Name" character varying(50) NOT NULL,
        "DisplayName" character varying(100) NOT NULL,
        "Description" character varying(500) NOT NULL,
        "IsRequired" boolean NOT NULL DEFAULT FALSE,
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "SortOrder" integer NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_DeliverableTypes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus') THEN
    CREATE TABLE "SearchHireDeliverableTypes" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "SearchHireId" integer NOT NULL,
        "DeliverableTypeId" integer NOT NULL,
        "IsSelected" boolean NOT NULL DEFAULT FALSE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_SearchHireDeliverableTypes" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SearchHireDeliverableTypes_DeliverableTypes_DeliverableType~" FOREIGN KEY ("DeliverableTypeId") REFERENCES "DeliverableTypes" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SearchHireDeliverableTypes_SearchHires_SearchHireId" FOREIGN KEY ("SearchHireId") REFERENCES "SearchHires" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus') THEN
    CREATE UNIQUE INDEX "IX_DeliverableTypes_Name" ON "DeliverableTypes" ("Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus') THEN
    CREATE INDEX "IX_SearchHireDeliverableTypes_DeliverableTypeId" ON "SearchHireDeliverableTypes" ("DeliverableTypeId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus') THEN
    CREATE UNIQUE INDEX "IX_SearchHireDeliverableTypes_SearchHireId_DeliverableTypeId" ON "SearchHireDeliverableTypes" ("SearchHireId", "DeliverableTypeId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928174443_AddDeliverableTypesAndAppointmentAwaitingReportStatus', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928182057_AddSearchServiceDeliverableTypes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928182057_AddSearchServiceDeliverableTypes', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928191752_InsertDeliverableTypesData') THEN
    ALTER TABLE "DeliverableTypes" ALTER COLUMN "Description" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928191752_InsertDeliverableTypesData') THEN
    CREATE TABLE "SearchServiceDeliverableTypes" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "SearchServiceId" integer NOT NULL,
        "DeliverableTypeId" integer NOT NULL,
        "IsSelected" boolean NOT NULL DEFAULT FALSE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_SearchServiceDeliverableTypes" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SearchServiceDeliverableTypes_DeliverableTypes_DeliverableT~" FOREIGN KEY ("DeliverableTypeId") REFERENCES "DeliverableTypes" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SearchServiceDeliverableTypes_SearchServices_SearchServiceId" FOREIGN KEY ("SearchServiceId") REFERENCES "SearchServices" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928191752_InsertDeliverableTypesData') THEN
    CREATE INDEX "IX_SearchServiceDeliverableTypes_DeliverableTypeId" ON "SearchServiceDeliverableTypes" ("DeliverableTypeId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928191752_InsertDeliverableTypesData') THEN
    CREATE UNIQUE INDEX "IX_SearchServiceDeliverableTypes_SearchServiceId_DeliverableTy~" ON "SearchServiceDeliverableTypes" ("SearchServiceId", "DeliverableTypeId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928191752_InsertDeliverableTypesData') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928191752_InsertDeliverableTypesData', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928195229_InsertDeliverableTypesDataFinal') THEN
    DROP TABLE "SearchHireDeliverableTypes";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928195229_InsertDeliverableTypesDataFinal') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928195229_InsertDeliverableTypesDataFinal', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250930092400_UpdateAppointmentStatusFlow') THEN

                    DELETE FROM "StatusMappings" 
                    WHERE "SourceStatusId" IN (
                        SELECT "Id" FROM "SystemStatuses" 
                        WHERE "StatusType" = 'AppointmentStatus' 
                        AND "StatusValue" = 'appointment_completed'
                    )
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250930092400_UpdateAppointmentStatusFlow') THEN

                    DELETE FROM "SystemStatuses" 
                    WHERE "StatusType" = 'AppointmentStatus' 
                    AND "StatusValue" = 'appointment_completed'
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250930092400_UpdateAppointmentStatusFlow') THEN

                    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
                    SELECT 'AppointmentStatus', 'AppointmentAwaitingReport', 'appointment_awaiting_report', 'Esperando Reporte', 'Esperando reporte/archivos del experto (3h después de la cita)', true, 10, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "SystemStatuses" 
                        WHERE "StatusType" = 'AppointmentStatus' 
                        AND "StatusValue" = 'appointment_awaiting_report'
                    )
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250930092400_UpdateAppointmentStatusFlow') THEN

                    INSERT INTO "StatusMappings" ("SourceStatusId", "TargetStatusId", "IsActive", "CreatedAt")
                    SELECT 
                        s1."Id" as SourceStatusId,
                        s2."Id" as TargetStatusId,
                        true as IsActive,
                        CURRENT_TIMESTAMP as CreatedAt
                    FROM "SystemStatuses" s1
                    CROSS JOIN "SystemStatuses" s2
                    WHERE s1."StatusValue" = 'appointment_awaiting_report' 
                    AND s2."StatusValue" = 'awaiting_client_decision'
                    AND NOT EXISTS (
                        SELECT 1 FROM "StatusMappings" sm
                        WHERE sm."SourceStatusId" = s1."Id"
                        AND sm."TargetStatusId" = s2."Id"
                    )
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250930092400_UpdateAppointmentStatusFlow') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250930092400_UpdateAppointmentStatusFlow', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251001112052_AddExpertReportTimeoutStatus') THEN

                    INSERT INTO "SystemStatuses" ("StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt")
                    SELECT 'AppointmentStatus', 'AppointmentCancelledByNoReport', 'appointment_cancelled_by_no_report', 'Cancelada por No Reporte', 'Cita cancelada porque el experto no envió reporte en 24h', true, 15, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "SystemStatuses" 
                        WHERE "StatusType" = 'AppointmentStatus' 
                        AND "StatusValue" = 'appointment_cancelled_by_no_report'
                    )
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251001112052_AddExpertReportTimeoutStatus') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251001112052_AddExpertReportTimeoutStatus', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251018153126_AddIsFinalizationStatusToSystemStatuses') THEN
    ALTER TABLE "SystemStatuses" ADD "IsFinalizationStatus" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251018153126_AddIsFinalizationStatusToSystemStatuses') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251018153126_AddIsFinalizationStatusToSystemStatuses', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    CREATE TABLE "LogTypes" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "Name" character varying(100) NOT NULL,
        "Description" character varying(500),
        "Category" character varying(50) NOT NULL,
        "Severity" character varying(20) NOT NULL,
        "RequiresAdminNotification" boolean NOT NULL,
        "RequiresEmailAlert" boolean NOT NULL,
        "RequiresSmsAlert" boolean NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        CONSTRAINT "PK_LogTypes" PRIMARY KEY ("Id"),
        CONSTRAINT "AK_LogTypes_Name" UNIQUE ("Name")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    ALTER TABLE "Logs" ADD "AdditionalData" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    ALTER TABLE "Logs" ADD "LogTypeId" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    ALTER TABLE "Logs" ADD "RelatedEntityId" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    ALTER TABLE "Logs" ADD "RelatedEntityType" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    CREATE INDEX "IX_Logs_LogTypeId" ON "Logs" ("LogTypeId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    ALTER TABLE "Logs" ADD CONSTRAINT "FK_Logs_LogTypes_LogTypeId" FOREIGN KEY ("LogTypeId") REFERENCES "LogTypes" ("Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN

                    INSERT INTO "LogTypes" ("Name", "Description", "Category", "Severity", "RequiresAdminNotification", "RequiresEmailAlert", "RequiresSmsAlert", "IsActive", "CreatedAt")
                    VALUES 
                    -- Critical Log Types
                    ('TRANSFER_FAILED', 'Transfer to expert failed but service completed', 'Critical', 'Critical', true, true, false, true, NOW()),
                    ('REFUND_FAILED', 'Automatic refund failed after payment', 'Critical', 'Critical', true, true, false, true, NOW()),
                    ('PAYMENT_PROCESSING_ERROR', 'Error processing payment in Stripe', 'Critical', 'Critical', true, true, false, true, NOW()),
                    ('STRIPE_WEBHOOK_ERROR', 'Error processing Stripe webhook', 'Critical', 'Critical', true, false, false, true, NOW()),

                    -- Error Log Types
                    ('SEARCH_CREATION_ERROR', 'Error creating search after payment', 'Error', 'High', true, false, false, true, NOW()),
                    ('EXPERT_ACCOUNT_VERIFICATION_FAILED', 'Expert account verification failed', 'Error', 'High', false, false, false, true, NOW()),
                    ('DATABASE_CONNECTION_ERROR', 'Database connection error', 'Error', 'High', true, false, false, true, NOW()),
                    ('EXTERNAL_API_ERROR', 'Error calling external API', 'Error', 'Medium', false, false, false, true, NOW()),

                    -- Warning Log Types
                    ('EXPERT_ACCOUNT_PENDING', 'Expert account pending verification', 'Warning', 'Medium', false, false, false, true, NOW()),
                    ('PAYMENT_RETRY_ATTEMPT', 'Payment retry attempt', 'Warning', 'Medium', false, false, false, true, NOW()),
                    ('USER_ACTION_LIMIT_EXCEEDED', 'User exceeded action limits', 'Warning', 'Low', false, false, false, true, NOW()),

                    -- Info Log Types
                    ('SERVICE_COMPLETED', 'Service completed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('REFUND_PROCESSED', 'Refund processed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('PAYMENT_SUCCESSFUL', 'Payment processed successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('USER_LOGIN', 'User logged in', 'Info', 'Low', false, false, false, true, NOW()),
                    ('SEARCH_CREATED', 'Search created successfully', 'Info', 'Low', false, false, false, true, NOW()),
                    ('EXPERT_ACCOUNT_VERIFIED', 'Expert account verified', 'Info', 'Low', false, false, false, true, NOW())
                    ON CONFLICT ("Name") DO NOTHING;
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019224616_AddLogTypeTableOnly') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251019224616_AddLogTypeTableOnly', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    ALTER TABLE "Logs" DROP CONSTRAINT "FK_Logs_LogTypes_LogTypeId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    CREATE TABLE "Severities" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "Name" character varying(20) NOT NULL,
        "Description" character varying(100),
        "SortOrder" integer NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        CONSTRAINT "PK_Severities" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    CREATE UNIQUE INDEX "IX_Severities_Name" ON "Severities" ("Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN

                    INSERT INTO "Severities" ("Name", "Description", "SortOrder", "IsActive", "CreatedAt")
                    VALUES
                    ('Critical', 'Critical severity level', 1, true, NOW()),
                    ('High', 'High severity level', 2, true, NOW()),
                    ('Medium', 'Medium severity level', 3, true, NOW()),
                    ('Low', 'Low severity level', 4, true, NOW())
                    ON CONFLICT ("Name") DO NOTHING;
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    ALTER TABLE "LogTypes" ADD "SeverityId" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN

                    UPDATE "LogTypes" 
                    SET "SeverityId" = (
                        CASE 
                            WHEN "Severity" = 'Critical' THEN (SELECT "Id" FROM "Severities" WHERE "Name" = 'Critical')
                            WHEN "Severity" = 'High' THEN (SELECT "Id" FROM "Severities" WHERE "Name" = 'High')
                            WHEN "Severity" = 'Medium' THEN (SELECT "Id" FROM "Severities" WHERE "Name" = 'Medium')
                            WHEN "Severity" = 'Low' THEN (SELECT "Id" FROM "Severities" WHERE "Name" = 'Low')
                            ELSE (SELECT "Id" FROM "Severities" WHERE "Name" = 'Low')
                        END
                    )
                    WHERE "SeverityId" IS NULL;
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    ALTER TABLE "LogTypes" ALTER COLUMN "SeverityId" SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    ALTER TABLE "LogTypes" DROP COLUMN "Severity";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    CREATE INDEX "IX_LogTypes_SeverityId" ON "LogTypes" ("SeverityId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    ALTER TABLE "Logs" ADD CONSTRAINT "FK_Logs_LogTypes_LogTypeId" FOREIGN KEY ("LogTypeId") REFERENCES "LogTypes" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    ALTER TABLE "LogTypes" ADD CONSTRAINT "FK_LogTypes_Severities_SeverityId" FOREIGN KEY ("SeverityId") REFERENCES "Severities" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019230221_AddSeverityTableAndUpdateLogType') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251019230221_AddSeverityTableAndUpdateLogType', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    ALTER TABLE "LogTypes" DROP CONSTRAINT "FK_LogTypes_Severities_SeverityId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    ALTER TABLE "LogTypes" DROP COLUMN "Category";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    ALTER TABLE "LogTypes" ALTER COLUMN "SeverityId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    ALTER TABLE "LogTypes" ALTER COLUMN "Name" TYPE character varying(50);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    ALTER TABLE "LogTypes" ALTER COLUMN "Description" TYPE character varying(200);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    ALTER TABLE "LogTypes" ADD CONSTRAINT "FK_LogTypes_Severities_SeverityId" FOREIGN KEY ("SeverityId") REFERENCES "Severities" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019231824_SimplifyLogTypes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251019231824_SimplifyLogTypes', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019234253_RemoveLogLevelColumn') THEN
    ALTER TABLE "Logs" DROP COLUMN "LogLevel";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251019234253_RemoveLogLevelColumn') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251019234253_RemoveLogLevelColumn', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251021103652_AddSeparateCancellationCounters') THEN
    ALTER TABLE "Appointments" ADD "ClientCancellationCount" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251021103652_AddSeparateCancellationCounters') THEN
    ALTER TABLE "Appointments" ADD "ExpertCancellationCount" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251021103652_AddSeparateCancellationCounters') THEN
    ALTER TABLE "Appointments" ADD "LastClientCancellationAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251021103652_AddSeparateCancellationCounters') THEN
    ALTER TABLE "Appointments" ADD "LastExpertCancellationAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251021103652_AddSeparateCancellationCounters') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251021103652_AddSeparateCancellationCounters', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251021103829_RemoveGlobalCancellationCount') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251021103829_RemoveGlobalCancellationCount', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251028122232_AddColorToSystemStatuses') THEN
    ALTER TABLE "Appointments" DROP COLUMN "CancellationCount";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251028122232_AddColorToSystemStatuses') THEN
    ALTER TABLE "SystemStatuses" ADD "Color" character varying(20);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251028122232_AddColorToSystemStatuses') THEN
    ALTER TABLE "Appointments" ALTER COLUMN "ExpertCancellationCount" SET DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251028122232_AddColorToSystemStatuses') THEN
    ALTER TABLE "Appointments" ALTER COLUMN "ClientCancellationCount" SET DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251028122232_AddColorToSystemStatuses') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251028122232_AddColorToSystemStatuses', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251101025058_AddExpertAvailabilitySystem') THEN
    ALTER TABLE "SearchHires" ADD "ExpertAvailabilityId" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251101025058_AddExpertAvailabilitySystem') THEN
    CREATE TABLE "ExpertAvailabilities" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "ExpertId" integer NOT NULL,
        "DaysOfWeek" text NOT NULL,
        "StartTime" interval NOT NULL,
        "EndTime" interval NOT NULL,
        "EffectiveFrom" timestamp with time zone NOT NULL,
        "EffectiveTo" timestamp with time zone,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ExpertAvailabilities" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_ExpertAvailabilities_ExpertProfiles_ExpertId" FOREIGN KEY ("ExpertId") REFERENCES "ExpertProfiles" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251101025058_AddExpertAvailabilitySystem') THEN
    CREATE INDEX "IX_SearchHires_ExpertAvailabilityId" ON "SearchHires" ("ExpertAvailabilityId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251101025058_AddExpertAvailabilitySystem') THEN
    CREATE INDEX "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo" ON "ExpertAvailabilities" ("ExpertId", "IsActive", "EffectiveTo") WHERE "EffectiveTo" IS NULL AND "IsActive" = true;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251101025058_AddExpertAvailabilitySystem') THEN
    ALTER TABLE "SearchHires" ADD CONSTRAINT "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId" FOREIGN KEY ("ExpertAvailabilityId") REFERENCES "ExpertAvailabilities" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251101025058_AddExpertAvailabilitySystem') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251101025058_AddExpertAvailabilitySystem', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104115022_RemoveUnusedAppointmentFields') THEN
    ALTER TABLE "Appointments" DROP COLUMN "CompletedAt";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104115022_RemoveUnusedAppointmentFields') THEN
    ALTER TABLE "Appointments" DROP COLUMN "CompletedBy";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104115022_RemoveUnusedAppointmentFields') THEN
    ALTER TABLE "Appointments" DROP COLUMN "DisputeReason";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104115022_RemoveUnusedAppointmentFields') THEN
    ALTER TABLE "Appointments" DROP COLUMN "IsLocked";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104115022_RemoveUnusedAppointmentFields') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251104115022_RemoveUnusedAppointmentFields', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    ALTER TABLE "SearchHires" DROP CONSTRAINT "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    DROP INDEX "IX_ExpertAvailabilities_ExpertId_IsActive_EffectiveTo";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    ALTER TABLE "ExpertProfiles" ADD "StripeFutureDueAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    ALTER TABLE "ExpertProfiles" ADD "StripeFutureRequirements" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    CREATE INDEX "IX_ExpertAvailabilities_ExpertId" ON "ExpertAvailabilities" ("ExpertId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    ALTER TABLE "SearchHires" ADD CONSTRAINT "FK_SearchHires_ExpertAvailabilities_ExpertAvailabilityId" FOREIGN KEY ("ExpertAvailabilityId") REFERENCES "ExpertAvailabilities" ("Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251104232306_AddStripeFutureRequirementsToExpertProfile') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251104232306_AddStripeFutureRequirementsToExpertProfile', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Conversations" DROP CONSTRAINT "FK_Conversations_Users_ClientId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Conversations" DROP CONSTRAINT "FK_Conversations_Users_ExpertId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "FinancialTransactions" DROP CONSTRAINT "FK_FinancialTransactions_Users_UserId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Messages" DROP CONSTRAINT "FK_Messages_Users_SenderId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Reviews" DROP CONSTRAINT "FK_Reviews_Users_ReviewerId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "SearchHires" DROP CONSTRAINT "FK_SearchHires_Users_ClientId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Users" ADD "DeletedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Users" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "SearchHires" ALTER COLUMN "ClientId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Reviews" ALTER COLUMN "ReviewerId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Messages" ALTER COLUMN "SenderId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "FinancialTransactions" ALTER COLUMN "UserId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Conversations" ALTER COLUMN "ExpertId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Conversations" ALTER COLUMN "ClientId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Conversations" ADD CONSTRAINT "FK_Conversations_Users_ClientId" FOREIGN KEY ("ClientId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Conversations" ADD CONSTRAINT "FK_Conversations_Users_ExpertId" FOREIGN KEY ("ExpertId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "FinancialTransactions" ADD CONSTRAINT "FK_FinancialTransactions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Messages" ADD CONSTRAINT "FK_Messages_Users_SenderId" FOREIGN KEY ("SenderId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "Reviews" ADD CONSTRAINT "FK_Reviews_Users_ReviewerId" FOREIGN KEY ("ReviewerId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    ALTER TABLE "SearchHires" ADD CONSTRAINT "FK_SearchHires_Users_ClientId" FOREIGN KEY ("ClientId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251114224416_MakeUserFieldsNullableForAccountDeletionAnonymization', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115203709_MakeExpertIdNullableInSearchHires') THEN
    ALTER TABLE "SearchHires" DROP CONSTRAINT "FK_SearchHires_Users_ExpertId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115203709_MakeExpertIdNullableInSearchHires') THEN
    ALTER TABLE "SearchHires" ALTER COLUMN "ExpertId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115203709_MakeExpertIdNullableInSearchHires') THEN
    ALTER TABLE "SearchHires" ADD CONSTRAINT "FK_SearchHires_Users_ExpertId" FOREIGN KEY ("ExpertId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115203709_MakeExpertIdNullableInSearchHires') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251115203709_MakeExpertIdNullableInSearchHires', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115204742_MakeExpertProfileIdNullableInSearchServices') THEN
    ALTER TABLE "SearchServices" DROP CONSTRAINT "FK_SearchServices_ExpertProfiles_ExpertProfileId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115204742_MakeExpertProfileIdNullableInSearchServices') THEN
    ALTER TABLE "SearchServices" ALTER COLUMN "ExpertProfileId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115204742_MakeExpertProfileIdNullableInSearchServices') THEN
    ALTER TABLE "SearchServices" ADD CONSTRAINT "FK_SearchServices_ExpertProfiles_ExpertProfileId" FOREIGN KEY ("ExpertProfileId") REFERENCES "ExpertProfiles" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115204742_MakeExpertProfileIdNullableInSearchServices') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251115204742_MakeExpertProfileIdNullableInSearchServices', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115220338_MakeSearchHireIdNullableInReviews') THEN
    ALTER TABLE "Reviews" DROP CONSTRAINT "FK_Reviews_SearchHires_SearchHireId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115220338_MakeSearchHireIdNullableInReviews') THEN
    ALTER TABLE "Reviews" ALTER COLUMN "SearchHireId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115220338_MakeSearchHireIdNullableInReviews') THEN
    ALTER TABLE "Reviews" ADD CONSTRAINT "FK_Reviews_SearchHires_SearchHireId" FOREIGN KEY ("SearchHireId") REFERENCES "SearchHires" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115220338_MakeSearchHireIdNullableInReviews') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251115220338_MakeSearchHireIdNullableInReviews', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115222519_MakeSearchIdNullableInSearchHires') THEN
    ALTER TABLE "SearchHires" DROP CONSTRAINT "FK_SearchHires_Searches_SearchId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115222519_MakeSearchIdNullableInSearchHires') THEN
    ALTER TABLE "SearchHires" ALTER COLUMN "SearchId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115222519_MakeSearchIdNullableInSearchHires') THEN
    ALTER TABLE "SearchHires" ADD CONSTRAINT "FK_SearchHires_Searches_SearchId" FOREIGN KEY ("SearchId") REFERENCES "Searches" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115222519_MakeSearchIdNullableInSearchHires') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251115222519_MakeSearchIdNullableInSearchHires', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115224511_FixReviewExpertIdNullableAndNotificationFK') THEN
    ALTER TABLE "SearchHires" DROP CONSTRAINT "FK_SearchHires_Searches_SearchId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115224511_FixReviewExpertIdNullableAndNotificationFK') THEN
    ALTER TABLE "SearchHires" ALTER COLUMN "SearchId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115224511_FixReviewExpertIdNullableAndNotificationFK') THEN
    ALTER TABLE "SearchHires" ADD CONSTRAINT "FK_SearchHires_Searches_SearchId" FOREIGN KEY ("SearchId") REFERENCES "Searches" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251115224511_FixReviewExpertIdNullableAndNotificationFK') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251115224511_FixReviewExpertIdNullableAndNotificationFK', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    ALTER TABLE "Notifications" DROP CONSTRAINT "FK_Notifications_Users_UserId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    ALTER TABLE "Reviews" DROP CONSTRAINT "FK_Reviews_Users_ExpertId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    ALTER TABLE "Reviews" ALTER COLUMN "ExpertId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    CREATE TABLE "RefreshTokens" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "UserId" integer NOT NULL,
        "Token" character varying(500) NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "IsRevoked" boolean NOT NULL,
        "RevokedAt" timestamp with time zone,
        "RevokedByIp" character varying(100),
        "CreatedByIp" character varying(100) NOT NULL,
        "ReplacedByToken" character varying(200),
        "DeviceInfo" character varying(500),
        CONSTRAINT "PK_RefreshTokens" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_RefreshTokens_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    CREATE INDEX "IX_RefreshTokens_ExpiresAt" ON "RefreshTokens" ("ExpiresAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    CREATE UNIQUE INDEX "IX_RefreshTokens_Token" ON "RefreshTokens" ("Token");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    CREATE INDEX "IX_RefreshTokens_UserId" ON "RefreshTokens" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    CREATE INDEX "IX_RefreshTokens_UserId_IsRevoked_ExpiresAt" ON "RefreshTokens" ("UserId", "IsRevoked", "ExpiresAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    ALTER TABLE "Notifications" ADD CONSTRAINT "FK_Notifications_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    ALTER TABLE "Reviews" ADD CONSTRAINT "FK_Reviews_Users_ExpertId" FOREIGN KEY ("ExpertId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116022251_AddRefreshTokens') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251116022251_AddRefreshTokens', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116023208_AddUserMfaSettings') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251116023208_AddUserMfaSettings', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116033301_CreateUserMfaSettingsTable') THEN
    CREATE TABLE "UserMfaSettings" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "UserId" integer NOT NULL,
        "IsEnabled" boolean NOT NULL,
        "TotpSecret" character varying(500) NOT NULL,
        "RecoveryCodesEncrypted" character varying(2000),
        "RecoveryCodesUsed" integer NOT NULL,
        "EnabledAt" timestamp with time zone,
        "LastVerifiedAt" timestamp with time zone,
        "FailedAttempts" integer NOT NULL,
        "LockedUntil" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        CONSTRAINT "PK_UserMfaSettings" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_UserMfaSettings_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116033301_CreateUserMfaSettingsTable') THEN
    CREATE UNIQUE INDEX "IX_UserMfaSettings_UserId" ON "UserMfaSettings" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251116033301_CreateUserMfaSettingsTable') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251116033301_CreateUserMfaSettingsTable', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251203134032_AddStripeModeColumnsToSystemSettings') THEN
    ALTER TABLE "SystemSettings" ADD "StripeMode" character varying(20) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251203134032_AddStripeModeColumnsToSystemSettings') THEN
    ALTER TABLE "SystemSettings" ADD "StripeModeChangedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251203134032_AddStripeModeColumnsToSystemSettings') THEN
    ALTER TABLE "SystemSettings" ADD "StripeModeChangedByUserId" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251203134032_AddStripeModeColumnsToSystemSettings') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251203134032_AddStripeModeColumnsToSystemSettings', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251212124057_AddBaseAmountAndTaxAmountToSearchHires') THEN
    ALTER TABLE "SearchHires" ADD "BaseAmount" numeric;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251212124057_AddBaseAmountAndTaxAmountToSearchHires') THEN
    ALTER TABLE "SearchHires" ADD "TaxAmount" numeric;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20251212124057_AddBaseAmountAndTaxAmountToSearchHires') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20251212124057_AddBaseAmountAndTaxAmountToSearchHires', '10.0.0');
    END IF;
END $EF$;
COMMIT;
