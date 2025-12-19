# 🧾 Guía de Implementación: Stripe Tax con Precios Inclusivos

## 📋 Resumen Ejecutivo

**Respuesta directa a tus preguntas:**
- ✅ **SÍ, los porcentajes siguen siendo los mismos** (ej: 10% plataforma, 80% experto, 10% cliente)
- ✅ **SÍ, el tax se calcula ANTES de aplicar los porcentajes**
- ✅ **Los porcentajes se aplican sobre el BASE PRE-TAX**, no sobre el total con IVA

---

## 🎯 Cómo Funciona Actualmente (SIN Tax)

### Estado Actual del Código

**En `RefundService.cs` (líneas 250-252):**
```csharp
var clientRefundAmount = searchHire.Amount * (config.ClientPercentage / 100);
var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);
```

**Problema:** Se calcula sobre el total (`searchHire.Amount`), que probablemente ya incluye IVA si el experto lo configuró así.

**Ejemplo actual:**
- Precio del servicio: €110 (experto lo puso con IVA incluido)
- Tu comisión (10%): €11 ❌ (estás cobrando sobre el IVA)
- Experto recibe (80%): €88
- Cliente recibe (10%): €11

---

## ✅ Cómo Debe Funcionar (CON Stripe Tax)

### Flujo Correcto con Precios Inclusivos

1. **Experto establece precio:** €110 (IVA 21% incluido)
2. **Stripe Tax calcula automáticamente:**
   - Base pre-tax: €90.91 (€110 / 1.21)
   - IVA: €19.09
3. **Aplicar porcentajes sobre BASE:**
   - Tu comisión (10%): €9.09 ✅ (sobre €90.91)
   - Experto recibe (80%): €72.73 ✅
   - Cliente recibe (10%): €9.09 ✅

**Total verificado:** €9.09 + €72.73 + €9.09 = €90.91 ✅ (sin IVA)
**IVA separado:** €19.09 (se remite a autoridades fiscales)

---

## 🔧 Implementación Técnica

### Paso 1: Configurar Stripe Tax en SessionCreateOptions

**En `SubscriptionController.cs` (línea ~1314):**

```csharp
var options = new SessionCreateOptions
{
    PaymentMethodTypes = new List<string> { "card" },
    LineItems = new List<SessionLineItemOptions>
    {
        new SessionLineItemOptions
        {
            PriceData = new SessionLineItemPriceDataOptions
            {
                Currency = "eur",
                UnitAmount = checked((long)Math.Round(amountToCharge * 100)),
                ProductData = new SessionLineItemPriceDataProductDataOptions
                {
                    Name = $"Payment for Service {service.Id}"
                },
                // ✅ AGREGAR: Configurar tax como inclusivo
                TaxBehavior = "inclusive" // El precio ya incluye IVA, Stripe hace reverse calc automático
            },
            Quantity = 1
        }
    },
    // ✅ AGREGAR: Habilitar cálculo automático de tax basado en ubicación del comprador
    AutomaticTax = new SessionAutomaticTaxOptions
    {
        Enabled = true // ✅ Habilita cálculo auto basado en IP, billing/shipping address
    },
    Mode = "payment",
    // ... resto de opciones ...
};
```

### Paso 2: Obtener el Breakdown del Tax desde Checkout Session

**⚠️ CORRECCIÓN CRÍTICA:** El tax breakdown está en la **Checkout Session**, NO en el PaymentIntent. El PaymentIntent solo tiene el `Amount` total (inclusivo), pero el desglose detallado (`TotalDetails.AmountTax`) está en la Session.

**Después de capturar el pago, obtener el tax breakdown desde la Session:**

```csharp
var sessionService = new SessionService();
var sessionGetOptions = new SessionGetOptions
{
    Expand = new List<string> { "total_details.breakdown" } // ✅ Opcional pero recomendado para breakdown detallado
};
var session = await sessionService.GetAsync(sessionId, sessionGetOptions); // Usa el sessionId del checkout

// Obtener el breakdown de tax (valores en centavos, dividir por 100 para decimal)
var totalAmount = session.AmountTotal.Value / 100m; // Total pagado (€110)
var taxAmount = (session.TotalDetails?.AmountTax ?? 0) / 100m; // IVA (€19.09) ✅ Correcto: maneja nulls apropiadamente
var baseAmount = totalAmount - taxAmount; // Base pre-tax (€90.91)

// ✅ VALIDACIÓN: Si AutomaticTax no aplicó (ej. exención B2B), AmountTax será 0
if (session.AutomaticTax?.Status == "requires_location_inputs")
{
    // Manejar caso donde Stripe necesita más información de ubicación
    // Puede requerir retry o usar dirección de facturación del cliente
}
```

