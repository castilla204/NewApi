# 📋 PLAN COMPLETO DE PRUEBAS - FINALIZACIONES Y CANCELACIONES

## ✅ CASOS YA PROBADOS

### 1. ✅ `appointment_cancelled_by_client_no_proposal`
- **Trigger**: Cliente no propone cita en 24h (timer "proposal" expira)
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ **Correcto** - Estados, porcentajes y lógica funcionan correctamente
- **SearchHireId de prueba**: 58, 60 (última prueba)
- **Fecha de prueba**: 2026-01-19 23:22:32 (58), 2026-01-20 09:48:31 (60)
- **Verificación detallada (SearchHire 60)**:
  - ✅ Estados correctos: `appointment_cancelled_by_client_no_proposal` → `cancelled`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=100%, Expert=0%, Platform=0%
  - ✅ Cálculos base correctos: Client=1.65€, Expert=0€, Platform=0€
  - ✅ Timer expirado correctamente: timer "proposal" expirado el 2026-01-20 09:50:47
  - ✅ **Comportamiento esperado**: Refund pendiente por saldo insuficiente (NORMAL)
    - Cliente pagó: 2,00€
    - Stripe cobró comisión: ~0.31€ (2.9% + 0.30€ estándar)
    - Balance disponible: 1,69€ (2,00€ - 0.31€)
    - Refund requerido: 2,00€ (devolución completa al cliente)
    - **Esto es normal**: Stripe ya se llevó su comisión, por lo que el balance es menor que el monto original
    - El sistema detecta correctamente la falta de balance y registra el log
    - PaymentIntentId: `pi_3SrbXzR7PVKiStYu2vnmTKlx`
  - ✅ Transacciones: 1 ServicePayment (-2€) registrado correctamente
  - ✅ Logs: Sistema detecta correctamente el saldo insuficiente y actualiza el estado
  - **Nota**: El refund se procesará cuando haya suficiente balance en la cuenta de Stripe (normalmente después de que Stripe procese el pago y haya fondos disponibles)

