# 🌍 Sistema de Impuestos Internacionales - Explicación Completa

## 🎯 Respuesta Directa a tus Preguntas

### ❓ **¿Cómo se decide cuántos impuestos son?**
**Respuesta:** Stripe Tax calcula automáticamente los impuestos según la **ubicación del COMPRADOR (cliente)**, NO del experto.

### ❓ **¿Es según la ubicación del experto?**
**Respuesta:** ❌ **NO**. Los impuestos se calculan según la **ubicación del CLIENTE** que está pagando.

### ❓ **¿En qué momento exacto se decide?**
**Respuesta:** Se decide en **2 momentos clave**:
1. **Al crear la Checkout Session** (cuando el cliente inicia el pago) - Stripe estima el tax
2. **Cuando el cliente completa el pago** (en Stripe Checkout) - Stripe calcula el tax final basado en la información real del cliente

---

## 📋 Flujo Completo del Sistema

### 1️⃣ **Experto Establece Precio** (Independiente de su ubicación)

El experto establece un precio **con IVA incluido** en su servicio:
- Ejemplo: Experto en España pone **€110** (ya incluye IVA 21%)
- Ejemplo: Experto en México pone **$1,100 MXN** (ya incluye IVA 16%)

**⚠️ IMPORTANTE:** El experto NO necesita saber qué impuesto aplicará. Solo pone su precio final.

---

### 2️⃣ **Cliente Inicia Pago** (Momento 1: Estimación)

Cuando el cliente quiere contratar el servicio, se crea una **Checkout Session de Stripe**:

```csharp
// SubscriptionController.cs - Línea ~1410
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
    // ✅ STRIPE TAX: Habilitar cálculo automático de tax
    AutomaticTax = new SessionAutomaticTaxOptions
    {
        Enabled = true // ✅ Stripe calcula el tax automáticamente
    }
};
```

**¿Qué hace Stripe en este momento?**
- Detecta la **ubicación del cliente** usando:
  - IP del cliente
  - Dirección de facturación (si ya la proporcionó)
  - Configuración de la cuenta Stripe
- **Estima** el impuesto aplicable según la jurisdicción del cliente
- Muestra al cliente el precio final (que ya incluye el tax estimado)

**Ejemplo:**
- Cliente en España: Ve €110 (IVA 21% ya incluido)
- Cliente en México: Ve €110, pero Stripe calculará el tax mexicano si aplica
- Cliente en EE.UU.: Ve €110, pero Stripe calculará sales tax según el estado

---

### 3️⃣ **Cliente Completa el Pago** (Momento 2: Cálculo Final)

El cliente completa el pago en Stripe Checkout:
- Proporciona su **dirección de facturación completa**
- Stripe **calcula el tax final** basado en:
  - País del cliente
  - Estado/Provincia del cliente (si aplica)
  - Código postal del cliente
  - Tipo de servicio (B2B vs B2C)
  - Número de VAT/IVA del cliente (si es B2B)

**Ejemplos de Cálculo:**

#### **Escenario 1: Cliente en España, Experto en España**
```
Precio: €110 (IVA 21% incluido)
Stripe calcula:
- Base: €90.91 (110 / 1.21)
- IVA: €19.09 (21% de €90.91)
- Total: €110 ✅
```

#### **Escenario 2: Cliente en México, Experto en España**
```
Precio: €110 (IVA 21% incluido)
Stripe calcula según jurisdicción mexicana:
- Base: €94.83 (110 / 1.16) - IVA mexicano 16%
- IVA: €15.17 (16% de €94.83)
- Total: €110 ✅
```

#### **Escenario 3: Cliente en EE.UU. (California), Experto en España**
```
Precio: €110 (IVA 21% incluido)
Stripe calcula según California:
- Base: €105.77 (110 / 1.04) - Sales tax CA ~4%
- Tax: €4.23 (4% de €105.77)
- Total: €110 ✅
```

#### **Escenario 4: Cliente B2B con VAT ID válido (Reverse Charge)**
```
Precio: €110
Stripe detecta VAT ID válido:
- Base: €110 (no hay tax aplicado - reverse charge)
- Tax: €0 (el cliente se encarga del IVA)
- Total: €110 ✅
```

