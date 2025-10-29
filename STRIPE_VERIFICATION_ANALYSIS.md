# 🔒 **ANÁLISIS COMPLETO: VERIFICACIÓN DE STRIPE WEBHOOKS**

## 📋 **RESUMEN EJECUTIVO**

He analizado a fondo la implementación de verificación de Stripe en tu aplicación. **La implementación está mayormente correcta**, pero hay **una mejora crítica** que debe aplicarse para garantizar la seguridad total.

---

## ✅ **LO QUE ESTÁ BIEN IMPLEMENTADO**

### **1. Verificación de Firma de Webhook**
✅ **Correcto**: Uso de `EventUtility.ConstructEvent()`
```csharp
var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _webhookSecret);
```
- Esta es la forma **oficial y recomendada** por Stripe para verificar webhooks
- `EventUtility.ConstructEvent()` hace automáticamente:
  - Verificación de la firma HMAC SHA-256
  - Validación del timestamp (previene replay attacks)
  - Construcción del objeto Event

### **2. Validación de Secretos**
✅ **Correcto**: Validación de secretos antes de usar
```csharp
if (string.IsNullOrEmpty(_generalWebhookSecret))
{
    _logger.LogError("❌ GENERAL WEBHOOK SECRET IS NULL OR EMPTY!");
    return BadRequest(new { error = "Webhook secret not configured" });
}
```

### **3. Validación de Headers**
✅ **Correcto**: Verificación del header Stripe-Signature
```csharp
if (string.IsNullOrEmpty(signatureHeader))
{
    _logger.LogError("❌ STRIPE SIGNATURE HEADER IS NULL OR EMPTY!");
    return BadRequest(new { error = "Stripe signature header missing" });
}
```

### **4. Idempotencia Completa**
✅ **Excelente**: Sistema de idempotencia bien implementado
```csharp
// Verificar si el evento ya fue procesado
if (await IsEventProcessedAsync(stripeEvent.Id))
{
    _logger.LogInformation("🔄 DEBUG: Evento ya procesado, ignorando");
    return Ok(new { message = "Event already processed" });
}

// Marcar evento como procesado después de procesarlo exitosamente
await MarkEventAsProcessedAsync(stripeEvent.Id, stripeEvent.Type);
```

### **5. Manejo de Errores**
✅ **Correcto**: Manejo adecuado de excepciones específicas de Stripe
```csharp
catch (StripeException e)
{
    _logger.LogError(e, "Stripe webhook error");
    return BadRequest(new { error = e.Message });
}
```

---

## ⚠️ **PROBLEMA CRÍTICO IDENTIFICADO**

### **🚨 Request.Body sin EnableBuffering**

**Problema Actual:**
```csharp
[HttpPost("webhook")]
[AllowAnonymous]
public async Task<IActionResult> HandleStripeWebhook()
{
    var json = await new StreamReader(Request.Body).ReadToEndAsync();
    // ⚠️ PROBLEMA: Request.Body puede estar ya consumido
    var signatureHeader = Request.Headers["Stripe-Signature"];
    // ...
}
```

**¿Por qué es un problema?**
- En ASP.NET Core, el `Request.Body` es un stream que **solo se puede leer una vez**
- Si algún middleware anterior intenta leer el body, el stream se consume
- Esto puede causar que la verificación de firma falle porque el body está vacío o corrupto
- Además, si hay binding de modelos, el framework puede haber leído el body antes

**Solución:**
```csharp
[HttpPost("webhook")]
[AllowAnonymous]
public async Task<IActionResult> HandleStripeWebhook()
{
    // ✅ HABILITAR BUFFERING antes de leer el body
    Request.EnableBuffering();
    
    // Resetear la posición del stream al inicio
    Request.Body.Position = 0;
    
    var json = await new StreamReader(Request.Body).ReadToEndAsync();
    var signatureHeader = Request.Headers["Stripe-Signature"];
    // ...
}
```

---

## 🔐 **CÓMO FUNCIONA LA VERIFICACIÓN DE STRIPE**

### **1. Proceso de Verificación de Firma**