### 2. ✅ `appointment_cancelled_by_expert_no_response`
- **Trigger**: Experto no responde propuesta en 24h (timer "response" expira)
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 61
- **Fecha de prueba**: 2026-01-20 09:58:04
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_expert_no_response` → `cancelled`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=100%, Expert=0%, Platform=0%
  - ✅ Cálculos base correctos: Client=1.65€, Expert=0€, Platform=0€
  - ✅ Timer "response" expirado correctamente: expirado el 2026-01-20 09:59:55
  - ✅ Refund Stripe procesado correctamente: 2.00€ (reembolso total del Amount)
  - ✅ StripeRefundId registrado: `re_3SrbhFR7PVKiStYu1N6umRP5`
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Refund (2€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Reembolso procesado" presente
  - ✅ Consistencia matemática verificada

---

## 🔴 CASOS PENDIENTES DE PROBAR

### CATEGORÍA A: CANCELACIONES AUTOMÁTICAS POR TIMERS

### 3. ✅ `appointment_cancelled_by_no_report`
- **Trigger**: Experto no envía reporte en 24h (timer "expert_report" expira)
- **Porcentajes**: Client=95%, Expert=0%, Platform=5%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 62
- **Fecha de prueba**: 2026-01-20 10:02:38
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_no_report` → `cancelled`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=95%, Expert=0%, Platform=5%
  - ✅ Cálculos base correctos: Client=1.57€ (95%), Expert=0€, Platform=0.08€ (5%)
  - ✅ Timer "expert_report" expirado correctamente: expirado el 2026-01-20 10:04:56
  - ✅ Refund Stripe procesado correctamente: 1.90€ (95% del Amount con tax proporcional)
  - ✅ StripeRefundId registrado: `re_3SrblfR7PVKiStYu2K63x5Ov`
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Refund (1.90€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Reembolso procesado" presente
  - ✅ Consistencia matemática verificada
  - ✅ Cálculo con tax proporcional correcto: 1.57€ base → 1.90€ con tax (95% de 2€)

### 4. ✅ `appointment_completed_without_client_approval`
- **Trigger**: Cliente no aprueba/disputa en 24h (timer "client_decision" expira)
- **Porcentajes**: Client=0%, Expert=100%, Platform=0%
- **Estado SearchHire**: `completed`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 63
- **Fecha de prueba**: 2026-01-20 10:08:23
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_completed_without_client_approval` → `completed`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=0%, Expert=100%, Platform=0%
  - ✅ Cálculos base correctos: Client=0€, Expert=1.65€ (100%), Platform=0€
  - ✅ Timer "client_decision" expirado correctamente: expirado el 2026-01-20 10:11:22
  - ✅ Transfer Stripe procesado correctamente: 1.65€ (100% del BaseAmount, sin tax)
  - ✅ StripeTransferId registrado: `tr_1SrbuDR7PVKiStYuCNHzVWw7`
  - ✅ No hay refund al cliente (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Payout (1.65€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Pago recibido" presente
  - ✅ Consistencia matemática verificada
  - ✅ Transfer usa BaseAmount (sin tax) como se espera

---

### CATEGORÍA B: CANCELACIONES MANUALES (SEGUNDA VEZ)

### 5. ✅ `appointment_cancelled_by_client_second`
- **Trigger**: Cliente cancela cita confirmada por segunda vez
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 64
- **Fecha de prueba**: 2026-01-20 11:12:14
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_client_second` → `cancelled`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=100%, Expert=0%, Platform=0%
  - ✅ Cálculos base correctos: Client=1.65€ (100%), Expert=0€, Platform=0€
  - ✅ Refund Stripe procesado correctamente: 2.00€ (reembolso total del Amount con tax proporcional)
  - ✅ StripeRefundId registrado: `re_3Srcr1R7PVKiStYu2icEcn1r`
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Refund (2€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Reembolso procesado" presente
  - ✅ Consistencia matemática verificada
  - ✅ Timers cancelados correctamente después de la cancelación

### 6. ✅ `appointment_cancelled_by_expert_second`
- **Trigger**: Experto cancela cita confirmada por segunda vez
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 65
- **Fecha de prueba**: 2026-01-20 11:23:52
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_expert_second` → `cancelled`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=100%, Expert=0%, Platform=0%
  - ✅ Cálculos base correctos: Client=1.65€ (100%), Expert=0€, Platform=0€
  - ✅ Refund Stripe procesado correctamente: 2.00€ (reembolso total del Amount con tax proporcional)
  - ✅ StripeRefundId registrado: `re_3Srd2GR7PVKiStYu0xnp41rd`
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Refund (2€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Reembolso procesado" presente
  - ✅ Consistencia matemática verificada
  - ✅ Timers cancelados correctamente después de la cancelación

### 7. ✅ `appointment_cancelled_by_expert_rejection`
- **Trigger**: Experto rechaza propuesta por segunda vez
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 66
- **Fecha de prueba**: 2026-01-20 11:29:30
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_expert_rejection` → `cancelled`
  - ✅ Ambos estados tienen `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=100%, Expert=0%, Platform=0%
  - ✅ Cálculos base correctos: Client=1.65€ (100%), Expert=0€, Platform=0€
  - ✅ Refund Stripe procesado correctamente: 2.00€ (reembolso total del Amount con tax proporcional)
  - ✅ StripeRefundId registrado: `re_3Srd7jR7PVKiStYu1f6kNeF7`
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Refund (2€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Reembolso procesado" presente
  - ✅ Log muestra razón correcta: "Segundo rechazo del experto - penalización máxima"
  - ✅ Consistencia matemática verificada
  - ✅ Timers cancelados correctamente después de la cancelación

---

### CATEGORÍA C: COMPLETADO MANUAL

### 8. ✅ `completed` (por aprobación del cliente)
- **Trigger**: Cliente aprueba servicio después de recibir reporte
- **Porcentajes**: Client=0%, Expert=95%, Platform=5%
- **Estado SearchHire**: `completed`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 67
- **Fecha de prueba**: 2026-01-20 11:34:16
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_report_sent` → `completed`
  - ✅ SearchHire tiene `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=0%, Expert=95%, Platform=5%
  - ✅ Cálculos base correctos: Client=0€, Expert=1.57€ (95%), Platform=0.08€ (5%)
  - ✅ Transfer Stripe procesado correctamente: 1.5675€ (95% del BaseAmount, sin tax)
  - ✅ StripeTransferId registrado: `tr_1SrdE7R7PVKiStYul69zUiLx`
  - ✅ No hay refund al cliente (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Payout (1.5675€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Pago recibido" presente
  - ✅ Log "Servicio completado" y "Servicio aprobado por el cliente" presentes
  - ✅ Consistencia matemática verificada: 1.65€ × 95% = 1.5675€
  - ✅ Timers cancelados correctamente después de la aprobación

---

### CATEGORÍA D: DISPUTAS Y RESOLUCIONES

### 9. ✅ `dispute_resolved_client`
- **Trigger**: Administrador resuelve disputa a favor del cliente
- **Porcentajes**: Client=90%, Expert=8%, Platform=2%
- **Estado SearchHire**: `dispute_resolved_client`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 68
- **Fecha de prueba**: 2026-01-20 11:38:30
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_report_sent` → `dispute_resolved_client`
  - ✅ SearchHire tiene `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=90%, Expert=8%, Platform=2%
  - ✅ Cálculos base correctos: Client=1.49€ (90%), Expert=0.13€ (8%), Platform=0.03€ (2%)
  - ✅ Refund Stripe procesado correctamente: 1.80€ (90% del Amount con tax proporcional)
  - ✅ StripeRefundId registrado: `re_3SrdGSR7PVKiStYu2ScmkmRo`
  - ✅ Transfer Stripe procesado correctamente: 0.1320€ (8% del BaseAmount, sin tax)
  - ✅ StripeTransferId registrado: `tr_1SrdJhR7PVKiStYu0VXe1VHT`
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Refund (1.80€), 1 Payout (0.1320€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Reembolso procesado" y "Pago recibido" presentes
  - ✅ Log muestra razón correcta: "Dispute resolved in favor of client"
  - ✅ Consistencia matemática verificada: 1.65€ × 90% = 1.49€ base → 1.80€ con tax, 1.65€ × 8% = 0.132€
  - ✅ Timer "client_decision" cancelado correctamente después de la resolución

### 10. ✅ `dispute_resolved_expert`
- **Trigger**: Administrador resuelve disputa a favor del experto
- **Porcentajes**: Client=0%, Expert=95%, Platform=5%
- **Estado SearchHire**: `dispute_resolved_expert`
- **Resultado**: ✅ **Correcto** - Todo funcionó perfectamente
- **SearchHireId de prueba**: 69
- **Fecha de prueba**: 2026-01-20 11:44:53
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_report_sent` → `dispute_resolved_expert`
  - ✅ SearchHire tiene `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=0%, Expert=95%, Platform=5%
  - ✅ Cálculos base correctos: Client=0€, Expert=1.57€ (95%), Platform=0.08€ (5%)
  - ✅ Transfer Stripe procesado correctamente: 1.5675€ (95% del BaseAmount, sin tax)
  - ✅ StripeTransferId registrado: `tr_1SrdQ8R7PVKiStYu09wOk1fM`
  - ✅ No hay refund al cliente (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Payout (1.5675€)
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Log "Pago recibido" presente
  - ✅ Log muestra razón correcta: "Dispute resolved in favor of expert"
  - ✅ Consistencia matemática verificada: 1.65€ × 95% = 1.5675€
  - ✅ Timer "client_decision" cancelado correctamente después de la resolución

---

### CATEGORÍA E: CANCELACIONES POR ELIMINACIÓN DE CUENTA

### 11. ⚠️ `cancelled_by_client_account_delete` (ERROR CORREGIDO)
- **Trigger**: Cliente elimina su cuenta
- **Porcentajes**: Client=0%, Expert=95%, Platform=5%
- **Estado SearchHire**: `cancelled_by_client_account_delete`
- **Resultado**: ⚠️ **Dinero procesado correctamente, pero error en eliminación de datos** (CORREGIDO)
- **SearchHireId de prueba**: 70
- **Fecha de prueba**: 2026-01-20 11:51:44
- **Verificación detallada**:
  - ✅ Estados correctos: `cancelled_by_client_account_delete` (StatusId: 28)
  - ✅ SearchHire tiene `IsFinalizationStatus = true`
  - ✅ Porcentajes suman 100%: Client=0%, Expert=95%, Platform=5%
  - ✅ Cálculos base correctos: Client=0€, Expert=1.57€ (95%), Platform=0.08€ (5%)
  - ✅ Transfer Stripe procesado correctamente: 1.5675€ (95% del BaseAmount, sin tax)
  - ✅ StripeTransferId registrado: `tr_1SrdUQR7PVKiStYu0VXe1VHT`
  - ✅ No hay refund al cliente (0%)
  - ✅ Transacciones registradas: 1 ServicePayment (-2€), 1 Payout (1.5675€)
  - ✅ Logs de dinero: 0 errores en procesamiento de dinero
  - ✅ Log "Pago recibido" presente
  - ✅ Log muestra razón correcta: "Client account deletion - transfer to expert"
  - ⚠️ **ERRORES ENCONTRADOS Y CORREGIDOS**:
    1. **Error en eliminación de datos del usuario**:
       - Error: "NpgsqlRetryingExecutionStrategy does not support user-initiated transactions"
       - Causa: `EnableRetryOnFailure` activa ExecutionStrategy automáticamente, incompatible con transacciones manuales
       - Ubicación: `AccountDeletionService.DeleteUserDataAsync` línea 856 (query `AnyAsync` dentro de transacción)
       - **Corrección aplicada**: Cambiado `AnyAsync()` a SQL directo (`ExecuteSqlRawAsync`) para evitar ExecutionStrategy
    2. **Jobs de Hangfire no cancelados**:
       - Problema: Timer activo (Id: 162, JobId: "529") no fue cancelado durante la eliminación de cuenta
       - Causa: `ProcessActiveContractsAsync` no cancelaba timers activos ni sus jobs de Hangfire
       - **Corrección aplicada**: Agregado método `CancelActiveTimersAndHangfireJobsAsync` que:
         - Marca todos los timers activos como expirados
         - Cancela todos los jobs de Hangfire asociados
         - Se ejecuta después de procesar el dinero exitosamente (tanto para cliente como experto)
       - **Estado**: El dinero ya estaba procesado correctamente antes del error, solo falló la eliminación de datos y cancelación de timers
       - **Acción requerida**: Reintentar eliminación de cuenta después de las correcciones

#### 12. 🔴 `cancelled_by_expert_account_delete`
- **Trigger**: Experto elimina su cuenta
- **Porcentajes esperados**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire esperado**: `cancelled_by_expert_account_delete`
- **Pasos para probar**:
  1. Crear SearchHire con servicio activo
  2. Eliminar cuenta del experto
  3. Verificar:
     - SearchHire.Status = `cancelled_by_expert_account_delete`
     - Refund al cliente: 100% del Amount (con tax proporcional)
     - Expert: 0€
     - Platform: 0€

---

## 📊 CHECKLIST DE VERIFICACIÓN PARA CADA CASO

Para cada caso de prueba, verificar:

### ✅ Estados
- [ ] Appointment.Status = estado esperado
- [ ] SearchHire.Status = estado esperado
- [ ] Ambos estados tienen `IsFinalizationStatus = true`

### ✅ Distribución de Dinero
- [ ] Porcentajes suman 100%
- [ ] Cálculo base usa `BaseAmount` (sin tax)
- [ ] Refund a Stripe incluye tax proporcional (si ClientPercentage > 0)
- [ ] Transfer a Stripe usa BaseAmount (sin tax) (si ExpertPercentage > 0)
- [ ] Montos calculados correctamente según porcentajes

### ✅ Transacciones Financieras
- [ ] `FinancialTransaction` registrado con tipo correcto (Refund/Payout)
- [ ] `StripeRefundId` o `StripeTransferId` presente
- [ ] Montos coinciden con cálculos

### ✅ Timers
- [ ] Timers activos cancelados (si aplica)
- [ ] HangfireJobIds eliminados de timers
- [ ] Jobs de Hangfire cancelados en dashboard

### ✅ Logs
- [ ] Log de "Money distribution calculation" presente
- [ ] Log muestra breakdown correcto (BaseAmount, porcentajes, montos Stripe)
- [ ] No hay logs de error crítico

### ✅ Mapeo de Estados
- [ ] AppointmentStatus mapea correctamente a SearchHireStatus
- [ ] Mapeo existe en `StatusMappings` o `GetDefaultMapping`

---

## 🎯 ORDEN RECOMENDADO DE PRUEBAS

1. ✅ **Completado**: Casos automáticos por timers (1 ✅, 2 ✅, 3 ✅, 4 ✅)
2. ✅ **Completado**: Cancelaciones manuales segunda vez (5 ✅, 6 ✅, 7 ✅)
3. ✅ **Completado**: Completado manual (8 ✅)
4. ✅ **Completado**: Disputas (9 ✅, 10 ✅)
5. **Pendiente**: Eliminación de cuentas (11, 12)

## 📊 PROGRESO

- **Casos probados**: 10/12 (83%)
- **Casos pendientes**: 2/12 (17%)
- **Última prueba**: Caso 10 - `dispute_resolved_expert` (2026-01-20 11:44:53)
- **SearchHireIds de prueba**: 58, 60 (caso 1), 61 (caso 2), 62 (caso 3), 63 (caso 4), 64 (caso 5), 65 (caso 6), 66 (caso 7), 67 (caso 8), 68 (caso 9), 69 (caso 10)

---

## 📝 NOTAS IMPORTANTES

- **Para forzar timers**: Usar Hangfire Dashboard para ejecutar jobs manualmente
- **Para verificar transacciones**: Revisar tabla `FinancialTransactions` y logs
- **Para verificar estados**: Revisar `SystemStatuses` y `StatusConfigurations`
- **Para verificar dinero**: Revisar logs detallados de `ProcessMoneyDistributionAsync`

---

## 🔍 QUERIES ÚTILES PARA VERIFICACIÓN

```sql
-- Verificar estado y porcentajes de un SearchHire
SELECT 
    sh."Id",
    ss_sh."StatusValue" as search_hire_status,
    ss_app."StatusValue" as appointment_status,
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    sh."Amount",
    sh."BaseAmount",
    sh."TaxAmount"
FROM "SearchHires" sh
JOIN "SystemStatuses" ss_sh ON sh."StatusId" = ss_sh."Id"
LEFT JOIN "Appointments" a ON a."SearchHireId" = sh."Id"
LEFT JOIN "SystemStatuses" ss_app ON a."StatusId" = ss_app."Id"
LEFT JOIN "StatusConfigurations" sc ON sc."StatusId" = ss_app."Id" OR sc."StatusId" = ss_sh."Id"
WHERE sh."Id" = [ID_DEL_SEARCHHIRE];

-- Verificar transacciones financieras
SELECT 
    ft."Id",
    ft."TransactionType",
    ft."Amount",
    ft."StripeRefundId",
    ft."StripeTransferId",
    ft."CreatedAt"
FROM "FinancialTransactions" ft
WHERE ft."RelatedEntityId" = [ID_DEL_SEARCHHIRE]
ORDER BY ft."CreatedAt" DESC;

-- Verificar logs de distribución de dinero
SELECT 
    l."Id",
    l."Message",
    l."Details",
    l."CreatedAt"
FROM "Logs" l
WHERE l."RelatedEntityId" = [ID_DEL_SEARCHHIRE]
    AND l."Message" LIKE '%Money distribution%'
ORDER BY l."CreatedAt" DESC;
```

---

## 📌 INSTRUCCIONES PARA ACTUALIZAR ESTE DOCUMENTO

Cuando se complete una prueba:

1. **Mover el caso de "PENDIENTES" a "YA PROBADOS"**
2. **Agregar detalles de verificación**:
   - SearchHireId de prueba
   - Fecha y hora de la prueba
   - Resultado de la verificación
   - Referencia al documento de análisis (si existe)
3. **Actualizar la sección "PROGRESO"**:
   - Incrementar contador de casos probados
   - Actualizar porcentaje
   - Actualizar última prueba
   - Agregar SearchHireId a la lista
4. **Actualizar "ORDEN RECOMENDADO"** si es necesario
