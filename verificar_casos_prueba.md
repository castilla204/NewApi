# Script de Verificación de Casos de Prueba

Este documento contiene las queries SQL para verificar cada caso de prueba usando el MCP de Supabase.

## Función de Verificación Genérica

Para cada caso, ejecutar estas queries en orden:

### 1. Verificar Estados

```sql
-- Verificar estado del SearchHire y Appointment
SELECT 
    sh."Id" as search_hire_id,
    ss_sh."StatusValue" as search_hire_status,
    ss_sh."IsFinalizationStatus" as sh_is_final,
    ss_app."StatusValue" as appointment_status,
    ss_app."IsFinalizationStatus" as app_is_final,
    sh."Amount",
    sh."BaseAmount",
    sh."TaxAmount"
FROM "SearchHires" sh
JOIN "SystemStatuses" ss_sh ON sh."StatusId" = ss_sh."Id"
LEFT JOIN "Appointments" a ON a."SearchHireId" = sh."Id"
LEFT JOIN "SystemStatuses" ss_app ON a."StatusId" = ss_app."Id"
WHERE sh."Id" = [ID_DEL_SEARCHHIRE];
```

### 2. Verificar Porcentajes de Distribución

```sql
-- Verificar porcentajes configurados para el estado
SELECT 
    sc."StatusId",
    ss."StatusValue" as status_value,
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    (sc."ClientPercentage" + sc."ExpertPercentage" + sc."PlatformPercentage") as total_percentage
FROM "StatusConfigurations" sc
JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
WHERE ss."StatusValue" IN (
    SELECT "StatusValue" FROM "SystemStatuses" 
    WHERE "Id" IN (
        SELECT "StatusId" FROM "SearchHires" WHERE "Id" = [ID_DEL_SEARCHHIRE]
        UNION
        SELECT "StatusId" FROM "Appointments" WHERE "SearchHireId" = [ID_DEL_SEARCHHIRE]
    )
);
```

### 3. Verificar Transacciones Financieras

```sql
-- Verificar transacciones financieras
SELECT 
    ft."Id",
    ft."TransactionType",
    ft."Amount",
    ft."StripeRefundId",
    ft."StripeTransferId",
    ft."CreatedAt",
    ft."RelatedEntityId",
    ft."RelatedEntityType"
FROM "FinancialTransactions" ft
WHERE ft."RelatedEntityId" = [ID_DEL_SEARCHHIRE]
    AND ft."RelatedEntityType" = 'SearchHire'
ORDER BY ft."CreatedAt" DESC;
```

### 4. Verificar Logs de Distribución de Dinero

```sql
-- Verificar logs de distribución de dinero
SELECT 
    l."Id",
    l."Message",
    l."Details",
    l."CreatedAt",
    l."SeverityId",
    s."Name" as severity_name
FROM "Logs" l
LEFT JOIN "Severities" s ON l."SeverityId" = s."Id"
WHERE l."RelatedEntityId" = [ID_DEL_SEARCHHIRE]
    AND (l."Message" LIKE '%Money distribution%' 
         OR l."Message" LIKE '%refund%' 
         OR l."Message" LIKE '%transfer%'
         OR l."Message" LIKE '%payout%')
ORDER BY l."CreatedAt" DESC;
```

### 5. Verificar Timers

```sql
-- Verificar timers del appointment
SELECT 
    at."Id",
    at."TimerType",
    at."IsExpired",
    at."EndTime",
    at."ExpiredAt",
    at."HangfireJobId",
    a."Id" as appointment_id,
    a."SearchHireId"
FROM "AppointmentTimers" at
JOIN "Appointments" a ON at."AppointmentId" = a."Id"
WHERE a."SearchHireId" = [ID_DEL_SEARCHHIRE]
ORDER BY at."CreatedAt" DESC;
```

---

## Casos Específicos

### Caso 2: `appointment_cancelled_by_expert_no_response`

**Estado esperado:**
- Appointment.Status = `appointment_cancelled_by_expert_no_response`
- SearchHire.Status = `cancelled` (o `cancelled_by_expert_no_response` si existe)

**Porcentajes esperados:**
- Client = 100%
- Expert = 0%
- Platform = 0%

**Transacciones esperadas:**
- 1 Refund de 100% del Amount (con tax proporcional)
- 0 Payouts

**Query de verificación completa:**

```sql
-- Verificación completa del caso 2
WITH search_hire_data AS (
    SELECT 
        sh."Id",
        sh."Amount",
        sh."BaseAmount",
        sh."TaxAmount",
        ss_sh."StatusValue" as sh_status,
        ss_app."StatusValue" as app_status
    FROM "SearchHires" sh
    JOIN "SystemStatuses" ss_sh ON sh."StatusId" = ss_sh."Id"
    LEFT JOIN "Appointments" a ON a."SearchHireId" = sh."Id"
    LEFT JOIN "SystemStatuses" ss_app ON a."StatusId" = ss_app."Id"
    WHERE sh."Id" = [ID_DEL_SEARCHHIRE]
),
status_config AS (
    SELECT 
        sc."ClientPercentage",
        sc."ExpertPercentage",
        sc."PlatformPercentage"
    FROM "StatusConfigurations" sc
    JOIN "SystemStatuses" ss ON sc."StatusId" = ss."Id"
    WHERE ss."StatusValue" = (SELECT app_status FROM search_hire_data)
),
financial_summary AS (
    SELECT 
        COUNT(*) FILTER (WHERE "TransactionType" = 'Refund') as refund_count,
        COUNT(*) FILTER (WHERE "TransactionType" = 'Payout') as payout_count,
        SUM("Amount") FILTER (WHERE "TransactionType" = 'Refund') as total_refund,
        SUM("Amount") FILTER (WHERE "TransactionType" = 'Payout') as total_payout
    FROM "FinancialTransactions"
    WHERE "RelatedEntityId" = [ID_DEL_SEARCHHIRE]
)
SELECT 
    shd.*,
    sc."ClientPercentage",
    sc."ExpertPercentage",
    sc."PlatformPercentage",
    fs.refund_count,
    fs.payout_count,
    fs.total_refund,
    fs.total_payout,
    CASE 
        WHEN shd.app_status = 'appointment_cancelled_by_expert_no_response' THEN '✅ Estado correcto'
        ELSE '❌ Estado incorrecto'
    END as estado_check,
    CASE 
        WHEN sc."ClientPercentage" = 100 AND sc."ExpertPercentage" = 0 AND sc."PlatformPercentage" = 0 THEN '✅ Porcentajes correctos'
        ELSE '❌ Porcentajes incorrectos'
    END as porcentajes_check
FROM search_hire_data shd
CROSS JOIN status_config sc
CROSS JOIN financial_summary fs;
```
