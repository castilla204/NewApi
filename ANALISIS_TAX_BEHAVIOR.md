# Análisis de `tax_behavior` en el Sistema

## ¿Qué es `tax_behavior`?

`tax_behavior` es un atributo de Stripe que define **cómo se presenta el impuesto al cliente** en los precios:

### 1. **`"inclusive"`** (Inclusivo)
- El precio **YA incluye** el impuesto
- Ejemplo: Si el precio es €110, ese €110 **ya contiene** el IVA
- Stripe hace un "reverse calculation" (cálculo inverso) para separar:
  - Base: €90.91
  - Tax: €19.09
  - Total: €110.00

### 2. **`"exclusive"`** (Exclusivo)
- El precio **NO incluye** el impuesto
- Ejemplo: Si el precio es €90.91, el impuesto se **añade aparte**
- Stripe calcula el tax y lo suma:
  - Base: €90.91
  - Tax: €19.09
  - Total: €110.00

### 3. **`"automatic"`** (Automático)
- Stripe decide según la moneda:
  - USD, CAD → `exclusive` por defecto
  - EUR, GBP, etc. → `inclusive` por defecto

---

## Estado Actual en tu Código

### ✅ **Configuración Verificada**

He verificado **todos los lugares** donde se crean sesiones de Stripe Checkout y **TODOS tienen `TaxBehavior = "inclusive"`**:

#### 1. **`SubscriptionController.cs` - LoadMoneyService** (línea ~1411)
```csharp
PriceData = new SessionLineItemPriceDataOptions
{
    Currency = "eur",
    UnitAmount = checked((long)Math.Round(request.Amount * 100)),
    ProductData = new SessionLineItemPriceDataProductDataOptions
    {
        Name = "Load Money"
    },
    // ✅ STRIPE TAX: Configurar tax como inclusivo
    TaxBehavior = "inclusive"
}
```

#### 2. **`SubscriptionController.cs` - HireService** (línea ~1613)
```csharp
PriceData = new SessionLineItemPriceDataOptions
{
    Currency = "eur",
    UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
    ProductData = new SessionLineItemPriceDataProductDataOptions
    {
        Name = $"Payment for Service {service.Id}"
    },
    // ✅ STRIPE TAX: Configurar tax como inclusivo (el precio ya incluye IVA)
    TaxBehavior = "inclusive" // Stripe hace reverse calc automático
}
```

#### 3. **`SubscriptionController.cs` - CreateCheckoutSession** (línea ~4279)
```csharp
PriceData = new SessionLineItemPriceDataOptions
{
    Currency = "eur",
    UnitAmount = checked((long)Math.Round(service.Price * 100)),
    ProductData = new SessionLineItemPriceDataProductDataOptions
    {
        Name = $"Payment for Service {service.Id}"
    },
    // ✅ STRIPE TAX: Configurar tax como inclusivo (el precio ya incluye IVA)
    TaxBehavior = "inclusive" // Stripe hace reverse calc automático
}
```

#### 4. **`SearchController.cs` - CreateCheckoutSession** (línea ~537)
```csharp
PriceData = new SessionLineItemPriceDataOptions
{
    Currency = "eur",
    UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
    ProductData = new SessionLineItemPriceDataProductDataOptions
    {
        Name = $"Payment for Service {service.Id}"
    },
    // ✅ STRIPE TAX: Configurar tax como inclusivo
    TaxBehavior = "inclusive"
}
```

---

## ¿Por qué `"inclusive"` es Correcto para tu Sistema?

### ✅ **Razones:**

1. **Modelo de Negocio Europeo**
   - En la UE, los precios mostrados al cliente **deben incluir IVA** (por ley)
   - `"inclusive"` es el estándar para EUR

2. **Consistencia con tu Lógica**
   - Tu código en `RefundService.cs` asume que `Amount` incluye tax
   - Calculas `BaseAmount = Amount - TaxAmount`
   - Esto es **correcto** con `tax_behavior: "inclusive"`

