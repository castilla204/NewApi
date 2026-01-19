# 📋 PLAN COMPLETO DE PRUEBAS - FINALIZACIONES Y CANCELACIONES

## ✅ CASOS YA PROBADOS

### 1. ✅ `appointment_cancelled_by_client_no_proposal`
- **Trigger**: Cliente no propone cita en 24h (timer "proposal" expira)
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ Correcto
- **SearchHireId de prueba**: 50

### 2. ✅ `appointment_cancelled_by_expert_no_response`
- **Trigger**: Experto no responde propuesta en 24h (timer "response" expira)
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ Correcto
- **SearchHireId de prueba**: 51

### 3. ✅ `appointment_cancelled_by_no_report`
- **Trigger**: Experto no envía reporte en 24h (timer "expert_report" expira)
- **Porcentajes**: Client=95%, Expert=0%, Platform=5%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ Correcto - Verificado exhaustivamente
- **SearchHireId de prueba**: 52
- **Fecha de prueba**: 2026-01-19 22:11:07
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_no_report` → `cancelled`
  - ✅ Porcentajes suman 100%: Client=95%, Expert=0%, Platform=5%
  - ✅ Cálculos base correctos: Client=1.5675€, Expert=0€, Platform=0.0825€
  - ✅ Refund Stripe correcto: 1.90€ (2.00€ × 95% con tax proporcional)
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 Refund, 0 Payouts
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Consistencia matemática verificada
- **Documento de análisis**: `ANALISIS_CASO_3_DETALLADO.md`

### 4. ✅ `appointment_completed_without_client_approval`
- **Trigger**: Cliente no aprueba/disputa en 24h (timer "client_decision" expira)
- **Porcentajes**: Client=0%, Expert=100%, Platform=0%
- **Estado SearchHire**: `completed`
- **Resultado**: ✅ Correcto - Verificado exhaustivamente
- **SearchHireId de prueba**: 53
- **Fecha de prueba**: 2026-01-19 22:23:40
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_completed_without_client_approval` → `completed`
  - ✅ Porcentajes suman 100%: Client=0%, Expert=100%, Platform=0%
  - ✅ Cálculos base correctos: Client=0€, Expert=1.65€, Platform=0€
  - ✅ Transfer Stripe correcto: 1.65€ (BaseAmount sin tax, como debe ser)
  - ✅ No hay refund al cliente (0%)
  - ✅ Transacciones registradas: 1 Payout, 0 Refunds
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Consistencia matemática verificada

### 5. ✅ `appointment_cancelled_by_client_second`
- **Trigger**: Cliente cancela cita confirmada por segunda vez
- **Porcentajes**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire**: `cancelled`
- **Resultado**: ✅ Correcto - Verificado exhaustivamente
- **SearchHireId de prueba**: 55
- **Fecha de prueba**: 2026-01-19 22:32:31
- **Verificación detallada**:
  - ✅ Estados correctos: `appointment_cancelled_by_client_second` → `cancelled`
  - ✅ Porcentajes suman 100%: Client=100%, Expert=0%, Platform=0%
  - ✅ Cálculos base correctos: Client=1.65€, Expert=0€, Platform=0€
  - ✅ Refund Stripe correcto: 2.00€ (reembolso total del Amount, ClientPercentage=100%)
  - ✅ No hay transfer al experto (0%)
  - ✅ Transacciones registradas: 1 Refund, 0 Payouts
  - ✅ Logs sin errores: 0 errores, 0 warnings, 0 críticos
  - ✅ Consistencia matemática verificada

---

## 🔴 CASOS PENDIENTES DE PROBAR

### CATEGORÍA A: CANCELACIONES AUTOMÁTICAS POR TIMERS

*(Todos los casos de esta categoría ya han sido probados)*

---

### CATEGORÍA B: CANCELACIONES MANUALES (SEGUNDA VEZ)

#### 6. 🔴 `appointment_cancelled_by_expert_second`
- **Trigger**: Experto cancela cita confirmada por segunda vez
- **Porcentajes esperados**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire esperado**: `cancelled`
- **Pasos para probar**:
  1. Crear SearchHire con servicio que requiere cita
  2. Cliente propone cita
  3. Experto confirma cita
  4. Experto cancela (primera vez) → `appointment_cancelled_by_expert`
  5. Cliente propone nueva cita
  6. Experto confirma nueva cita
  7. Experto cancela (segunda vez) → `appointment_cancelled_by_expert_second`
  8. Verificar:
     - Appointment.Status = `appointment_cancelled_by_expert_second`
     - SearchHire.Status = `cancelled`
     - Refund: 100% del Amount (con tax proporcional)
     - Expert: 0€
     - Platform: 0€