---

### 4️⃣ **Webhook Recibe Confirmación** (Obtención del Tax Breakdown)

Cuando Stripe confirma el pago, se ejecuta el webhook `HandlePendingHireCompleted`:

```csharp
// SubscriptionController.cs - Línea ~3303
// ✅ STRIPE TAX: Obtener tax breakdown de la Checkout Session
var sessionService = new SessionService();
var sessionWithTax = await sessionService.GetAsync(session.Id, new SessionGetOptions 
{ 
    Expand = new List<string> { "total_details.breakdown" } 
});

// Extrae los valores (están en centavos, dividir por 100)
totalAmount = sessionWithTax.AmountTotal.Value / 100m;        // €110
taxAmount = (sessionWithTax.TotalDetails?.AmountTax ?? 0) / 100m; // €19.09 (o €15.17, o €4.23, etc.)
baseAmount = totalAmount - taxAmount;                         // €90.91 (o €94.83, o €105.77, etc.)
```

**¿Qué información se guarda?**

```csharp
searchHire = new SearchHire
{
    Amount = totalAmount,      // €110 (total con tax)
    BaseAmount = baseAmount,   // €90.91 (base sin tax) ✅
    TaxAmount = taxAmount,     // €19.09 (tax calculado) ✅
    
    // ✅ INTERNACIONALIZACIÓN: Snapshot del experto (NO se usa para calcular tax)
    ExpertTimezone = expertProfile?.Timezone ?? "UTC",  // "Europe/Madrid"
    ExpertCountry = expertProfile?.Country,              // "ES"
    // ...
};
```

**⚠️ IMPORTANTE:** 
- `ExpertCountry` y `ExpertTimezone` se guardan solo como **snapshot** para referencia
- **NO se usan** para calcular impuestos
- Los impuestos se calculan según la **ubicación del cliente**

---

### 5️⃣ **Distribución de Dinero** (Usando BaseAmount)

Cuando el servicio se completa/cancela, se distribuye el dinero usando **BaseAmount** (no Amount):

```csharp
// RefundService.cs - Línea ~250
// ✅ Calcular sobre BASE PRE-TAX (sin tax)
var baseAmount = searchHire.BaseAmount ?? searchHire.Amount; // Fallback para datos antiguos

// Aplicar porcentajes sobre el base
var clientRefundAmount = baseAmount * (config.ClientPercentage / 100);  // 10% de €90.91 = €9.09
var expertAmount = baseAmount * (config.ExpertPercentage / 100);         // 80% de €90.91 = €72.73
var platformAmount = baseAmount * (config.PlatformPercentage / 100);     // 10% de €90.91 = €9.09
```

**Ejemplo con números (Cliente en España, Experto en España):**
- BaseAmount: €90.91 (sin IVA)
- Cliente recibe: €9.09 (10% de €90.91) ✅
- Experto recibe: €72.73 (80% de €90.91) ✅
- Plataforma recibe: €9.09 (10% de €90.91) ✅
- **IVA separado:** €19.09 (se remite a autoridades fiscales)

**Ejemplo con números (Cliente en México, Experto en España):**
- BaseAmount: €94.83 (sin tax mexicano)
- Cliente recibe: €9.48 (10% de €94.83) ✅
- Experto recibe: €75.86 (80% de €94.83) ✅
- Plataforma recibe: €9.48 (10% de €94.83) ✅
- **Tax separado:** €15.17 (se remite a autoridades fiscales mexicanas)

---

## 🌍 Adaptación a Contrataciones Internacionales

### ✅ **Ventajas del Sistema Actual**

1. **Automático:** Stripe Tax calcula automáticamente según la ubicación del cliente
2. **Cumplimiento:** Maneja reglas complejas de diferentes países:
   - IVA en UE (21% España, 19% Alemania, 20% Reino Unido, etc.)
   - Sales Tax en EE.UU. (varía por estado: 0% en algunos, hasta 10% en otros)
   - GST en Australia, India, etc.
   - Reverse Charge para B2B con VAT ID
