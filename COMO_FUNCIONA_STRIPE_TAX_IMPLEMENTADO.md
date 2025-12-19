# 🧾 Cómo Funciona el Sistema de Stripe Tax Implementado

## 📋 Flujo Completo del Sistema

### 1️⃣ **Experto Establece Precio** (Frontend/Base de Datos)

El experto establece un precio **con IVA incluido** en su servicio:
- Ejemplo: Experto pone **€110** (ya incluye IVA 21%)

---

### 2️⃣ **Cliente Inicia Pago** (`SubscriptionController.LoadMoneyService` o `HireService`)

Cuando el cliente quiere contratar el servicio, se crea una **Checkout Session de Stripe** con:

```csharp
// Línea ~1314 de SubscriptionController.cs
var options = new SessionCreateOptions
{
    LineItems = new List<SessionLineItemOptions>
    {
        new SessionLineItemOptions
        {
            PriceData = new SessionLineItemPriceDataOptions
            {
                Currency = "eur",
                UnitAmount = checked((long)Math.Round(amountToCharge * 100)), // €110 = 11000 centavos
                TaxBehavior = "inclusive" // ✅ El precio YA incluye IVA
            }
        }
    },
    AutomaticTax = new SessionAutomaticTaxOptions
    {
        Enabled = true // ✅ Stripe calcula el tax automáticamente
    }
};
```

**¿Qué hace Stripe?**
- Detecta la ubicación del comprador (IP, dirección de facturación)
- Calcula el IVA/tax aplicable según la jurisdicción
- Hace un **cálculo inverso** (reverse calculation):
  - Si el precio es €110 con 21% IVA inclusivo
  - Base = €110 / 1.21 = **€90.91**
  - IVA = €110 - €90.91 = **€19.09**

---

### 3️⃣ **Cliente Paga** (Stripe Checkout)

El cliente completa el pago en Stripe:
- Paga **€110** (precio final que ve)
- Stripe procesa el pago y calcula el tax automáticamente

---

### 4️⃣ **Webhook Recibe Confirmación** (`SubscriptionController.HandlePendingHireCompleted`)

Cuando Stripe confirma el pago, se ejecuta el webhook que:

#### 4.1 Obtiene el Tax Breakdown de la Session

```csharp
// Línea ~2343 de SubscriptionController.cs
var sessionService = new SessionService();
var sessionWithTax = await sessionService.GetAsync(session.Id, new SessionGetOptions 
{ 
    Expand = new List<string> { "total_details.breakdown" } 
});

// Extrae los valores (están en centavos, dividir por 100)
totalAmount = sessionWithTax.AmountTotal.Value / 100m;        // €110
taxAmount = (sessionWithTax.TotalDetails?.AmountTax ?? 0) / 100m; // €19.09
baseAmount = totalAmount - taxAmount;                         // €90.91
```

**⚠️ IMPORTANTE:** El tax breakdown está en la **Checkout Session**, NO en el PaymentIntent.

#### 4.2 Guarda los Valores en SearchHire

```csharp
// Línea ~2391 de SubscriptionController.cs
searchHire = new SearchHire
{
    Amount = totalAmount,      // €110 (total con IVA)
    BaseAmount = baseAmount,   // €90.91 (base sin IVA) ✅ NUEVO
    TaxAmount = taxAmount,     // €19.09 (IVA calculado) ✅ NUEVO
    // ... otros campos ...
};
```

**Base de Datos:**
```
SearchHires table:
- Amount: 110.00
- BaseAmount: 90.91
- TaxAmount: 19.09
```

---

### 5️⃣ **Distribución de Dinero** (`RefundService.ProcessMoneyDistributionAsync`)

Cuando el servicio se completa/cancela, se distribuye el dinero usando **BaseAmount** (no Amount):

```csharp
// Línea ~250 de RefundService.cs
// ✅ Calcular sobre BASE PRE-TAX (sin IVA)
var baseAmount = searchHire.BaseAmount ?? searchHire.Amount; // Fallback para datos antiguos

// Aplicar porcentajes sobre el base
var clientRefundAmount = baseAmount * (config.ClientPercentage / 100);  // 10% de €90.91 = €9.09
var expertAmount = baseAmount * (config.ExpertPercentage / 100);         // 80% de €90.91 = €72.73
var platformAmount = baseAmount * (config.PlatformPercentage / 100);     // 10% de €90.91 = €9.09
```

**Ejemplo con números:**
- Config: Cliente 10%, Experto 80%, Plataforma 10%
- BaseAmount: €90.91
- Cliente recibe: €9.09 ✅ (no €11)
- Experto recibe: €72.73 ✅ (no €88)
- Plataforma recibe: €9.09 ✅ (no €11)

**Total distribuido:** €9.09 + €72.73 + €9.09 = **€90.91** (sin IVA)
**IVA separado:** €19.09 (se remite a autoridades fiscales)

---

## 🔄 Flujo Visual