```
┌─────────────────────────────────────────────────────────────┐
│ PASO 1: Stripe envía webhook con header Stripe-Signature    │
│         Header contiene: t=timestamp,v1=signature           │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ PASO 2: Tu servidor recibe el webhook                       │
│         - Lee el body completo                               │
│         - Extrae el header Stripe-Signature                 │
│         - Extrae el webhook secret de configuración         │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ PASO 3: EventUtility.ConstructEvent() verifica:            │
│         ✅ Firma HMAC SHA-256                                │
│         ✅ Timestamp (previene replay attacks)               │
│         ✅ Construcción del objeto Event                     │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ PASO 4: Si la verificación falla:                          │
│         - Lanza StripeException                             │
│         - Debes retornar BadRequest                         │
│                                                              │
│         Si la verificación es exitosa:                      │
│         - Continúa con el procesamiento                     │
└─────────────────────────────────────────────────────────────┘
```

### **2. Verificación HMAC SHA-256**

Stripe usa HMAC SHA-256 para firmar los webhooks:

```python
# Pseudocódigo de cómo Stripe genera la firma
import hmac
import hashlib

def generate_signature(payload, secret, timestamp):
    signed_payload = f"{timestamp}.{payload}"
    signature = hmac.new(
        secret.encode('utf-8'),
        signed_payload.encode('utf-8'),
        hashlib.sha256
    ).hexdigest()
    return signature
```

**El header Stripe-Signature contiene:**
```
t=1234567890,v1=abc123def456...
```
- `t`: Timestamp del evento
- `v1`: Firma HMAC SHA-256

**EventUtility.ConstructEvent() verifica:**
1. Calcula la firma esperada usando el secret y el timestamp
2. Compara con la firma recibida
3. Verifica que el timestamp no sea muy antiguo (previene replay attacks)
4. Si todo es correcto, construye el objeto Event

### **3. Protección contra Replay Attacks**

Stripe incluye el timestamp en la firma para prevenir replay attacks:
- El timestamp debe ser reciente (normalmente dentro de 5 minutos)
- Si alguien intenta reenviar un webhook antiguo, fallará la verificación

---

## 🛠️ **MEJORAS RECOMENDADAS**

### **1. Habilitar EnableBuffering (CRÍTICO)**

```csharp
[HttpPost("webhook")]
[AllowAnonymous]
public async Task<IActionResult> HandleStripeWebhook()
{
    try
    {
        // ✅ HABILITAR BUFFERING antes de leer el body
        Request.EnableBuffering();
        
        // Resetear posición del stream
        Request.Body.Position = 0;
        
        var json = await new StreamReader(Request.Body).ReadToEndAsync();
        var signatureHeader = Request.Headers["Stripe-Signature"];
        
        // ... resto del código
    }
    catch (Exception ex)
    {
        // ... manejo de errores
    }
}
```

### **2. Configurar EnableBuffering Globalmente (Recomendado)**

En `Program.cs`, agregar middleware global para webhooks:

```csharp
// Antes de app.UseAuthentication()
app.Use(async (context, next) =>
{
    // Habilitar buffering solo para rutas de webhook
    if (context.Request.Path.StartsWithSegments("/api/Subscription/webhook") ||
        context.Request.Path.StartsWithSegments("/api/Subscription/webhook-general"))
    {
        context.Request.EnableBuffering();
    }
    await next();
});
```

### **3. Validación de Timestamp (Opcional pero Recomendado)**

Aunque `EventUtility.ConstructEvent()` ya valida el timestamp, puedes agregar logging adicional:

```csharp
// Después de construir el evento
_logger.LogInformation("✅ Webhook timestamp: {Timestamp}, Event created at: {Created}", 
    stripeEvent.Created, DateTime.UtcNow);
```

### **4. Logging Mejorado (Buenas Prácticas)**

```csharp
_logger.LogInformation("🔐 Webhook verification: eventId={EventId}, type={Type}, timestamp={Timestamp}", 
    stripeEvent.Id, stripeEvent.Type, stripeEvent.Created);
```

---

