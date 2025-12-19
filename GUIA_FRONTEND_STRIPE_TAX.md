# 📱 Guía Frontend: Cambios en DTOs por Stripe Tax

## 🎯 Resumen Ejecutivo

**¿Qué cambió?**
- Se agregaron 3 campos nuevos a los DTOs relacionados con contrataciones (`SearchHire`)
- Todos los precios de servicios ahora **incluyen IVA** (precios inclusivos)
- El backend calcula automáticamente el IVA usando Stripe Tax

**¿Qué debes hacer?**
- Actualizar tus interfaces TypeScript/TypeScript para incluir los nuevos campos
- Mostrar precios con IVA incluido claramente
- Manejar casos donde `BaseAmount` es `null` (datos antiguos)

---

## 📋 DTOs Modificados

### 1. `SearchHireDto`

**Campos nuevos agregados:**

```typescript
interface SearchHireDto {
  // ... campos existentes ...
  
  /**
   * Monto total pagado (con IVA incluido).
   * Este es el precio final que pagó el cliente.
   * Ejemplo: €110 (incluye 21% IVA = €19.09)
   */
  Amount?: number;
  
  /**
   * Base amount sin IVA/tax (pre-tax).
   * Se calcula desde Stripe Tax breakdown.
   * Si es null, significa que es un dato antiguo o no hay tax calculado.
   * En ese caso, usar Amount como fallback.
   * Ejemplo: €90.91 (base sin IVA)
   */
  BaseAmount?: number;
  
  /**
   * Monto de IVA/tax calculado por Stripe Tax.
   * Si es null o 0, no hay tax aplicado.
   * Ejemplo: €19.09 (IVA del 21%)
   */
  TaxAmount?: number;
}
```

### 2. `SearchHireResponseDto`

**Campos nuevos agregados:**

```typescript
interface SearchHireResponseDto {
  // ... campos existentes ...
  
  /**
   * Monto total pagado (con IVA incluido).
   * Este es el precio final que pagó el cliente.
   */
  Amount: number;
  
  /**
   * Base amount sin IVA/tax (pre-tax).
   * Si es null, usar Amount como fallback (datos antiguos).
   */
  BaseAmount?: number;
  
  /**
   * Monto de IVA/tax calculado por Stripe Tax.
   * Si es null o 0, no hay tax aplicado.
   */
  TaxAmount?: number;
}
```

### 3. `SearchServiceDto` / `SearchServiceResponseDto`

**⚠️ IMPORTANTE: El campo `Price` NO cambió, pero su significado sí:**

```typescript
interface SearchServiceResponseDto {
  // ... campos existentes ...
  
  /**
   * Precio del servicio CON IVA incluido.
   * Este es el precio final que pagará el cliente.
   * Stripe calculará automáticamente el IVA según la ubicación del comprador.
   * 
   * Ejemplo: Si el experto establece Price = 110, el cliente pagará €110.
   * Stripe calculará automáticamente: Base = €90.91, IVA = €19.09
   */
  Price: number;
}
```

**✅ NO hay cambios en la estructura**, solo en la documentación. El precio ya incluye IVA.

---

## 🔍 Cómo Leer los Precios

### Escenario 1: Contratación Nueva (con Stripe Tax)

```typescript
const searchHire: SearchHireResponseDto = {
  Amount: 110,        // Total con IVA (€110)
  BaseAmount: 90.91,  // Base sin IVA (€90.91)
  TaxAmount: 19.09    // IVA (€19.09)
};

// ✅ Verificación: Amount = BaseAmount + TaxAmount
// 110 = 90.91 + 19.09 ✅
```

**Cómo mostrar en UI:**
```typescript
// Mostrar precio total
const displayPrice = searchHire.Amount; // €110

// Mostrar desglose (opcional)
const basePrice = searchHire.BaseAmount; // €90.91
const tax = searchHire.TaxAmount;        // €19.09
```

### Escenario 2: Contratación Antigua (sin BaseAmount)

```typescript
const searchHire: SearchHireResponseDto = {
  Amount: 110,        // Total (puede incluir IVA o no)
  BaseAmount: null,    // ❌ No hay información de tax
  TaxAmount: null      // ❌ No hay información de tax
};
```