3. **Sin configuración manual:** No necesitas mantener tablas de tasas de impuestos
4. **Actualización automática:** Stripe actualiza las tasas cuando cambian las leyes fiscales

### 📊 **Cobertura de Stripe Tax**

Stripe Tax cubre **100+ países** con cálculo automático:
- ✅ **UE completa** (todos los países con IVA)
- ✅ **Todos los estados de EE.UU.** (sales tax)
- ✅ **América Latina** (México, Chile, Colombia, Perú, Brasil, etc.)
- ✅ **Asia-Pacífico** (Japón, Australia, Singapur, India, etc.)
- ✅ **Canadá** (GST/HST según provincia)
- ✅ **Reino Unido** (VAT post-Brexit)

### ⚠️ **Limitaciones**

1. **Países no soportados:** Si un cliente está en un país no soportado por Stripe Tax:
   - Stripe puede requerir información adicional (`requires_location_inputs`)
   - O puede no aplicar tax (depende de la configuración)
   - **Solución:** El sistema tiene fallback a usar el precio completo como base

2. **Servicios digitales específicos:** Algunos servicios digitales tienen reglas especiales:
   - **Solución:** Asignar tax codes apropiados en el Dashboard de Stripe

---

## 🔑 Puntos Clave del Sistema

### 1. **Los Impuestos se Calculan Según el Cliente, NO el Experto**

```
❌ INCORRECTO: "El experto está en España, así que aplicamos IVA 21%"
✅ CORRECTO: "El cliente está en España, así que aplicamos IVA 21%"
```

**Razón:** En la mayoría de jurisdicciones, el impuesto se aplica según la ubicación del **consumidor final** (cliente), no del proveedor (experto).

### 2. **Momentos de Decisión**

| Momento | Qué Pasa | Información Disponible |
|---------|----------|------------------------|
| **Creación de Session** | Stripe estima el tax | IP del cliente, configuración básica |
| **Pago en Checkout** | Stripe calcula el tax final | Dirección completa, VAT ID (si B2B), tipo de servicio |
| **Webhook** | Se guarda el tax breakdown | Tax final calculado por Stripe |

### 3. **ExpertCountry y ExpertTimezone NO se Usan para Tax**

Estos campos se guardan solo como **snapshot** para:
- Referencia histórica (si el experto cambia de país después)
- Mostrar información al cliente (ej: "Experto en España")
- Lógica de negocio (ej: mostrar servicios cercanos)

**NO se usan para:**
- ❌ Calcular impuestos
- ❌ Determinar jurisdicción fiscal
- ❌ Aplicar tasas de tax

### 4. **Precios Inclusivos Simplifican Todo**

El experto pone un precio **con tax incluido**, y Stripe hace el **cálculo inverso**:
- Si el precio es €110 con 21% IVA inclusivo
- Stripe calcula: Base = €110 / 1.21 = €90.91
- IVA = €110 - €90.91 = €19.09

Esto funciona **independientemente** de qué tax rate aplique (21% España, 16% México, 4% California, etc.).

---

## 📊 Ejemplos Prácticos

### **Ejemplo 1: Cliente Español, Experto Español**
```
Experto pone precio: €110 (IVA 21% incluido)
Cliente está en: Madrid, España
Stripe calcula:
- Base: €90.91
- IVA: €19.09 (21%)
- Total: €110
```

### **Ejemplo 2: Cliente Alemán, Experto Español**
```
Experto pone precio: €110 (IVA 21% incluido)
Cliente está en: Berlín, Alemania
Stripe calcula:
- Base: €92.44
- IVA: €17.56 (19% alemán)
- Total: €110
```

### **Ejemplo 3: Cliente Mexicano, Experto Español**
```
Experto pone precio: €110 (IVA 21% incluido)
Cliente está en: Ciudad de México, México
Stripe calcula:
- Base: €94.83
- IVA: €15.17 (16% mexicano)
- Total: €110
```