3. **Stripe Tax Automático**
   - Con `AutomaticTax.Enabled = true` + `TaxBehavior = "inclusive"`:
     - Stripe calcula el tax automáticamente
     - Hace "reverse calculation" para separar base y tax
     - Proporciona `AmountTotal` (con tax) y `TotalDetails.AmountTax`

---

## Implicaciones para tu Sistema

### ✅ **Lo que está BIEN:**

1. **Refunds Parciales**
   ```csharp
   // ✅ CORRECTO: Mantiene proporción de tax
   clientRefundAmountForStripe = searchHire.Amount * (config.ClientPercentage / 100);
   ```
   - Si el cliente pagó €110 (€90.91 base + €19.09 tax)
   - Y devuelves 10%, devuelves €11 (que incluye proporción de tax)

2. **Transfers al Experto**
   ```csharp
   // ✅ CORRECTO: Usa monto base (sin tax)
   expertAmountForStripe = expertAmountBase;
   ```
   - El experto recibe €72.73 (80% de €90.91 base)
   - **NO** recibe el tax, porque se remite a autoridades fiscales

3. **Cálculo de BaseAmount**
   ```csharp
   var baseAmount = searchHire.BaseAmount ?? searchHire.Amount;
   ```
   - Si `BaseAmount` está guardado, lo usa
   - Si no, usa `Amount` como fallback (correcto para datos antiguos)

---

## Verificación de Configuración

### ✅ **Checklist Completo:**

| Aspecto | Estado | Ubicación |
|---------|--------|-----------|
| `TaxBehavior = "inclusive"` en LoadMoney | ✅ | `SubscriptionController.cs:1411` |
| `TaxBehavior = "inclusive"` en HireService | ✅ | `SubscriptionController.cs:1613` |
| `TaxBehavior = "inclusive"` en CreateCheckoutSession | ✅ | `SubscriptionController.cs:4279` |
| `TaxBehavior = "inclusive"` en SearchController | ✅ | `SearchController.cs:537` |
| `AutomaticTax.Enabled = true` | ✅ | Todos los lugares |
| Lógica de refunds usa proporción de tax | ✅ | `RefundService.cs:271` |
| Lógica de transfers usa base (sin tax) | ✅ | `RefundService.cs:283` |
| Guardado de `BaseAmount` y `TaxAmount` | ✅ | `SubscriptionController.cs:3324+` |

---

## ⚠️ **Puntos de Atención (No Críticos)**

### 1. **Precios Antiguos**
- Si tienes precios creados **antes** de configurar `tax_behavior`, pueden tener comportamiento diferente
- **Solución**: Los precios nuevos siempre tienen `TaxBehavior = "inclusive"` ✅

### 2. **Datos Antiguos en BD**
- Si hay `SearchHire` con `BaseAmount = null`, el código usa `Amount` como fallback
- **Solución**: El código maneja esto correctamente con fallback ✅

### 3. **Monedas Diferentes**
- Si en el futuro usas otras monedas (USD, CAD), considera si deben ser `exclusive`
- **Solución Actual**: Solo EUR, `inclusive` es correcto ✅

---

## Conclusión

### ✅ **Tu Configuración es 100% Correcta**

1. **Todos los lugares** donde se crean sesiones de Stripe tienen `TaxBehavior = "inclusive"` ✅
2. **La lógica de refunds** mantiene proporción de tax correctamente ✅
3. **La lógica de transfers** usa monto base (sin tax) correctamente ✅
4. **El guardado de datos** separa `BaseAmount` y `TaxAmount` correctamente ✅
5. **Stripe Tax automático** está habilitado en todos los lugares ✅

### 🎯 **No se Requieren Cambios**

Tu sistema está **perfectamente configurado** según las mejores prácticas de Stripe 2026 para:
- Precios inclusivos (EUR)
- Marketplaces con tax liability
- Refunds proporcionales
- Transfers a cuentas conectadas

---

## Referencias

- [Stripe Tax Behavior Documentation](https://docs.stripe.com/tax/products-prices-tax-codes-tax-behavior)
- [Stripe Automatic Tax](https://docs.stripe.com/payments/advanced/tax)
- [Stripe Connect Tax](https://docs.stripe.com/tax/connect)
