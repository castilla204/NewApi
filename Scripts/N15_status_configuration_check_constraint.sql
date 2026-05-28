-- N15 FIX: CHECK constraint que garantiza que ClientPercentage + ExpertPercentage + PlatformPercentage = 100
-- (con tolerancia 0.01, alineada con RefundService.cs:211).
--
-- Contexto: StatusConfiguration.IsValid existe en el modelo C# como propiedad calculada
-- (ClientPercentage + ExpertPercentage + PlatformPercentage == 100), pero NUNCA se invoca
-- en lógica de negocio. La única validación runtime es en RefundService (con tolerancia 0.01).
-- Si alguien hace un UPDATE SQL directo a StatusConfigurations bypaseando la API, la suma
-- podría corromperse silenciosamente y nadie se entera hasta que un hire intenta procesar
-- dinero con esa config rota.
--
-- Este CHECK constraint es defensa profunda: ningún UPDATE/INSERT puede dejar la suma fuera de 100.
--
-- IDEMPOTENTE: usa DO block para chequear existencia antes de crear.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_StatusConfigurations_PercentageSumTo100'
    ) THEN
        ALTER TABLE "StatusConfigurations"
        ADD CONSTRAINT "CK_StatusConfigurations_PercentageSumTo100"
        CHECK (
            ABS("ClientPercentage" + "ExpertPercentage" + "PlatformPercentage" - 100) < 0.01
        );
        RAISE NOTICE 'N15: CHECK constraint CK_StatusConfigurations_PercentageSumTo100 created.';
    ELSE
        RAISE NOTICE 'N15: CHECK constraint already exists, skipping.';
    END IF;
END $$;

-- Verificación:
-- SELECT conname FROM pg_constraint WHERE conname = 'CK_StatusConfigurations_PercentageSumTo100';