### **Ejemplo 4: Cliente B2B con VAT ID (Reverse Charge)**
```
Experto pone precio: €110
Cliente es empresa con VAT ID válido
Stripe calcula:
- Base: €110
- IVA: €0 (reverse charge - el cliente se encarga)
- Total: €110
```

---

## 🛡️ Manejo de Casos Edge

### 1. **Stripe Tax Requiere Más Información**

```csharp
if (sessionWithTax.AutomaticTax?.Status == "requires_location_inputs")
{
    // Stripe necesita más información de ubicación
    // Fallback: usar precio completo como base
    baseAmount = totalAmount;
    taxAmount = 0;
}
```

**Cuándo pasa:**
- Cliente en país no soportado completamente
- Información de ubicación incompleta
- Reglas fiscales complejas que requieren inputs adicionales

### 2. **TaxAmount = 0 (Exenciones)**

```csharp
// Si AutomaticTax no aplicó (ej. exención B2B), AmountTax será 0
if (sessionWithTax.AutomaticTax?.Status == "requires_location_inputs")
{
    baseAmount = totalAmount;
    taxAmount = 0;
}
```

**Cuándo pasa:**
- B2B con VAT ID válido (reverse charge)
- Ubicación no sujeta a tax
- Exenciones fiscales aplicables

### 3. **Error al Obtener Tax Breakdown**

```csharp
catch (Exception taxEx)
{
    // Si falla obtener tax breakdown, usar precio completo como fallback
    baseAmount = totalAmount;
    taxAmount = 0;
    // Log warning para revisión manual
}
```

**Cuándo pasa:**
- Error de red con Stripe
- Session no encontrada
- Problemas de configuración

---

## ✅ Resumen Final

### **¿Cómo se adapta a contrataciones internacionales?**
✅ **Automáticamente** - Stripe Tax calcula según la ubicación del cliente, sin configuración manual adicional.

### **¿Cómo se decide cuántos impuestos son?**
✅ **Según la ubicación del CLIENTE** - Stripe detecta país, estado/provincia, código postal y calcula el tax aplicable.

### **¿Es según la ubicación del experto?**
❌ **NO** - Los impuestos se calculan según la ubicación del **cliente**, no del experto.

### **¿En qué momento exacto se decide?**
✅ **En 2 momentos:**
1. **Al crear la Checkout Session** - Stripe estima el tax (basado en IP)
2. **Cuando el cliente completa el pago** - Stripe calcula el tax final (basado en dirección completa)

### **¿Qué se guarda del experto?**
✅ **Solo snapshot** (`ExpertCountry`, `ExpertTimezone`) para referencia histórica, NO para calcular impuestos.

---

## 📝 Archivos Clave del Sistema

| Archivo | Función |
|---------|---------|
| `SubscriptionController.cs` (línea ~1410) | Crea Session con `AutomaticTax.Enabled = true` |
| `SubscriptionController.cs` (línea ~3303) | Obtiene tax breakdown de la Session |
| `SubscriptionController.cs` (línea ~3379) | Guarda `BaseAmount` y `TaxAmount` en SearchHire |
| `RefundService.cs` (línea ~250) | Calcula porcentajes sobre `BaseAmount` (no `Amount`) |
| `SearchHire.cs` | Modelo con campos `BaseAmount`, `TaxAmount`, `ExpertCountry`, `ExpertTimezone` |

---

## 🎯 Conclusión

El sistema está **bien adaptado** para contrataciones internacionales porque:

1. ✅ **Automático:** Stripe Tax calcula según ubicación del cliente
2. ✅ **Cumplimiento:** Maneja reglas fiscales de 100+ países
3. ✅ **Sin configuración manual:** No necesitas mantener tablas de tasas
4. ✅ **Actualización automática:** Stripe actualiza cuando cambian las leyes
5. ✅ **Precios inclusivos:** Simplifica para expertos (solo ponen precio final)
6. ✅ **Base pre-tax:** Los porcentajes se calculan correctamente sobre el base sin tax

**El único requisito es tener Stripe Tax activado en tu Dashboard de Stripe.**