## 📊 **FLUJO COMPLETO DE VERIFICACIÓN**

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Stripe envía webhook POST                                │
│    Headers: Stripe-Signature: t=1234567890,v1=abc123...     │
│    Body: JSON payload del evento                            │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ 2. Tu servidor recibe la petición                           │
│    ✅ [MEJORA] Request.EnableBuffering()                    │
│    ✅ Lee Request.Body completo                             │
│    ✅ Extrae Stripe-Signature header                        │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ 3. Validaciones preliminares                                │
│    ✅ Verificar que webhook secret existe                   │
│    ✅ Verificar que signature header existe                 │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ 4. EventUtility.ConstructEvent()                            │
│    ✅ Calcula HMAC SHA-256 con secret                      │
│    ✅ Compara con firma recibida                            │
│    ✅ Verifica timestamp (replay protection)                 │
│    ✅ Construye objeto Event                                │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ 5. Verificación de Idempotencia                             │
│    ✅ Buscar evento en ProcessedWebhookEvents               │
│    ✅ Si existe, retornar Ok() sin procesar                 │
│    ✅ Si no existe, continuar                               │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ 6. Procesamiento del Evento                                 │
│    ✅ Procesar según tipo de evento                         │
│    ✅ Ejecutar lógica de negocio                           │
└─────────────────────────────────────────────────────────────┘
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ 7. Marcar como Procesado                                    │
│    ✅ Guardar en ProcessedWebhookEvents                    │
│    ✅ Retornar 200 OK                                       │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔍 **VERIFICACIÓN DE SEGURIDAD DE PAGOS**

### **1. Verificación de Tarjeta (CVC)**
Stripe realiza automáticamente la verificación CVC cuando procesas un pago:
- ✅ Se solicita el CVC en Checkout
- ✅ Stripe lo valida con el emisor
- ✅ Si falla, el pago se rechaza automáticamente

**Tu código ya lo maneja correctamente:**
```csharp
// Stripe maneja esto automáticamente en Checkout
var session = await serviceStripe.CreateAsync(options);
```

### **2. Verificación de Dirección (AVS)**
Stripe también valida AVS automáticamente:
- ✅ Compara dirección de facturación con registros del emisor
- ✅ Si hay discrepancias, puede rechazar el pago

### **3. Stripe Radar (Detección de Fraude)**
Stripe Radar analiza automáticamente cada transacción:
- ✅ Velocidad de transacciones
- ✅ Patrones de comportamiento
- ✅ Lista negra de tarjetas
- ✅ Machine learning para detectar fraude

**Configuración recomendada:**
- Activar Stripe Radar en el dashboard de Stripe
- Configurar reglas personalizadas según tu negocio
- Revisar alertas regularmente

---

## 📝 **CHECKLIST DE SEGURIDAD**

### **Verificación de Webhooks**
- [x] ✅ Uso de `EventUtility.ConstructEvent()`
- [x] ✅ Validación de secretos antes de usar
- [x] ✅ Validación de headers
- [x] ✅ Idempotencia implementada
- [ ] ⚠️ **FALTA**: `Request.EnableBuffering()` antes de leer body
- [x] ✅ Manejo de errores adecuado
- [x] ✅ Logging de eventos

### **Verificación de Pagos**
- [x] ✅ Stripe maneja CVC automáticamente
- [x] ✅ Stripe maneja AVS automáticamente
- [x] ✅ Stripe Radar activo por defecto
- [ ] 💡 **RECOMENDADO**: Revisar dashboard de Stripe regularmente
- [ ] 💡 **RECOMENDADO**: Configurar reglas personalizadas en Radar

---

## 🚀 **IMPLEMENTACIÓN DE MEJORAS**

Voy a crear los cambios necesarios para aplicar las mejoras críticas:

1. **Agregar `Request.EnableBuffering()`** en ambos webhooks
2. **Agregar middleware global** para webhooks (opcional pero recomendado)
3. **Mejorar logging** para auditoría

---

## 📚 **REFERENCIAS**

- [Stripe Webhook Security](https://stripe.com/docs/webhooks/signatures)
- [Stripe Event Verification](https://docs.stripe.com/webhooks/verify)
- [ASP.NET Core Request Body](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-response)
- [Stripe Payment Verification](https://docs.stripe.com/disputes/prevention/verification)

---

*Documento creado: 2025-01-20*  
*Última actualización: 2025-01-20*  
*Prioridad: CRÍTICA - Aplicar EnableBuffering inmediatamente*