**Cómo manejar:**
```typescript
// ✅ Usar Amount como fallback
const displayPrice = searchHire.Amount; // €110

// ⚠️ No mostrar desglose de tax (no hay información)
// No mostrar "IVA incluido" porque no sabemos si lo incluye
```

### Escenario 3: Servicio (Listado de Servicios)

```typescript
const service: SearchServiceResponseDto = {
  Price: 110  // Precio CON IVA incluido
};

// ✅ Mostrar directamente
const displayPrice = service.Price; // €110

// ✅ Mostrar "IVA incluido" o "Precio final"
// El IVA se calculará automáticamente por Stripe según el comprador
```

---

## 💻 Ejemplos de Código Frontend

### TypeScript Interfaces

```typescript
// Actualizar tus interfaces existentes
interface SearchHireDto {
  id: number;
  expertId?: number;
  status: string;
  statusTranslated: string;
  createdAt: string;
  amount?: number;        // ✅ NUEVO
  baseAmount?: number;    // ✅ NUEVO
  taxAmount?: number;     // ✅ NUEVO
  expert?: UserDto;
  service?: ServiceInfo;
  statusInfo?: SystemStatusDto;
  expertTimezone?: string;
  expertCountry?: string;
}

interface SearchHireResponseDto {
  id: number;
  clientId?: number;
  expertId?: number;
  searchServiceId: number;
  status: string;
  statusTranslated: string;
  expertTransferId?: string;
  amount: number;         // ✅ NUEVO (required)
  baseAmount?: number;    // ✅ NUEVO (optional)
  taxAmount?: number;     // ✅ NUEVO (optional)
  createdAt: string;
  updatedAt?: string;
  client?: UserDto;
  expert?: UserDto;
  service: SearchServiceResponseDto;
  // ... otros campos ...
}
```

### Función Helper para Mostrar Precios

```typescript
/**
 * Obtiene el precio a mostrar en la UI
 * @param searchHire - Contratación con información de precios
 * @returns Objeto con precio total, base y tax (si disponible)
 */
function getPriceDisplay(searchHire: SearchHireResponseDto | SearchHireDto) {
  const total = searchHire.amount ?? searchHire.Amount ?? 0;
  const base = searchHire.baseAmount ?? searchHire.BaseAmount;
  const tax = searchHire.taxAmount ?? searchHire.TaxAmount;
  
  // Si hay información de tax, mostrar desglose
  const hasTaxInfo = base != null && tax != null && tax > 0;
  
  return {
    total: total,
    base: base ?? total,  // Fallback a total si no hay base
    tax: tax ?? 0,
    hasTaxInfo: hasTaxInfo,
    // Helper para formatear
    formattedTotal: `€${total.toFixed(2)}`,
    formattedBase: base != null ? `€${base.toFixed(2)}` : null,
    formattedTax: tax != null && tax > 0 ? `€${tax.toFixed(2)}` : null
  };
}
```

### Componente React (Ejemplo)

```tsx
interface PriceDisplayProps {
  searchHire: SearchHireResponseDto;
}

function PriceDisplay({ searchHire }: PriceDisplayProps) {
  const priceInfo = getPriceDisplay(searchHire);
  
  return (
    <div className="price-display">
      {/* Precio principal */}
      <div className="price-total">
        <span className="label">Total pagado:</span>
        <span className="amount">{priceInfo.formattedTotal}</span>
      </div>
      
      {/* Desglose de tax (solo si hay información) */}
      {priceInfo.hasTaxInfo && (
        <div className="price-breakdown">
          <div className="breakdown-item">
            <span>Base (sin IVA):</span>
            <span>{priceInfo.formattedBase}</span>
          </div>
          <div className="breakdown-item">
            <span>IVA:</span>
            <span>{priceInfo.formattedTax}</span>
          </div>
        </div>
      )}
      
      {/* Indicador de IVA incluido */}
      {priceInfo.hasTaxInfo && (
        <div className="tax-badge">
          ✅ IVA incluido
        </div>
      )}
    </div>
  );
}
```

### Componente para Listado de Servicios

