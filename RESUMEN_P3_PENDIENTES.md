# P3 — Pendientes / DEFERRED

Resumen consolidado de todo lo que el worker P3 dejó parcial o pospuesto, con la decisión razonada en cada caso y el plan concreto para la siguiente iteración.

---

## P3-1 — Refund failure: estado intermedio `RefundPending`

Estado actual (versión mínima aplicada):

- `SearchHire.RefundFailedAt DateTime?` añadido al modelo y a la BD (`SQL_ADD_REFUND_FAILED_AT.sql`).
- `HangfireFailedJobNotificationFilter` marca `RefundFailedAt = UtcNow` cuando `RetryMoneyDistributionJobAsync` cae en `FailedState` (retries agotados).
- Job recurring diario `refund-failed-digest` (Cron 07:00 UTC) que envía email a admins con la lista de hires marcados en las últimas 24h.
- Se sigue dejando el hire en `Completed` / `DisputeResolvedClient`. Esto es operativamente "visible" pero NO bloquea al usuario.

Versión COMPLETA pendiente (DEFERRED):

1. Añadir `SearchHireStatus.RefundPending` al enum + fila en `SystemStatus`.
2. Añadir `SearchHire.PendingFinalStatusId int?` (apunta al estado al que debe ascender una vez Fase 3 confirme).
3. Reescribir Fase 2 de `RefundService.cs` (L514-617) para:
   - Marcar `StatusId = RefundPending` + `PendingFinalStatusId = <Completed | DisputeResolvedClient>` en lugar del estado final directo.
   - Encolar Fase 3 (job `RetryMoneyDistributionJobAsync`).
4. En `RetryMoneyDistributionJobAsync`, al éxito, ascender `StatusId = PendingFinalStatusId` y limpiar `PendingFinalStatusId = null`.
5. Filtros de consultas (mapa, listados de cliente/experto) deben tratar `RefundPending` como "finalizado pero con dinero pendiente" o "en proceso" según el contexto.
6. SQL idempotente que migra los hires actualmente `Completed` con `RefundFailedAt != null` a `RefundPending` si el operador lo decide.

Razón del DEFERRED: el rediseño toca >40 sitios (mapa, listados, AppointmentService, panel admin, frontend). La versión mínima ya cumple el objetivo de "no perder visibilidad del dinero atascado" sin riesgo en producción.

---

## P3-4 — Account deletion: warning de `pendingTransactionsCount` en `DeleteUserDataAsync:912`

Estado actual:

- Se añadió bloqueo HARD ANTES de Fase 1 en `DeleteAccountAsync` para:
  - Disputas `Status = "Pending"` del usuario (como reporter, client o expert) → HTTP-equivalente error "No se puede eliminar la cuenta con disputas pendientes".
  - PaymentIntents en deferred capture: `FinancialTransaction.TransactionType = ServicePayment` con `StripePaymentIntentId != null`, `!IsRefunded`, ligado a `SearchHire` en estado `pending`.

Lo que NO se hizo (DEFERRED):

- Convertir el warning de `DeleteUserDataAsync:912` (`pendingTransactionsCount > 0`) en error HARD. La consulta SQL actual usa `ExecuteSqlRawAsync(...)` con un `SELECT COUNT(*)`, cuyo retorno es "filas afectadas" (no el COUNT real), por lo que `pendingTransactionsCount > 0` ya es falso siempre y el warning nunca se dispara. Convertirlo a HARD directamente cambiaría su semántica si se arregla la query; preferimos dejar el bloqueo HARD equivalente arriba (con LINQ tipado y filtros claros) y corregir esa query como parte de una iteración posterior dedicada a `DeleteUserDataAsync`.
- Flujo de gracia de 30 días: explícitamente DEFERRED por el brief.

---

## P3-8 — `ConcurrencyRetryHelper.SaveChangesWithRetryAsync` en top 5 de `AppointmentService`

Métodos objetivo: `ProposeAppointmentAsync`, `ConfirmAppointmentAsync`, `RejectAppointmentAsync`, `CancelAppointmentAsync`, `SubmitExpertReportAsync` (el brief decía `SubmitReportAsync`; el método real es `SubmitExpertReportAsync`).

Decisión: **DEFERRED** la envoltura automática.

Razón:

- Cada uno de estos métodos abre transacción manual (`_context.Database.BeginTransactionAsync()`) y hace múltiples `SaveChangesAsync` consecutivos dentro de la misma transacción (timers, notificaciones, cambios de estado, AppointmentTimers, finalización Hangfire, etc.).
- `ConcurrencyRetryHelper.SaveChangesWithRetryAsync` hace `entry.ReloadAsync()` sobre el primer `DbUpdateConcurrencyException` y reintenta la acción. Dentro de una transacción manual ya activa, recargar y volver a guardar puede dejar la transacción en estado inconsistente (PostgreSQL aborta la TX cuando hay error y todos los siguientes statements fallan).
- Envolver solo el último `SaveChangesAsync` rompe la atomicidad: el resto del estado quedaría persistido sin reintento, lo que es peor que no reintentar.
- Riesgo de romper producción estimado > 30 %.

Plan para una iteración dedicada:

1. Refactorizar cada método para que tenga un único `SaveChangesAsync` final con todas las mutaciones acumuladas en el `ChangeTracker` (estilo "unit of work").
2. Mover los efectos secundarios externos (Hangfire `BackgroundJob.Enqueue`, broadcasts Supabase) DESPUÉS del `CommitAsync`.
3. Solo entonces envolver el `SaveChangesAsync` final con `SaveChangesWithRetryAsync` (el `ReloadAsync` interno será seguro porque solo hay un punto de fallo).