**Nota:** Si necesitas el PaymentIntent (por ej., para confirmar el pago), accede vía `session.PaymentIntent`, pero el tax breakdown está en la Session.

**⚠️ IMPORTANTE:** Si `AutomaticTax.Enabled = false` o el tax no aplica (ej. exención B2B), `AmountTax` será 0. En ese caso, `BaseAmount = TotalAmount`.

### Paso 3: Guardar el Base Amount en SearchHire

**Modificar `SearchHire` para guardar el base pre-tax:**

```csharp
// En SearchHire.cs - AGREGAR campo:
public decimal? BaseAmount { get; set; } // Base sin IVA
public decimal? TaxAmount { get; set; } // IVA calculado
public decimal TotalAmount { get; set; } // Total con IVA (Amount actual)
```

**Al crear el SearchHire (SubscriptionController.cs línea ~2338):**

```csharp
// ✅ Obtener tax breakdown de la Session (después de captura del pago)
var sessionService = new SessionService();
var session = await sessionService.GetAsync(sessionId, new SessionGetOptions 
{ 
    Expand = new List<string> { "total_details.breakdown" } 
});

var totalAmount = session.AmountTotal.Value / 100m;
var taxAmount = (session.TotalDetails?.AmountTax ?? 0) / 100m; // ✅ Correcto: maneja nulls apropiadamente
var baseAmount = totalAmount - taxAmount;

searchHire = new SearchHire
{
    // ... campos existentes ...
    Amount = totalAmount, // Total con IVA (€110)
    BaseAmount = baseAmount, // Base sin IVA (€90.91) ✅ NUEVO
    TaxAmount = taxAmount, // IVA (€19.09) ✅ NUEVO
    // ...
};
```

### Paso 4: Modificar Cálculo de Porcentajes en RefundService

**En `RefundService.cs` (líneas 250-252) - CAMBIAR a:**

```csharp
// ✅ CORRECCIÓN: Calcular sobre BASE PRE-TAX, no sobre total con IVA
var baseAmount = searchHire.BaseAmount ?? searchHire.Amount; // Fallback si no hay BaseAmount

var clientRefundAmount = baseAmount * (config.ClientPercentage / 100);
var expertAmount = baseAmount * (config.ExpertPercentage / 100);
var platformAmount = baseAmount * (config.PlatformPercentage / 100);
```

**Ejemplo con números:**
- `baseAmount` = €90.91 (sin IVA)
- `config.ClientPercentage` = 10%
- `clientRefundAmount` = €9.09 ✅ (no €11)

---

## 📊 Comparación: Antes vs Después

### Escenario: Servicio de €110 con IVA 21% incluido

| Concepto | SIN Tax (Actual) | CON Tax (Correcto) |
|---------|------------------|-------------------|
| **Total pagado** | €110 | €110 |
| **Base pre-tax** | ❌ No calculado | ✅ €90.91 |
| **IVA** | ❌ No separado | ✅ €19.09 |
| **Tu comisión (10%)** | ❌ €11 (sobre total) | ✅ €9.09 (sobre base) |
| **Experto (80%)** | ❌ €88 | ✅ €72.73 |
| **Cliente (10%)** | ❌ €11 | ✅ €9.09 |
| **Total distribuido** | €110 | €90.91 + €19.09 (IVA) |

---

## 🌍 Internacionalización

### Cobertura de Stripe Tax

- ✅ **100+ países** con cálculo automático
- ✅ **Todos los estados de EE.UU.**
- ✅ **UE completa** (IVA)
- ✅ **América Latina** (México, Chile, Colombia, Perú, etc.)
- ✅ **Asia-Pacífico** (Japón, Australia, Singapur, etc.)

### Ventajas

1. **Automático:** Stripe calcula el tax según ubicación del comprador
2. **Cumplimiento:** Maneja reglas complejas (B2B vs B2C, reverse charge, etc.)
3. **Reportes:** Genera reportes fiscales automáticamente
4. **Sin cambios en %:** Tus porcentajes siguen iguales, solo cambia la base

---

## ⚠️ Consideraciones Importantes

### 1. Responsabilidad Fiscal

En marketplaces, **TÚ (la plataforma) eres responsable** de:
- Calcular el IVA
- Recolectarlo del comprador
- Remitirlo a las autoridades fiscales

Stripe Tax facilita esto, pero **debes registrarte** para IVA en las jurisdicciones relevantes.

**Nota:** En la UE, la plataforma actúa como "deemed supplier" para servicios digitales y bienes de bajo valor. En EE.UU., las leyes de "marketplace facilitator" hacen que la plataforma sea responsable del sales tax en la mayoría de estados.

