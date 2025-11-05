# Implementación de Captura Manual de Pagos con Stripe

## Resumen
Se implementó **captura manual de pagos** usando Stripe Checkout Session con PaymentIntent para evitar perder comisiones de Stripe cuando ocurren errores después del pago.

## Cambios Implementados

### 1. Configuración de Captura Manual en Checkout Sessions

**Archivos modificados:**
- `Controllers/SubscriptionController.cs`
- `Controllers/SearchController.cs`

**Cambios:**

Se agregó `PaymentIntentData` con `CaptureMethod = "manual"` a todas las Checkout Sessions que procesan pagos de servicios:

```csharp
var options = new SessionCreateOptions
{
    // ... configuración existente ...
    PaymentIntentData = new SessionPaymentIntentDataOptions
    {
        CaptureMethod = "manual"  // ✅ NUEVO: Autoriza pero no captura automáticamente
    }
};
```

**Ubicaciones:**
1. `LoadMoneyService` (línea ~1463)
2. `HireService` (línea ~3023)
3. `SearchController.CreateSearchWithHire` (línea ~382)

**Efecto:** 
- El pago se **autoriza** cuando el usuario completa el checkout
- El pago **NO se captura** automáticamente
- Queda en estado `requires_capture` hasta que lo capturemos manualmente

---

### 2. Captura del PaymentIntent Después de Validar Todo

**Archivo:** `Controllers/SubscriptionController.cs` - Método `HandlePendingHireCompleted`

**Cambio:** Se agregó lógica para capturar el PaymentIntent **después** de validar y crear todo exitosamente:

```csharp
// Después de transaction.CommitAsync() exitoso:

// ✅ CAPTURA MANUAL: Capturar el PaymentIntent SOLO después de validar todo exitosamente
if (!string.IsNullOrEmpty(session.PaymentIntentId))
{
    try
    {
        var paymentIntentService = new PaymentIntentService();
        
        // ✅ EDGE CASE: Verificar el estado del PaymentIntent antes de capturar
        var paymentIntent = await paymentIntentService.GetAsync(session.PaymentIntentId);
        
        if (paymentIntent.Status == "requires_capture")
        {
            // ✅ Estado correcto: Capturar el PaymentIntent
            var capturedPaymentIntent = await paymentIntentService.CaptureAsync(session.PaymentIntentId);
            _logger.LogInformation("✅ PaymentIntent captured successfully...");
        }
        else if (paymentIntent.Status == "succeeded")
        {
            // Ya está capturado (edge case)
            _logger.LogWarning("⚠️ PaymentIntent already captured...");
        }
        else
        {
            // Estado inesperado: No se puede capturar
            _logger.LogError("❌ PaymentIntent in unexpected state...");
            // Registrar crítico pero no lanzar excepción
        }
    }
    catch (StripeException stripeEx)
    {
        // Registrar error pero no lanzar excepción
        // El PaymentIntent expirará en 7 días automáticamente sin comisiones
    }
}
```

**Ubicación:** Línea ~2497-2545

---

### 3. Manejo de Errores Sin Refund (Solo No Capturar)

**Archivo:** `Controllers/SubscriptionController.cs` - Método `HandlePendingHireCompleted`

**Cambio:** Si hay error durante el procesamiento, **NO se hace refund**. Simplemente **NO se captura** el PaymentIntent:

```csharp
catch (Exception ex)
{
    await transaction.RollbackAsync();
    _logger.LogError(ex, "❌ ERROR PROCESSING PENDING HIRE...");
    
    // ✅ CON CAPTURA MANUAL: NO hacer refund - simplemente NO capturar el PaymentIntent
    // El PaymentIntent queda en estado "requires_capture" y expira en 7 días automáticamente
    // Esto evita perder comisiones porque nunca se capturó el pago
    if (session != null && !string.IsNullOrEmpty(session.PaymentIntentId))
    {
        _logger.LogWarning("⚠️ ERROR DETECTED - PaymentIntent will NOT be captured. It will expire in 7 days automatically.");
        // Registrar crítico pero NO capturar
    }
    
    return; // ✅ Retornar silenciosamente - webhook retorna 200 OK
}
```

**Ubicación:** Línea ~2556-2569

**Efecto:**
- Si falla: PaymentIntent no se captura → Expira en 7 días → **Sin comisiones perdidas**
- Si OK: PaymentIntent se captura → Pago completo

---

### 4. Validaciones Antes del Pago (Previenen Errores)

**Archivos modificados:**
- `Controllers/SubscriptionController.cs` (métodos `LoadMoneyService`, `HireService`)
- `Controllers/SearchController.cs` (método `CreateSearchWithHire`)

**Validaciones agregadas ANTES de crear el checkout session:**

1. **Experto no puede contratarse a sí mismo:**
   ```csharp
   if (service.ExpertProfile != null && service.ExpertProfile.UserId == userId)
   {
       return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
   }
   ```

2. **Teléfono verificado:**
   ```csharp
   if (!user.PhoneVerified)
   {
       return StatusCode(403, new { message = "Phone verification required to create hires" });
   }
   ```

**Ubicaciones:**
- `LoadMoneyService`: líneas ~1385, ~1404
- `HireService`: líneas ~2929, ~2956
- `SearchController.CreateSearchWithHire`: línea ~331

**Efecto:** Errores detectados **antes** de crear el checkout → No se procesa el pago → No hay que capturar ni refund

---

### 5. Mejoras en Validación de Webhook Signature (CRÍTICO - PREVIENE ATAQUES)

