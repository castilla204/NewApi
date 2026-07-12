-- 🛡️ FIX [M1-INDEXES] (auditoría 2026-07-12): índices parciales para los magic links.
-- SellerBookingController y ExpertConfirmationController son [AllowAnonymous] y buscan el hire
-- por igualdad exacta del token (FirstOrDefaultAsync(h => h.SellerBookingToken == token)).
-- Sin índice, CADA petición anónima (contexto, window, summary, slots por día, confirm, decline,
-- approve, reject) hace un seq-scan completo de "SearchHires" — coste creciente con la tabla y
-- vector de amplificación de carga para tráfico basura con tokens aleatorios.
--
-- Parciales (WHERE ... IS NOT NULL) porque el token se anula al consumirse: solo una fracción
-- pequeña de hires tiene token vivo en un momento dado → índices diminutos y siempre calientes.
--
-- Aplicar con psql (mismo flujo manual que SQL_ADD_SELLER_COORDINATION.sql, por el drift de
-- migraciones EF). Idempotente: IF NOT EXISTS.

CREATE INDEX IF NOT EXISTS "IX_SearchHires_SellerBookingToken"
    ON "SearchHires" ("SellerBookingToken")
    WHERE "SellerBookingToken" IS NOT NULL;

CREATE INDEX IF NOT EXISTS "IX_SearchHires_ExpertConfirmationToken"
    ON "SearchHires" ("ExpertConfirmationToken")
    WHERE "ExpertConfirmationToken" IS NOT NULL;

-- 🛡️ REVIEW-DEDUP (verificado 2026-07-13): índice ÚNICO parcial anti-reseñas duplicadas. El código EF
-- lo declara (AppDbContext.cs:434-438) pero el propio comentario advierte que hay que aplicarlo A MANO en
-- prod por el drift de migraciones. El guard anti-duplicado de ReviewController (check FirstOrDefault + Add)
-- NO es atómico → sin este índice, dos POST simultáneos del mismo cliente cuelan 2 reseñas e inflan/hunden
-- el rating del experto (afecta a su ranking → ingresos). Idempotente. VERIFICAR con \d "Reviews" si ya existe.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Reviews_Reviewer_Expert_Hire"
    ON "Reviews" ("ReviewerId", "ExpertId", "SearchHireId")
    WHERE "SearchHireId" IS NOT NULL;