```
┌─────────────────────────────────────────────────────────────┐
│ 1. EXPERTO ESTABLECE PRECIO                                 │
│    Precio: €110 (IVA 21% incluido)                          │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. CLIENTE INICIA PAGO                                       │
│    Stripe Checkout Session creada con:                       │
│    - TaxBehavior: "inclusive"                                │
│    - AutomaticTax.Enabled: true                              │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. STRIPE CALCULA TAX AUTOMÁTICAMENTE                       │
│    Basado en ubicación del comprador:                       │
│    - Total: €110                                             │
│    - Base: €90.91 (reverse calc: 110 / 1.21)                │
│    - IVA: €19.09                                             │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. CLIENTE PAGA                                              │
│    Paga €110 en Stripe Checkout                             │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 5. WEBHOOK RECIBE CONFIRMACIÓN                               │
│    HandlePendingHireCompleted:                               │
│    - Obtiene Session con tax breakdown                      │
│    - Extrae: totalAmount, taxAmount, baseAmount              │
│    - Guarda en SearchHire:                                  │
│      * Amount = €110                                        │
│      * BaseAmount = €90.91 ✅                               │
│      * TaxAmount = €19.09 ✅                                 │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 6. DISTRIBUCIÓN DE DINERO (cuando se completa/cancela)      │
│    RefundService.ProcessMoneyDistributionAsync:             │
│    - Usa BaseAmount (€90.91) para calcular %               │
│    - Cliente: 10% de €90.91 = €9.09 ✅                      │
│    - Experto: 80% de €90.91 = €72.73 ✅                     │
│    - Plataforma: 10% de €90.91 = €9.09 ✅                   │
│    - IVA (€19.09) se remite a autoridades fiscales          │
└─────────────────────────────────────────────────────────────┘
```

---

## 📊 Comparación: Antes vs Después

### ❌ ANTES (SIN Stripe Tax)

```
Precio del servicio: €110
Cálculo sobre Amount (€110):
- Cliente (10%): €11 ❌ (incluye IVA)
- Experto (80%): €88 ❌ (incluye IVA)
- Plataforma (10%): €11 ❌ (incluye IVA)
Total: €110
```

**Problema:** Estás cobrando comisión sobre el IVA, que no es revenue real.

### ✅ DESPUÉS (CON Stripe Tax)

```
Precio del servicio: €110 (IVA 21% incluido)
Stripe calcula:
- Base: €90.91
- IVA: €19.09

Cálculo sobre BaseAmount (€90.91):
- Cliente (10%): €9.09 ✅ (sin IVA)
- Experto (80%): €72.73 ✅ (sin IVA)
- Plataforma (10%): €9.09 ✅ (sin IVA)
Total distribuido: €90.91
IVA separado: €19.09 (se remite)
```

**Ventaja:** Solo cobras comisión sobre el valor real del servicio.

---

## 🛡️ Manejo de Errores y Casos Edge

### 1. **Si no se puede obtener tax breakdown**

```csharp
catch (Exception taxEx)
{
    // Fallback: usar precio completo como base
    baseAmount = totalAmount;
    taxAmount = 0;
    // Log warning para revisión manual
}
```

### 2. **Si AutomaticTax requiere más información**

```csharp
if (sessionWithTax.AutomaticTax?.Status == "requires_location_inputs")
{
    // Usar precio completo como fallback
    baseAmount = totalAmount;
    taxAmount = 0;
}
```

### 3. **Datos antiguos sin BaseAmount**

```csharp
// En RefundService.cs
var baseAmount = searchHire.BaseAmount ?? searchHire.Amount; // Fallback

if (searchHire.BaseAmount == null)
{
    // Log warning: se está calculando sobre Amount (puede incluir IVA)
}
```

---

## 🔑 Puntos Clave del Sistema

1. **Precios Inclusivos:** Los expertos ponen precios con IVA incluido (más sencillo)

2. **Cálculo Automático:** Stripe calcula el tax automáticamente según ubicación

3. **Base Pre-Tax:** Los porcentajes se calculan sobre `BaseAmount`, no sobre `Amount`

4. **Compatibilidad:** Si `BaseAmount` es null (datos antiguos), usa `Amount` como fallback

5. **Logging:** Se registran warnings cuando se usa fallback o hay problemas

6. **IVA Separado:** El IVA se guarda en `TaxAmount` y se remite a autoridades fiscales

---

## 📝 Archivos Clave

| Archivo | Función |
|---------|---------|
| `SubscriptionController.cs` | Crea Session con Tax, obtiene tax breakdown, guarda en SearchHire |
| `RefundService.cs` | Calcula porcentajes sobre BaseAmount |
| `SearchHire.cs` | Modelo con campos BaseAmount y TaxAmount |
| Migración `20251212124057_...` | Agrega campos a la base de datos |

---

## ✅ Resumen

**El sistema funciona así:**
1. Experto pone precio con IVA incluido (ej: €110)
2. Stripe calcula automáticamente: Base €90.91 + IVA €19.09
3. Se guarda BaseAmount y TaxAmount en SearchHire
4. Al distribuir dinero, se calcula sobre BaseAmount (€90.91)
5. Los porcentajes siguen iguales, solo cambia la base
6. El IVA se remite a autoridades fiscales

**Resultado:** Comisiones justas (sin cobrar sobre IVA) y cumplimiento fiscal automático.

