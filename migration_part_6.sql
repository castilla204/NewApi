

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
