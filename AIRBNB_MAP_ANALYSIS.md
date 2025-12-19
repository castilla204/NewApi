# 🗺️ Análisis: Funcionamiento del Mapa de Airbnb

## 📱 Comportamiento Observado

### **Vista Móvil**
1. **Carga Inicial**: 
   - Muestra un servicio por defecto (el primero o el del centro)
   - Carga servicios visibles en el área inicial del mapa
   - Los pines de precio aparecen en el mapa

2. **Al Mover el Mapa**:
   - Se hacen nuevas llamadas API cuando el usuario mueve/zoom el mapa
   - Las llamadas incluyen los **bounds** del mapa visible (northeast, southwest)
   - Se cargan solo los servicios dentro del área visible
   - Hay **debouncing** para evitar demasiadas llamadas

3. **Interacción**:
   - Click en pin de precio → muestra detalles del servicio
   - Un servicio visible a la vez (card inferior)
   - Swipe para cambiar entre servicios

### **Vista Desktop**
1. **Carga Inicial**:
   - Lista de servicios a la izquierda (scrollable)
   - Mapa a la derecha
   - Muestra todos los servicios disponibles

2. **Al Mover el Mapa**:
   - Similar a móvil: nuevas llamadas con bounds
   - La lista se actualiza según el área visible
   - Puedes ver todos los servicios en la lista

3. **Interacción**:
   - Click en pin → muestra detalles en sidebar
   - Click en item de lista → centra mapa y muestra detalles

---

## 🔍 Estrategia de Llamadas API (Inferida)

### **Patrón de Airbnb:**

```typescript
// 1. Carga inicial (sin bounds)
GET /api/searchservice/map-experts?categoryId=X&serviceTypeId=Y

// 2. Al mover el mapa (con bounds)
GET /api/searchservice/map-experts?categoryId=X&serviceTypeId=Y
    &northeastLat=40.5&northeastLng=-3.6
    &southwestLat=40.3&southwestLng=-3.8
    &zoom=12
    &limit=50
```

### **Parámetros Clave:**
- **northeastLat/Lng**: Esquina superior derecha del mapa visible
- **southwestLat/Lng**: Esquina inferior izquierda del mapa visible
- **zoom**: Nivel de zoom (determina cuántos servicios mostrar)
- **limit**: Límite de resultados (paginación implícita)

### **Optimizaciones:**
1. **Debouncing**: Espera 300-500ms después de que el usuario deja de mover el mapa
2. **Clustering**: Agrupa pins cercanos cuando hay muchos servicios
3. **Lazy Loading**: Solo carga servicios visibles, no todos
4. **Cache**: Cachea resultados por área para evitar llamadas repetidas

---

## 🛠️ Cambios Necesarios en el Backend

### **1. Nuevo Endpoint: Búsqueda por Bounds**

```csharp
[HttpGet("map-experts-by-bounds")]
public async Task<IActionResult> GetMapExpertsByBounds(
    [FromQuery] int categoryId,
    [FromQuery] int serviceTypeId,
    [FromQuery] decimal? northeastLat,
    [FromQuery] decimal? northeastLng,
    [FromQuery] decimal? southwestLat,
    [FromQuery] decimal? southwestLng,
    [FromQuery] int? zoom = null,
    [FromQuery] int limit = 100)
```

**Lógica:**
- Si NO hay bounds → devuelve todos los servicios (carga inicial)
- Si HAY bounds → filtra servicios dentro del área visible
- Usa zoom para determinar límite de resultados
- Ordena por distancia al centro del bounds

### **2. Modificar Endpoint Existente (Alternativa)**

Agregar parámetros opcionales a `map-experts`:

```csharp
[HttpGet("map-experts")]
public async Task<IActionResult> GetMapExperts(
    [FromQuery] int categoryId,
    [FromQuery] int serviceTypeId,
    [FromQuery] decimal? northeastLat = null,
    [FromQuery] decimal? northeastLng = null,
    [FromQuery] decimal? southwestLat = null,
    [FromQuery] decimal? southwestLng = null,
    [FromQuery] int? zoom = null,
    [FromQuery] int limit = 100)
```

**Ventajas:**
- Un solo endpoint para todo
- Compatible con código existente (parámetros opcionales)
- Más simple de mantener

---

## 📊 Comparación: Antes vs Después

### **ANTES (Actual)**
```typescript
// Carga TODOS los servicios siempre
GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2
// ❌ Problema: Carga todos los servicios del mundo
// ❌ No optimizado para mapas grandes
// ❌ No se actualiza al mover el mapa
```

### **DESPUÉS (Como Airbnb)**
```typescript
// 1. Carga inicial (sin bounds) - muestra servicio por defecto
GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2

// 2. Al mover mapa (con bounds) - carga solo área visible
GET /api/searchservice/map-experts?categoryId=1&serviceTypeId=2
    &northeastLat=40.5&northeastLng=-3.6
    &southwestLat=40.3&southwestLng=-3.8
    &zoom=12&limit=50
// ✅ Solo carga servicios visibles
// ✅ Optimizado para rendimiento
// ✅ Se actualiza dinámicamente
```

---

## 🎯 Implementación Recomendada

### **Opción 1: Modificar Endpoint Existente (Recomendado)**
- ✅ Mantiene compatibilidad
- ✅ Un solo endpoint
- ✅ Parámetros opcionales

### **Opción 2: Nuevo Endpoint**
- ✅ Separación clara de responsabilidades
- ❌ Duplicación de código
- ❌ Más endpoints que mantener

**Recomendación: Opción 1** - Modificar `map-experts` con parámetros opcionales.

---

## 🔧 Cambios Técnicos Necesarios

1. **Agregar validación de bounds**:
   - Verificar que northeast > southwest
   - Validar rangos de lat/lng (-90 a 90, -180 a 180)

2. **Filtrado por bounds**:
   ```csharp
   if (northeastLat.HasValue && southwestLat.HasValue)
   {
       services = services.Where(ss => 
           decimal.Parse(ss.ExpertProfile.Latitude) >= southwestLat &&
           decimal.Parse(ss.ExpertProfile.Latitude) <= northeastLat &&
           decimal.Parse(ss.ExpertProfile.Longitude) >= southwestLng &&
           decimal.Parse(ss.ExpertProfile.Longitude) <= northeastLng
       );
   }
   ```

3. **Límite basado en zoom**:
   ```csharp
   int maxResults = zoom switch
   {
       >= 15 => 200,  // Zoom alto: más servicios
       >= 12 => 100,  // Zoom medio
       _ => 50        // Zoom bajo: menos servicios
   };
   ```

4. **Ordenamiento por distancia al centro**:
   ```csharp
   var centerLat = (northeastLat + southwestLat) / 2;
   var centerLng = (northeastLng + southwestLng) / 2;
   // Ordenar por distancia al centro
   ```

---

## 📝 Resumen

**Airbnb funciona así:**
1. ✅ Carga inicial sin bounds (todos o área inicial)
2. ✅ Al mover mapa → nueva llamada con bounds
3. ✅ Solo carga servicios visibles
4. ✅ Debouncing en frontend (300-500ms)
5. ✅ Límite de resultados según zoom
6. ✅ Un servicio por defecto al inicio

**Cambios en Backend:**
1. ✅ Agregar parámetros opcionales de bounds a `map-experts`
2. ✅ Filtrar por bounds cuando se proporcionan
3. ✅ Agregar límite y paginación
4. ✅ Ordenar por distancia al centro

