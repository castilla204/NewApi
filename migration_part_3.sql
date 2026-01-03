

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250925173009_AddAdminRoleAndUpdateExistingUser') THEN

                    UPDATE "Users" 
                    SET "Role" = 2 
                    WHERE "Email" = 'dcastillaa@gmail.com';
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250925173009_AddAdminRoleAndUpdateExistingUser') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250925173009_AddAdminRoleAndUpdateExistingUser', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250927104428_AddBalanceNonNegativeConstraint') THEN
    ALTER TABLE "Users" ADD CONSTRAINT "CK_Users_Balance_NonNegative" CHECK ("Balance" >= 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250927104428_AddBalanceNonNegativeConstraint') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250927104428_AddBalanceNonNegativeConstraint', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928081524_AddDisputeResolvedConfigurations') THEN
    ALTER TABLE "FinancialTransactions" ADD "IsRefunded" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928081524_AddDisputeResolvedConfigurations') THEN
    ALTER TABLE "FinancialTransactions" ADD "StripePaymentIntentId" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928081524_AddDisputeResolvedConfigurations') THEN
    ALTER TABLE "FinancialTransactions" ADD "StripeRefundId" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928081524_AddDisputeResolvedConfigurations') THEN
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.8948Z', TIMESTAMPTZ '2026-01-03T15:32:34.8948Z');
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.8948Z', TIMESTAMPTZ '2026-01-03T15:32:34.894801Z');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928081524_AddDisputeResolvedConfigurations') THEN
    PERFORM setval(
        pg_get_serial_sequence('"AppointmentStatusConfigs"', 'Id'),
        GREATEST(
            (SELECT MAX("Id") FROM "AppointmentStatusConfigs") + 1,
            nextval(pg_get_serial_sequence('"AppointmentStatusConfigs"', 'Id'))),
        false);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928081524_AddDisputeResolvedConfigurations') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928081524_AddDisputeResolvedConfigurations', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082329_AddSpecificCancellationConfigurations') THEN
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('cancelled_by_client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.94664Z', TIMESTAMPTZ '2026-01-03T15:32:34.946641Z');
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('cancelled_by_client_second', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.946641Z', TIMESTAMPTZ '2026-01-03T15:32:34.946641Z');
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('cancelled_by_expert', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.946641Z', TIMESTAMPTZ '2026-01-03T15:32:34.946641Z');
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('cancelled_by_no_response', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.946641Z', TIMESTAMPTZ '2026-01-03T15:32:34.946642Z');
    INSERT INTO "AppointmentStatusConfigs" ("Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES ('cancelled_by_expert_rejection', 98.0, 0.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:34.946642Z', TIMESTAMPTZ '2026-01-03T15:32:34.946642Z');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082329_AddSpecificCancellationConfigurations') THEN
    PERFORM setval(
        pg_get_serial_sequence('"AppointmentStatusConfigs"', 'Id'),
        GREATEST(
            (SELECT MAX("Id") FROM "AppointmentStatusConfigs") + 1,
            nextval(pg_get_serial_sequence('"AppointmentStatusConfigs"', 'Id'))),
        false);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082329_AddSpecificCancellationConfigurations') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928082329_AddSpecificCancellationConfigurations', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082825_RemoveSpecificCancellationConfigurations') THEN
    DELETE FROM "AppointmentStatusConfigs"
    WHERE "Status" = 'cancelled_by_client';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082825_RemoveSpecificCancellationConfigurations') THEN
    DELETE FROM "AppointmentStatusConfigs"
    WHERE "Status" = 'cancelled_by_client_second';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082825_RemoveSpecificCancellationConfigurations') THEN
    DELETE FROM "AppointmentStatusConfigs"
    WHERE "Status" = 'cancelled_by_expert';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082825_RemoveSpecificCancellationConfigurations') THEN
    DELETE FROM "AppointmentStatusConfigs"
    WHERE "Status" = 'cancelled_by_no_response';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082825_RemoveSpecificCancellationConfigurations') THEN
    DELETE FROM "AppointmentStatusConfigs"
    WHERE "Status" = 'cancelled_by_expert_rejection';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928082825_RemoveSpecificCancellationConfigurations') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928082825_RemoveSpecificCancellationConfigurations', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928083847_AddDisputeResolvedConfigurationsToCategoryServiceType') THEN
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 1, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057098Z', TIMESTAMPTZ '2026-01-03T15:32:35.057098Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 2, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057099Z', TIMESTAMPTZ '2026-01-03T15:32:35.057099Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 3, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057099Z', TIMESTAMPTZ '2026-01-03T15:32:35.057099Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 1, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057099Z', TIMESTAMPTZ '2026-01-03T15:32:35.057099Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 2, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057101Z', TIMESTAMPTZ '2026-01-03T15:32:35.057101Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 3, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057101Z', TIMESTAMPTZ '2026-01-03T15:32:35.057102Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 1, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057102Z', TIMESTAMPTZ '2026-01-03T15:32:35.057102Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 2, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057102Z', TIMESTAMPTZ '2026-01-03T15:32:35.057102Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 3, 'dispute-resolved-client', 90.0, 8.0, 2.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057103Z', TIMESTAMPTZ '2026-01-03T15:32:35.057103Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 1, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057106Z', TIMESTAMPTZ '2026-01-03T15:32:35.057107Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 2, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057107Z', TIMESTAMPTZ '2026-01-03T15:32:35.057108Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (1, 3, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057108Z', TIMESTAMPTZ '2026-01-03T15:32:35.057108Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 1, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057108Z', TIMESTAMPTZ '2026-01-03T15:32:35.057108Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 2, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057109Z', TIMESTAMPTZ '2026-01-03T15:32:35.057109Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (2, 3, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057109Z', TIMESTAMPTZ '2026-01-03T15:32:35.057109Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 1, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057109Z', TIMESTAMPTZ '2026-01-03T15:32:35.057109Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 2, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.05711Z', TIMESTAMPTZ '2026-01-03T15:32:35.05711Z');
    INSERT INTO "CategoryServiceTypeConfigs" ("CategoryId", "ServiceTypeCategoryId", "Status", "ClientPercentage", "ExpertPercentage", "PlatformPercentage", "IsActive", "CreatedAt", "UpdatedAt")
    VALUES (3, 3, 'dispute-resolved-expert', 0.0, 95.0, 5.0, TRUE, TIMESTAMPTZ '2026-01-03T15:32:35.057111Z', TIMESTAMPTZ '2026-01-03T15:32:35.057111Z');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928083847_AddDisputeResolvedConfigurationsToCategoryServiceType') THEN
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
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20250928083847_AddDisputeResolvedConfigurationsToCategoryServiceType') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20250928083847_AddDisputeResolvedConfigurationsToCategoryServiceType', '10.0.0');
    END IF;
END $EF$;
COMMIT;

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