**Archivo:** `Controllers/SubscriptionController.cs` - Métodos `HandleStripeWebhook` y `HandleGeneralStripeWebhook`

**Cambios:**

1. **Conversión correcta de StringValues a string:**
   ```csharp
   // ✅ SEGURIDAD: Convertir StringValues a string (puede venir como array)
   var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
   ```

2. **Validaciones explícitas antes de `ConstructEvent`:**
   ```csharp
   // ✅ SEGURIDAD CRÍTICA: Validar signature antes de procesar
   // EventUtility.ConstructEvent valida la signature y lanza StripeException si es inválida
   // Esto previene ataques de replay e inyección de eventos falsos
   if (string.IsNullOrEmpty(_webhookSecret))
   {
       _logger.LogError("❌ WEBHOOK SECRET IS NULL OR EMPTY!");
       return BadRequest(new { error = "Webhook secret not configured" });
   }
   
   if (string.IsNullOrEmpty(signatureHeader))
   {
       _logger.LogError("❌ STRIPE SIGNATURE HEADER IS NULL OR EMPTY!");
       return BadRequest(new { error = "Stripe signature header missing" });
   }
   
   var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _webhookSecret);
   ```

3. **Manejo específico de errores de signature inválida:**
   ```csharp
   catch (StripeException e)
   {
       // ✅ SEGURIDAD: Si la signature es inválida, ConstructEvent lanza StripeException
       // Esto previene ataques de replay e inyección de eventos falsos
       if (e.Message?.Contains("signature") == true || e.Message?.Contains("Invalid signature") == true)
       {
           _logger.LogError(e, "❌ SECURITY: Invalid webhook signature - potential attack attempt. Signature: {Signature}", signatureHeader);
           return BadRequest(new { error = "Invalid webhook signature" });
       }
       // ... resto del manejo
   }
   ```

**Ubicaciones:**
- `HandleStripeWebhook`: líneas ~1500-1522, ~2040-2052
- `HandleGeneralStripeWebhook`: líneas ~2072-2096, ~2265-2277

**⚠️ IMPORTANTE - VECTOR DE ATAQUE CRÍTICO:**
- Sin `ConstructEvent`: Un atacante podría forjar eventos falsos (ej. inyectar `checkout.session.completed` para crear hires gratis)
- Con `ConstructEvent`: La signature se valida criptográficamente, previniendo replay attacks y eventos falsos
- **NUNCA usar `ParseEvent`**: Solo deserializa el JSON sin validar signature
- **SIEMPRE usar `ConstructEvent`**: Valida signature y lanza `StripeException` si es inválida

---

## Flujo Completo del Pago

### Antes (Captura Automática):
1. Usuario completa checkout → **Pago se captura automáticamente**
2. Webhook `checkout.session.completed` → Valida y crea hire
3. Si falla → **Hacer refund** → **Pierdes comisiones de Stripe**

### Ahora (Captura Manual):
1. Usuario completa checkout → **Pago se autoriza (NO se captura)**
2. Webhook `checkout.session.completed` → Valida y crea hire
3. Si todo OK → **Capturar PaymentIntent** → Pago completo
4. Si falla → **NO capturar PaymentIntent** → Expira en 7 días → **Sin comisiones perdidas**

---

## Beneficios

1. ✅ **Sin pérdida de comisiones**: Si algo falla, no se captura el pago
2. ✅ **Mejor UX**: Mantiene la UI profesional de Checkout Session
3. ✅ **Control total**: Solo se captura después de validar todo
4. ✅ **Automático**: Si no se captura, expira sin intervención

---

## Consideraciones Importantes

1. **Autorizaciones expiran en 7 días**: Si no capturas dentro de este plazo, la autorización caduca y no podrás capturar el pago
2. **Estado del PaymentIntent**: Siempre verificar `Status == "requires_capture"` antes de capturar
3. **Webhook retorna 200 OK**: Incluso si hay error, el webhook debe retornar 200 para evitar reintentos de Stripe
4. **Idempotencia**: El sistema verifica si el evento ya fue procesado antes de procesarlo nuevamente

---

## Archivos Modificados

1. `Controllers/SubscriptionController.cs`
   - Agregado `PaymentIntentData` en `LoadMoneyService` (línea ~1463)
   - Agregado `PaymentIntentData` en `HireService` (línea ~3023)
   - Agregado captura manual en `HandlePendingHireCompleted` (línea ~2497)
   - Mejorado manejo de errores sin refund (línea ~2556)
   - Agregadas validaciones antes del pago (líneas ~1385, ~1404, ~2929, ~2956)
   - **Mejorada validación de webhook signature (CRÍTICO)**:
     - Conversión correcta de StringValues a string (línea ~1501, ~2072)
     - Validaciones explícitas antes de `ConstructEvent` (líneas ~1507-1519, ~2084-2094)
     - Manejo específico de errores de signature inválida (líneas ~2040-2052, ~2265-2277)

2. `Controllers/SearchController.cs`
   - Agregado `PaymentIntentData` en `CreateSearchWithHire` (línea ~382)
   - Agregada validación experto no puede contratarse a sí mismo (línea ~331)

---

## Testing Recomendado

1. **Usar Stripe CLI**: `stripe trigger checkout.session.completed` para simular webhooks
2. **Probar edge cases**:
   - PaymentIntent en estado `succeeded` (ya capturado)
   - PaymentIntent en estado inesperado
   - Error durante validaciones
   - Error durante captura
3. **Verificar expiración**: Confirmar que PaymentIntents no capturados expiran correctamente

