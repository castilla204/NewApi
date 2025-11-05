# Mejores Prácticas de Logging Implementadas

## Principios Aplicados

### 1. **Un Log Crítico por Evento**
- ✅ Cada error crítico se loguea **UNA SOLA VEZ** en el punto donde ocurre
- ✅ Evita duplicación: Si `ProcessMoneyDistributionAsync` ya loguea, el controller **NO** duplica el log
- ✅ El controller solo referencia el log existente mediante `logId` en la respuesta

### 2. **Información Completa y Descriptiva**
Cada log crítico incluye:
- **Mensaje claro**: Describe QUÉ ocurrió
- **Detalles completos**: Incluye contexto, valores, IDs relevantes
- **ACTION REQUIRED**: Instrucciones específicas para resolver el problema
- **AdditionalData estructurado**: JSON con toda la información técnica

### 3. **Cobertura Total de Casos de Error**

#### En `ProcessMoneyDistributionAsync`:
1. ✅ **SearchHire no encontrado** - Log crítico con detalles
2. ✅ **Configuración de distribución faltante** - Log crítico con status y contexto
3. ✅ **Configuración inválida** (porcentajes no suman 100%) - Log crítico con valores
4. ✅ **Pago original no encontrado** - Log crítico con información de búsqueda
5. ✅ **Balance insuficiente** - Log crítico con balance disponible vs requerido
6. ✅ **Error verificando balance** - Log crítico con error de Stripe
7. ✅ **PaymentIntent no capturado** - Log crítico con estado y detalles
8. ✅ **Error verificando PaymentIntent** - Log crítico con error específico
9. ✅ **Cuenta de experto faltante** - Log crítico con instrucciones manuales
10. ✅ **Cuenta de experto no habilitada** - Log crítico con estado de cuenta
11. ✅ **Error de Stripe durante transfer/refund** - Log crítico con estado de transacciones
12. ✅ **Error general durante transacción** - Log crítico con stack trace
13. ✅ **Error fuera de transacción** - Log crítico indicando problema pre-transacción

#### En `SearchHireController.CompleteService`:
1. ✅ **Error antes de llamar ProcessMoneyDistributionAsync** - Log crítico (StripeException, Exception)
2. ✅ **Error después de ProcessMoneyDistributionAsync falla** - NO duplica log, solo referencia

### 4. **Estructura de Logs Críticos**

```csharp
await _loggingService.LogCriticalAsync(
    message: "CRITICAL: [Tipo de error específico]",
    details: $"Contexto completo: [qué ocurrió, por qué, valores relevantes]. " +
            $"ACTION REQUIRED: [instrucciones específicas para resolver].",
    userId: userId,
    source: "ServiceOrController.MethodName",
    relatedEntityType: "SearchHire|Appointment|etc",
    relatedEntityId: entityId,
    additionalData: new { 
        // Todos los datos técnicos relevantes en JSON
    }
);
```

### 5. **Evitar Duplicación**

**ANTES (INCORRECTO):**
- Controller loguea error genérico
- Service loguea error específico
- Resultado: 2 logs para el mismo evento

**AHORA (CORRECTO):**
- Service loguea error específico con toda la información
- Controller NO duplica, solo referencia el log existente
- Resultado: 1 log completo por evento

### 6. **Mensajes Descriptivos**

**Ejemplos de mensajes mejorados:**

- ❌ Antes: "Money distribution failed"
- ✅ Ahora: "CRITICAL: Insufficient Stripe platform balance for money distribution"

- ❌ Antes: "Error processing payment"
- ✅ Ahora: "CRITICAL: Stripe exception during money distribution transaction"

### 7. **Información de Contexto en AdditionalData**

Cada log incluye:
- IDs relevantes (SearchHireId, ExpertId, PaymentIntentId, etc.)
- Valores financieros (Amounts, Percentages, Balances)
- Estados (Status, PaymentIntentStatus)
- Errores técnicos (ErrorType, ErrorMessage, StackTrace, StripeError details)
- Flags de transacciones (CreatedTransferId, CreatedRefundId)

## Endpoints de Cambio de Estado

Todos los endpoints que cambian estados críticos deben seguir este patrón:

1. **Validaciones tempranas** → Log crítico si fallan
2. **Llamada a servicio** → El servicio loguea si falla
3. **Catch blocks** → Loguean solo si el error ocurre en el controller (no en el servicio)
4. **Un log por evento** → Nunca duplicar

## Beneficios

✅ **Trazabilidad completa**: Cada error tiene un log único y completo
✅ **Sin duplicación**: Un evento = un log
✅ **Información accionable**: Cada log incluye qué hacer
✅ **Fácil debugging**: Stack traces, IDs, y contexto completo
✅ **Auditoría**: Todos los eventos críticos están registrados