**⚠️ ViDA 2025 (VAT in the Digital Age):** Bajo las nuevas regulaciones ViDA de la UE, los marketplaces ahora son responsables del VAT en ventas B2C de bienes por vendedores con base en la UE desde el 1 de enero de 2025, incluso si estaban bajo los umbrales previos. Stripe Tax soporta estos cambios con reportes digitales mejorados y e-reporting.

### 2. Precios de Expertos

**Recomendación:** Los expertos deben establecer precios **con IVA incluido** para simplificar:
- Experto pone: €110 (ya incluye IVA)
- Cliente paga: €110 (precio final claro)
- Stripe calcula: Base €90.91 + IVA €19.09

### 3. Comisión sobre Tax

**❌ INCORRECTO:** Calcular comisión sobre el total con IVA
- Estarías "cobrando" sobre dinero que no es revenue (es impuesto)
- Podría causar problemas en declaraciones fiscales (sub-reportar revenue)

**✅ CORRECTO:** Calcular comisión sobre el base pre-tax
- Solo cobras sobre el valor real del servicio
- Best practice recomendada por Stripe para marketplaces
- Evita issues fiscales y mantiene equidad

**Nota:** Stripe cobra su propio processing fee (ej. 1.4% + €0.25) sobre el total inclusivo, pero tu `application_fee_amount` en Connect debe calcularse sobre el base pre-tax.

### 4. Costo de Stripe Tax

**Stripe Tax tiene un costo adicional:**
- **0.5% por transacción** (mínimo €0.50)
- Este costo es **adicional** a los processing fees normales de Stripe
- Considera este costo al calcular tus márgenes

**Ejemplo:** Para una transacción de €110:
- Processing fee estándar: ~€1.89 (1.4% + €0.25)
- Stripe Tax fee: €0.55 (0.5% de €110, mínimo €0.50)
- **Total fees:** ~€2.44

**Nota sobre e-invoicing:** En 2025, no hay cambios en los fees de Stripe Tax, pero ViDA añade requisitos de e-invoicing obligatoria para 2030 en la UE. Prepara tu aplicación para integrar facturación electrónica cuando sea requerida.

### 5. Validaciones y Casos Edge

**Casos a manejar:**

1. **AutomaticTax.Status = "requires_location_inputs"**
   - Stripe necesita más información de ubicación del comprador
   - Solución: Usar dirección de facturación del cliente o permitir que el cliente la proporcione

2. **TaxAmount = 0 (exenciones)**
   - B2B con VAT ID válido (reverse charge)
   - Ubicación no sujeta a tax
   - Solución: `BaseAmount = TotalAmount` cuando `TaxAmount = 0`

3. **Países no soportados por Stripe Tax**
   - Stripe Tax no cubre todos los países
   - Solución: Fallback a cálculo manual o restringir mercados

4. **Tax codes no asignados**
   - Si no asignas tax codes a productos, Stripe usa tasas genéricas
   - Solución: Asignar códigos apropiados en Dashboard (ej. `txcd_10000000` para consultoría digital)

---

## 🚀 Plan de Implementación

### Fase 1: Preparación (Sin cambios en código)
1. Activar Stripe Tax en tu Dashboard de Stripe
2. Configurar registros fiscales (país, número de IVA, etc.)
3. Asignar códigos de producto/servicio para tax

### Fase 2: Modificaciones de Código
1. Agregar campos `BaseAmount` y `TaxAmount` a `SearchHire`
2. Modificar `SessionCreateOptions` para incluir `AutomaticTax` y `TaxBehavior: "inclusive"`
3. **✅ CORRECCIÓN:** Obtener tax breakdown de la **Checkout Session** (NO PaymentIntent) después de captura
4. Guardar `BaseAmount` y `TaxAmount` en `SearchHire`
5. Modificar `RefundService.cs` para calcular sobre `BaseAmount`

### Fase 3: Testing
1. Probar en modo test de Stripe
2. **Usar IPs simuladas** (Stripe CLI) para probar diferentes países/regiones
3. Verificar cálculos con diferentes países (ej. España 21% IVA, EE.UU. sales tax por estado)
4. Validar que los porcentajes se aplican correctamente sobre base
5. Probar casos edge: exenciones B2B (VAT ID), ubicaciones no soportadas, etc.

### Fase 4: Migración de Datos Existentes
1. Para `SearchHire` existentes sin `BaseAmount`, usar `Amount` como fallback
2. Opcional: Calcular `BaseAmount` retroactivamente si tienes información de tax

---

## 📝 Ejemplo Completo de Código