Mientras tanto: las conflictos de concurrencia siguen propagando `DbUpdateConcurrencyException` al endpoint y el cliente recibe 500 (debería ser 409, pero eso es otra iteración).

---

## P3-9 — Outbox completo para captura del PaymentIntent

Estado actual:

- P1-5 implementó la versión mínima: `SearchHire.CaptureStatus` ("Pending"/"Captured"/"Failed"/null) y el happy-path + compensación cuando el commit local falla tras la captura (refund inmediato del PI).
- No hay aún:
  - Tabla `OutboxCaptureMessage` con `(id, search_hire_id, payment_intent_id, attempts, last_error, next_attempt_at, state, created_at)`.
  - Servicio `OutboxCaptureService` con `Enqueue(...)` y `ProcessOnceAsync(...)` (idempotente por `IdempotencyKey = $"capture-{hireId}"`).
  - Job recurring `outbox-capture-watchdog` (cada 5 min) que toma los outbox `state = Pending` con `next_attempt_at <= now`, intenta capturar, marca `Captured` o reagenda con backoff exponencial.
  - Métrica/alerta para outbox `state = Failed` (> N reintentos).

Decisión: **DEFERRED**.

Razón:

- Diseño correcto requiere migración de tabla nueva + servicio + filtro Hangfire + UI mínimo para admins.
- La versión mínima ya cubre el happy path y la compensación crítica (no se queda dinero capturado en Stripe sin SearchHire local).
- El gap restante (commit local OK + post-commit falla en algún side-effect Hangfire) es muy estrecho y solo afectaría a no-encolar timers, no a pérdidas monetarias.

Plan:

1. SQL `SQL_CREATE_OUTBOX_CAPTURE.sql` con la tabla y `(state, next_attempt_at)` index.
2. Modelo `OutboxCaptureMessage` + `DbSet<OutboxCaptureMessage>` en `AppDbContext`.
3. `IOutboxCaptureService` con `Enqueue` y `ProcessOnceAsync`.
4. Reemplazar la captura inline de `HandlePendingHireCompleted` por `Enqueue` (write-only) y dejar la captura real al watchdog.
5. Filtro de transición de estados igual al `HangfireFailedJobNotificationFilter`: alerta a admin cuando un outbox cae en `Failed` definitivo.

---

## P3-10 — Migración `Amount decimal EUR → long céntimos`

Estado actual:

- Columna `FinancialTransaction.AmountCents bigint NOT NULL DEFAULT 0` añadida al modelo y SQL con backfill (`SQL_ADD_AMOUNTCENTS.sql`).
- `Amount` (decimal) sigue como propiedad principal de escritura. Se marcó solo en XML doc como DEPRECATED — el atributo `[Obsolete]` queda DEFERRED para evitar saturar el build con warnings hasta que se actualicen los escritores.

Lo que NO se hizo (DEFERRED — > 15 sitios):

Archivos / fragmentos identificados donde se escribe `Amount`:

- `Controllers/SubscriptionController.cs` — ~7 sitios (HireService, LoadMoney, LogCriticalRefundFailure, HandleChargeRefunded, etc.).
- `Services/RefundService.cs` — ~4 sitios (ProcessMoneyDistributionAsync, ReverseExpertTransferForChargebackAsync, registros de Payout y Refund).
- `Services/StripeReconciliationService.cs` — 2 sitios.
- `Controllers/FinancialTransactionController.cs` — 2 sitios.

Plan:

1. En cada `new FinancialTransaction { Amount = X, ... }` añadir `AmountCents = (long)Math.Round(X * 100m)`.
2. Cuando todos los escritores estén actualizados, activar `[Obsolete]` sobre `Amount` y empezar a migrar lectores (queries `Sum(Amount)`, etc.) hacia `AmountCents / 100m` o directamente `AmountCents`.
3. Cuando los lectores también estén migrados, planificar un `ALTER TABLE "FinancialTransactions" DROP COLUMN "Amount"` en `SQL_DROP_AMOUNT.sql` (no en este sprint).

---

## P3-6 — Drops de tablas legacy de suscripciones

- No se aplican drops en este sprint.
- TODO: `SQL_DROP_LEGACY_SUBSCRIPTIONS.sql` (no creado) cuando se confirme con BD que las tablas `Subscriptions*` ya no tienen filas vivas ni FKs colgantes.
- Rename `SubscriptionService → SearchHireTimeoutService`: **DEFERRED**. Solo el nombre del controller (`SubscriptionController`) rompe ya >40 referencias de routing/Stripe webhooks. Se hará junto con el rediseño de webhooks.

---

## Riesgos pendientes globales

- `DeleteUserDataAsync:912` sigue con la query `ExecuteSqlRawAsync(SELECT COUNT(*)...)` rota — el bloqueo HARD efectivo ya vive arriba con LINQ tipado, pero esa línea es engañosa.
- `[Obsolete]` sobre `FinancialTransaction.Amount` queda diferido para no romper signal-to-noise del build hasta que se complete la migración a `AmountCents`.
- Outbox parcial: gap muy estrecho de pérdida de side-effects post-captura sin pérdida monetaria.
- P3-8: los métodos críticos de `AppointmentService` siguen sin retry de concurrencia — manejarán `DbUpdateConcurrencyException` con 500 en lugar de 409.

---

## SQL nuevos a aplicar (en orden)

1. `SQL_ADD_REFUND_FAILED_AT.sql` (P3-1).
2. `SQL_ADD_AMOUNTCENTS.sql` (P3-10).

Ambos son idempotentes (`IF NOT EXISTS` / `UPDATE ... WHERE AmountCents = 0`).