```tsx
interface ServiceCardProps {
  service: SearchServiceResponseDto;
}

function ServiceCard({ service }: ServiceCardProps) {
  return (
    <div className="service-card">
      <h3>{service.serviceTypeName}</h3>
      
      {/* Precio con IVA incluido */}
      <div className="service-price">
        <span className="price">€{service.price.toFixed(2)}</span>
        <span className="tax-info">IVA incluido</span>
      </div>
      
      {/* Nota: El IVA se calculará automáticamente según la ubicación del comprador */}
      <p className="price-note">
        Precio final. El IVA se calculará automáticamente según tu ubicación.
      </p>
    </div>
  );
}
```

---

## 📊 Tabla de Comparación: Antes vs Después

| Concepto | Antes (SIN Tax) | Después (CON Tax) |
|----------|----------------|-------------------|
| **Precio del Servicio** | `Price: 100` (sin IVA) | `Price: 110` (con IVA incluido) |
| **Contratación - Total** | `Amount: 100` | `Amount: 110` (total con IVA) |
| **Contratación - Base** | ❌ No existía | `BaseAmount: 90.91` (sin IVA) |
| **Contratación - IVA** | ❌ No existía | `TaxAmount: 19.09` (IVA) |
| **Cómo mostrar** | Mostrar `Amount` directamente | Mostrar `Amount` como total, opcionalmente desglosar `BaseAmount` + `TaxAmount` |
| **Datos antiguos** | N/A | `BaseAmount: null`, usar `Amount` como fallback |

---

## ⚠️ Casos Especiales a Manejar

### 1. BaseAmount es null (Datos Antiguos)

```typescript
if (searchHire.baseAmount == null) {
  // ⚠️ Dato antiguo, no hay información de tax
  // Mostrar solo el total, sin desglose
  return {
    display: `€${searchHire.amount.toFixed(2)}`,
    showTaxBreakdown: false
  };
}
```

### 2. TaxAmount es 0 o null (Sin Tax)

```typescript
if (searchHire.taxAmount == null || searchHire.taxAmount === 0) {
  // No hay tax aplicado (ej: B2B con VAT ID, o país sin tax)
  return {
    display: `€${searchHire.amount.toFixed(2)}`,
    showTaxBreakdown: false,
    note: "Sin impuestos aplicables"
  };
}
```

### 3. Verificación de Consistencia

```typescript
function validatePriceConsistency(searchHire: SearchHireResponseDto): boolean {
  if (searchHire.baseAmount == null || searchHire.taxAmount == null) {
    return true; // Datos antiguos, no validar
  }
  
  const calculatedTotal = searchHire.baseAmount + searchHire.taxAmount;
  const difference = Math.abs(calculatedTotal - searchHire.amount);
  
  // Permitir pequeñas diferencias por redondeo (ej: 0.01€)
  return difference < 0.02;
}
```

---

## 🎨 Recomendaciones de UI/UX

### 1. Mostrar Precio Total Prominente

```tsx
// ✅ CORRECTO: Precio grande y claro
<div className="price-main">
  <span className="currency">€</span>
  <span className="amount">{price.toFixed(2)}</span>
  <span className="tax-label">IVA incluido</span>
</div>
```

### 2. Desglose Opcional (Colapsable)

```tsx
// ✅ CORRECTO: Desglose opcional, no intrusivo
<details className="price-breakdown">
  <summary>Ver desglose</summary>
  <div>
    <p>Base (sin IVA): €{baseAmount.toFixed(2)}</p>
    <p>IVA: €{taxAmount.toFixed(2)}</p>
    <p>Total: €{amount.toFixed(2)}</p>
  </div>
</details>
```

### 3. Indicador Visual de IVA Incluido

```tsx
// ✅ CORRECTO: Badge pequeño y discreto
<span className="tax-badge">
  ✅ IVA incluido
</span>
```

### 4. No Mostrar "Sin IVA" si no hay información

```tsx
// ❌ INCORRECTO: No asumir que no hay IVA
{!hasTaxInfo && <span>Sin IVA</span>}

// ✅ CORRECTO: No mostrar nada o mostrar solo el total
{hasTaxInfo && <span>IVA incluido</span>}
```

