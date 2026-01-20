# 🔄 Migración de Endpoint: Details Complete

## 📋 Resumen del Cambio

Se ha **eliminado** el endpoint `/api/Search/{searchId}/details-complete` y ahora **solo existe** `/api/searchhire/{id}/details-complete`.

## ❌ Endpoint Eliminado

```
GET /api/Search/{searchId}/details-complete
```

**Razón de eliminación:**
- Era redundante con `/api/searchhire/{id}/details-complete`
- Tenía limitaciones: no funcionaba si el `Search` fue eliminado (cliente borró su cuenta)
- El endpoint por `SearchHireId` es más robusto y específico para contrataciones

## ✅ Endpoint a Usar (Único)

```
GET /api/searchhire/{id}/details-complete
```

**Ventajas:**
- ✅ Funciona incluso si el `Search` fue eliminado
- ✅ Más específico para contrataciones (`SearchHire`)
- ✅ Más robusto y confiable
- ✅ Mismo formato de respuesta

## 🔧 Cómo Migrar el Código

### Antes (❌ Eliminado)

```typescript
// ❌ NO USAR - Endpoint eliminado
const response = await fetch(`/api/Search/${searchId}/details-complete`);
const data = await response.json();
```

### Después (✅ Correcto)

```typescript
// ✅ USAR - Endpoint correcto
const response = await fetch(`/api/searchhire/${searchHireId}/details-complete`);
const data = await response.json();
```

## 📝 Ejemplo Completo de Migración

### Antes

```typescript
// ❌ Código antiguo
async function getSearchDetails(searchId: number) {
  const response = await fetch(
    `/api/Search/${searchId}/details-complete`,
    {
      headers: {
        'Authorization': `Bearer ${token}`
      }
    }
  );
  
  if (!response.ok) {
    throw new Error('Failed to fetch search details');
  }
  
  return await response.json();
}

// Uso
const details = await getSearchDetails(86);
```

### Después

```typescript
// ✅ Código nuevo
async function getSearchHireDetails(searchHireId: number) {
  const response = await fetch(
    `/api/searchhire/${searchHireId}/details-complete`,
    {
      headers: {
        'Authorization': `Bearer ${token}`
      }
    }
  );
  
  if (!response.ok) {
    throw new Error('Failed to fetch search hire details');
  }
  
  return await response.json();
}

// Uso
const details = await getSearchHireDetails(58);
```

## 🔍 Obtener el SearchHireId

Si solo tienes el `SearchId`, necesitas obtener el `SearchHireId` primero:

### Opción 1: Desde el objeto Search

```typescript
// Si ya tienes el objeto Search con SearchHire
const searchHireId = search.searchHire?.id;

if (searchHireId) {
  const details = await getSearchHireDetails(searchHireId);
}
```

### Opción 2: Desde el endpoint de búsqueda

```typescript
// Obtener SearchId y luego buscar el SearchHireId
const searchResponse = await fetch(`/api/Search/${searchId}`);
const search = await searchResponse.json();

if (search.searchHire?.id) {
  const details = await getSearchHireDetails(search.searchHire.id);
}
```

### Opción 3: Desde el endpoint /api/Search/all

```typescript
// El endpoint /api/Search/all ya incluye SearchHireId
const response = await fetch('/api/Search/all?page=1&pageSize=20');
const data = await response.json();

data.searches.forEach(search => {
  if (search.searchHire?.id) {
    // Usar search.searchHire.id para obtener detalles completos
    const details = await getSearchHireDetails(search.searchHire.id);
  }
});
```

## 📦 Formato de Respuesta (Sin Cambios)

El formato de respuesta **NO ha cambiado**. Sigue siendo el mismo:

```typescript
interface SearchDetailsCompleteResponseDto {
  search: SearchListDto;
  moneyDistribution: MoneyDistributionConfigDto | null;
  category: CategoryDto | null;
  review: ReviewDto | null;
  appointment: AppointmentDto | null;
  deliverables: DeliverableDto[];
  requiredDeliverableTypes: DeliverableTypeDto[];
  disputes: DisputeDto[];
  expertProfile: ExpertProfileDto | null;
}
```

## ⚠️ Puntos Importantes

1. **El parámetro cambió**: De `searchId` a `searchHireId`
2. **La ruta cambió**: De `/api/Search/` a `/api/searchhire/`
3. **El formato de respuesta es idéntico**: No necesitas cambiar el código que procesa la respuesta
4. **Si no tienes `searchHireId`**: Necesitas obtenerlo primero desde el objeto `Search`

## 🔍 Búsqueda de Referencias en el Código

Busca en tu código frontend:

```bash
# Buscar referencias al endpoint eliminado
grep -r "Search/.*details-complete" .
grep -r "Search.*details-complete" .
grep -r "/api/Search/.*details" .
```

## ✅ Checklist de Migración

- [ ] Buscar todas las referencias a `/api/Search/{id}/details-complete`
- [ ] Reemplazar con `/api/searchhire/{id}/details-complete`
- [ ] Asegurarse de tener `searchHireId` disponible (no solo `searchId`)
- [ ] Actualizar funciones/helpers que usen este endpoint
- [ ] Actualizar tipos TypeScript si es necesario
- [ ] Probar que el endpoint funciona correctamente
- [ ] Verificar que la respuesta se procesa correctamente

## 📞 Soporte

Si tienes dudas sobre cómo obtener el `searchHireId` en tu caso específico, consulta con el equipo de backend.

---

**Fecha de cambio**: 2026-01-20  
**Endpoint eliminado**: `GET /api/Search/{searchId}/details-complete`  
**Endpoint a usar**: `GET /api/searchhire/{id}/details-complete`