### Modificación en RefundService.cs

```csharp
// Línea ~250 - ANTES:
var clientRefundAmount = searchHire.Amount * (config.ClientPercentage / 100);
var expertAmount = searchHire.Amount * (config.ExpertPercentage / 100);
var platformAmount = searchHire.Amount * (config.PlatformPercentage / 100);

// DESPUÉS:
// ✅ Calcular sobre BASE PRE-TAX (sin IVA)
var baseAmount = searchHire.BaseAmount ?? searchHire.Amount; // Fallback para compatibilidad

if (searchHire.BaseAmount == null)
{
    // ⚠️ WARNING: No hay BaseAmount, usando Amount como fallback
    // Esto puede causar que se calcule comisión sobre IVA
    await _loggingService.LogWarningAsync(
        message: "Calculating percentages on total amount (tax may be included)",
        details: $"SearchHire {searchHireId} does not have BaseAmount. Using Amount {searchHire.Amount} as fallback. " +
                $"This may cause commission to be calculated on tax amount.",
        userId: initiatedByUserId ?? searchHire.ClientId,
        source: "StripeRefundService.ProcessMoneyDistributionAsync",
        relatedEntityType: "SearchHire",
        relatedEntityId: searchHireId
    );
}

var clientRefundAmount = baseAmount * (config.ClientPercentage / 100);
var expertAmount = baseAmount * (config.ExpertPercentage / 100);
var platformAmount = baseAmount * (config.PlatformPercentage / 100);

// ✅ Logging mejorado con información de tax
await _loggingService.LogInformationAsync(
    message: "Money distribution calculated on base amount (pre-tax)",
    details: $"BaseAmount: {baseAmount}€, TaxAmount: {searchHire.TaxAmount ?? 0}€, " +
            $"TotalAmount: {searchHire.Amount}€. " +
            $"Distribution: Client {clientRefundAmount}€ ({config.ClientPercentage}%), " +
            $"Expert {expertAmount}€ ({config.ExpertPercentage}%), " +
            $"Platform {platformAmount}€ ({config.PlatformPercentage}%)",
    userId: initiatedByUserId ?? searchHire.ClientId,
    source: "StripeRefundService.ProcessMoneyDistributionAsync",
    relatedEntityType: "SearchHire",
    relatedEntityId: searchHireId
);
```

---

## ✅ Checklist de Implementación

- [ ] Activar Stripe Tax en Dashboard
- [ ] Configurar registros fiscales
- [ ] Agregar campos `BaseAmount` y `TaxAmount` a `SearchHire` (migración)
- [ ] Modificar `SessionCreateOptions` para incluir `AutomaticTax`
- [ ] Obtener tax breakdown de la **Checkout Session** (NO PaymentIntent) después de captura
- [ ] Guardar `BaseAmount` y `TaxAmount` al crear `SearchHire`
- [ ] Modificar `RefundService.cs` para calcular sobre `BaseAmount`
- [ ] Agregar logging para casos sin `BaseAmount`
- [ ] Probar en modo test con diferentes países
- [ ] Validar cálculos con servicios existentes

---

## 📚 Referencias

- [Stripe Tax Documentation](https://stripe.com/docs/tax)
- [Stripe Tax Inclusive Pricing](https://stripe.com/docs/tax/inclusive-pricing)
- [Stripe Checkout Session Object (total_details)](https://stripe.com/docs/api/checkout/sessions/object#checkout_session_object-total_details)
- [Stripe AutomaticTax en Checkout](https://stripe.com/docs/tax/checkout)
- [Stripe Connect Tax](https://stripe.com/docs/connect/tax)
- [Marketplace Tax Responsibilities](https://stripe.com/docs/tax/marketplace)
- [Best Practices para Fees en Marketplaces](https://stripe.com/docs/connect/charges)
- [ViDA (VAT in the Digital Age) Overview](https://stripe.com/docs/tax/vida) - Nueva regulación UE 2025
- [2025 Midyear Sales Tax Updates](https://stripe.com/docs/tax/updates) - Actualizaciones de impuestos 2025

---

## 💡 Resumen Final

**Tus porcentajes NO cambian**, solo cambia la base sobre la que se calculan:
- **Antes:** Porcentajes sobre total (puede incluir IVA)
- **Después:** Porcentajes sobre base pre-tax (sin IVA)

**El tax se calcula ANTES** de aplicar los porcentajes, y tú eres responsable de remitirlo a las autoridades fiscales (Stripe facilita esto con reportes automáticos).

**Es la forma más sencilla** para los expertos (ellos solo ponen el precio final) y cumple con regulaciones internacionales de marketplaces.