---

## 🔄 Migración de Código Existente

### Paso 1: Actualizar Interfaces TypeScript

```typescript
// Buscar todas las definiciones de SearchHireDto y SearchHireResponseDto
// Agregar los 3 campos nuevos (Amount, BaseAmount, TaxAmount)
```

### Paso 2: Actualizar Componentes que Muestran Precios

```typescript
// Buscar: searchHire.amount o searchHire.Amount
// Reemplazar con: getPriceDisplay(searchHire).total

// Buscar: service.price
// Verificar que se muestre como "IVA incluido"
```

### Paso 3: Agregar Validaciones

```typescript
// Agregar validación para casos donde BaseAmount es null
// Agregar validación para verificar consistencia de precios
```

### Paso 4: Testing

```typescript
// Probar con:
// 1. Contrataciones nuevas (con BaseAmount y TaxAmount)
// 2. Contrataciones antiguas (sin BaseAmount y TaxAmount)
// 3. Servicios (solo Price, sin desglose)
```

---

## 📝 Checklist de Implementación

- [ ] Actualizar interfaces TypeScript para `SearchHireDto` y `SearchHireResponseDto`
- [ ] Agregar campos `Amount`, `BaseAmount`, `TaxAmount` a las interfaces
- [ ] Crear función helper `getPriceDisplay()` para manejar precios
- [ ] Actualizar componentes que muestran precios de contrataciones
- [ ] Agregar manejo de casos donde `BaseAmount` es `null`
- [ ] Mostrar "IVA incluido" en precios de servicios
- [ ] Agregar desglose opcional de tax (colapsable)
- [ ] Probar con datos nuevos y antiguos
- [ ] Validar consistencia de precios (BaseAmount + TaxAmount = Amount)
- [ ] Actualizar documentación interna del frontend

---

## 🆘 Preguntas Frecuentes

### ¿Qué pasa si BaseAmount es null?

**Respuesta:** Es un dato antiguo (antes de implementar Stripe Tax). Usa `Amount` como fallback y no muestres desglose de tax.

### ¿Debo mostrar el desglose de IVA siempre?

**Respuesta:** No, es opcional. Muestra el precio total prominentemente, y el desglose solo si el usuario lo solicita (colapsable).

### ¿El precio de los servicios cambió?

**Respuesta:** No, la estructura no cambió. Solo el significado: ahora `Price` incluye IVA. Los expertos establecen precios con IVA incluido.

### ¿Cómo sé si hay tax aplicado?

**Respuesta:** Si `TaxAmount` es `null` o `0`, no hay tax. Si `TaxAmount > 0`, hay tax aplicado.

### ¿Debo validar que BaseAmount + TaxAmount = Amount?

**Respuesta:** Sí, pero permite pequeñas diferencias por redondeo (ej: 0.01€). Si la diferencia es mayor, reporta un error.

---

## 📚 Referencias

- [Backend: STRIPE_TAX_IMPLEMENTATION_GUIDE.md](./STRIPE_TAX_IMPLEMENTATION_GUIDE.md)
- [Backend: COMO_FUNCIONA_STRIPE_TAX_IMPLEMENTADO.md](./COMO_FUNCIONA_STRIPE_TAX_IMPLEMENTADO.md)
- [Stripe Tax Documentation](https://stripe.com/docs/tax)

---

## ✅ Resumen Final

**Cambios principales:**
1. ✅ Agregar 3 campos nuevos a interfaces: `Amount`, `BaseAmount`, `TaxAmount`
2. ✅ Todos los precios ahora incluyen IVA
3. ✅ Manejar casos donde `BaseAmount` es `null` (datos antiguos)
4. ✅ Mostrar "IVA incluido" en precios de servicios
5. ✅ Desglose opcional de tax (colapsable)

**No cambia:**
- ❌ Estructura de `SearchServiceResponseDto.Price` (solo el significado)
- ❌ Endpoints de API (solo se agregaron campos a las respuestas)
- ❌ Lógica de negocio (solo se agregó información adicional)

---

**¿Dudas?** Contacta al equipo de backend o revisa la documentación de Stripe Tax.