#### 7. 🔴 `appointment_cancelled_by_expert_rejection`
- **Trigger**: Experto rechaza propuesta por segunda vez
- **Porcentajes esperados**: Client=100%, Expert=0%, Platform=0%
- **Estado SearchHire esperado**: `cancelled`
- **Pasos para probar**:
  1. Crear SearchHire con servicio que requiere cita
  2. Cliente propone cita
  3. Experto rechaza (primera vez) → `appointment_rejected`
  4. Cliente propone nueva cita
  5. Experto rechaza (segunda vez) → `appointment_cancelled_by_expert_rejection`
  6. Verificar:
     - Appointment.Status = `appointment_cancelled_by_expert_rejection`
     - SearchHire.Status = `cancelled`
     - Refund: 100% del Amount (con tax proporcional)
     - Expert: 0€
     - Platform: 0€

---

### CATEGORÍA C: COMPLETADO MANUAL

#### 8. ✅ `completed` (por aprobación del cliente)
- **Trigger**: Cliente aprueba servicio después de recibir reporte
- **Porcentajes esperados**: Client=0%, Expert=95%, Platform=5%
- **Estado SearchHire esperado**: `completed`
- **Pasos para probar**:
  1. Crear SearchHire con servicio que requiere cita
  2. Cliente propone cita
  3. Experto confirma cita
  4. Esperar 3h para que cambie a "awaiting_report"
  5. Experto envía reporte (con archivos requeridos)
  6. Cliente aprueba el servicio
  7. Verificar:
     - Appointment.Status = `appointment_report_sent` (o similar)
     - SearchHire.Status = `completed`
     - Transfer al experto: 95% del BaseAmount (sin tax)
     - Platform: 5% del BaseAmount
     - Client: 0€

---

### CATEGORÍA D: DISPUTAS Y RESOLUCIONES

#### 9. 🔴 `dispute_resolved_client`
- **Trigger**: Administrador resuelve disputa a favor del cliente
- **Porcentajes esperados**: Client=90%, Expert=8%, Platform=2%
- **Estado SearchHire esperado**: `dispute_resolved_client`
- **Pasos para probar**:
  1. Crear SearchHire con servicio que requiere cita
  2. Completar flujo hasta que experto envíe reporte
  3. Cliente abre disputa
  4. Administrador resuelve disputa a favor del cliente
  5. Verificar:
     - SearchHire.Status = `dispute_resolved_client`
     - Refund al cliente: 90% del BaseAmount (con tax proporcional)
     - Transfer al experto: 8% del BaseAmount (sin tax)
     - Platform: 2% del BaseAmount
     - Timer "client_decision" cancelado

#### 10. 🔴 `dispute_resolved_expert`
- **Trigger**: Administrador resuelve disputa a favor del experto
- **Porcentajes esperados**: Client=0%, Expert=95%, Platform=5%
- **Estado SearchHire esperado**: `dispute_resolved_expert`
- **Pasos para probar**:
  1. Crear SearchHire con servicio que requiere cita
  2. Completar flujo hasta que experto envíe reporte
  3. Cliente abre disputa
  4. Administrador resuelve disputa a favor del experto
  5. Verificar:
     - SearchHire.Status = `dispute_resolved_expert`
     - Transfer al experto: 95% del BaseAmount (sin tax)
     - Platform: 5% del BaseAmount
     - Client: 0€
     - Timer "client_decision" cancelado

---

### CATEGORÍA E: CANCELACIONES POR ELIMINACIÓN DE CUENTA

#### 11. 🔴 `cancelled_by_client_account_delete`
- **Trigger**: Cliente elimina su cuenta
- **Porcentajes esperados**: Client=0%, Expert=95%, Platform=5%
- **Estado SearchHire esperado**: `cancelled_by_client_account_delete`
- **Pasos para probar**:
  1. Crear SearchHire con servicio activo
  2. Eliminar cuenta del cliente
  3. Verificar:
     - SearchHire.Status = `cancelled_by_client_account_delete`
     - Transfer al experto: 95% del BaseAmount (sin tax)
     - Platform: 5% del BaseAmount
     - Client: 0€

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

1. ✅ **Completado**: Casos automáticos por timers (1, 2, 3, 4)
2. ⏳ **En progreso**: Cancelaciones manuales segunda vez (5 ✅, 6, 7)
3. **Pendiente**: Completado manual (8)
4. **Pendiente**: Disputas (9, 10)
5. **Pendiente**: Eliminación de cuentas (11, 12)

## 📊 PROGRESO

- **Casos probados**: 5/12 (42%)
- **Casos pendientes**: 7/12 (58%)
- **Última prueba**: Caso 5 - `appointment_cancelled_by_client_second` (2026-01-19 22:32:31)
- **SearchHireIds de prueba**: 50, 51, 52, 53, 55

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
