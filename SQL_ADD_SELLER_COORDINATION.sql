-- Coordinación con el vendedor (modo "seller" del checkout).
-- Añade a SearchHires el modo de coordinación y los datos de contacto del vendedor.
-- Todo nullable: los hires existentes (modo "yo me encargo") no se ven afectados.
--
-- Recomendado: generar también la migración EF para mantener el snapshot del modelo:
--   dotnet ef migrations add AddSellerCoordinationToSearchHire
--   dotnet ef database update
-- (o ejecutar este SQL directamente contra la BD si prefieres el flujo manual).

ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "CoordinationMode"  text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "SellerPhone"       text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "SellerEmail"       text NULL;
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "SellerListingUrl"  text NULL;
-- Token secreto del "magic link" del vendedor (modo seller). Sin login.
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "SellerBookingToken" text NULL;
-- Máximo de días a futuro que el vendedor puede elegir (lo fija el cliente). Null = default.
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "SellerBookingMaxDays" integer NULL;
-- Fecha límite para que el vendedor reserve; si pasa sin cita, reembolso 100% al comprador.
ALTER TABLE "SearchHires" ADD COLUMN IF NOT EXISTS "SellerBookingDeadline" timestamptz NULL;
