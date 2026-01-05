# 🏷️ Explicación: Etiquetas de Precio en el Mapa

## ✅ SÍ, Funciona Exactamente Como Dices

### **1. Muestra TODAS las etiquetas de precio según donde estés**

**Cómo funciona**:
- El sistema **NO filtra por país**, solo por **coordenadas (bounds)**
- Muestra **TODAS** las etiquetas de precio que están dentro del área visible
- No importa de qué país sean, si están en el viewport, se muestran

**Ejemplo**:
```
Estás en la frontera España-Francia (zoom 10):
┌─────────────────────────────┐
│  🇪🇸 Madrid: €50            │
│  🇪🇸 Barcelona: €60         │
│  🇫🇷 París: €70             │  ← Ve etiquetas de AMBOS países
│  🇫🇷 Lyon: €55               │
└─────────────────────────────┘
```

---

### **2. Al desampliar (zoom out) ves etiquetas de varios países**

**Cómo funciona**:
- Al hacer zoom out, los **bounds se expanden** (área visible más grande)
- Puede incluir **varios países** si el área es suficientemente grande
- Muestra etiquetas de **todos los países** dentro del área visible

**Ejemplo Visual**:

```
Zoom 15 (muy ampliado - Madrid):
┌──────────┐
│ 🏷️€50   │  ← Solo Madrid, España
│ 🏷️€60   │
└──────────┘

Zoom 10 (medio - Centro de Europa):
┌──────────────────────┐
│ 🏷️€50  🏷️€70        │
│ 🇪🇸      🇫🇷          │  ← España y Francia
│ 🏷️€60  🏷️€55        │
└──────────────────────┘

Zoom 5 (muy desampliado - Europa):
┌─────────────────────────────────┐
│ 🏷️€50  🏷️€70  🏷️€80  🏷️€90   │
│ 🇪🇸      🇫🇷      🇩🇪      🇮🇹   │  ← Múltiples países
│ 🏷️€60  🏷️€55  🏷️€65  🏷️€75   │
└─────────────────────────────────┘
```

---

## 🎯 Comportamiento Detallado

### **Con Bounds (Mover/Zoom)**
```typescript
// Usuario en zoom 8, viendo España + Francia + Alemania
GET /api/SearchService/map-markers?
  categoryId=1&
  serviceTypeId=2&
  northeastLat=52.0&    // Norte de Alemania
  northeastLng=10.0&
  southwestLat=40.0&    // Sur de España
  southwestLng=-5.0&
  zoom=8

// Respuesta: TODOS los marcadores en esa área (varios países)
{
  markers: [
    { id: 1, price: 50, latitude: "40.4", longitude: "-3.7" },  // España
    { id: 2, price: 60, latitude: "41.4", longitude: "2.1" },   // España
    { id: 3, price: 70, latitude: "48.9", longitude: "2.3" },  // Francia
    { id: 4, price: 55, latitude: "45.8", longitude: "4.8" },  // Francia
    { id: 5, price: 80, latitude: "52.5", longitude: "13.4" }, // Alemania
    // ... hasta 100 marcadores (límite por zoom bajo)
  ]
}
```

**Resultado**: Ves etiquetas de precio de **España, Francia y Alemania** simultáneamente ✅

---

### **Sin Bounds (Carga Inicial)**
```typescript
// Primera carga sin bounds
GET /api/SearchService/map-markers?
  categoryId=1&
  serviceTypeId=2&
  zoom=5

// Respuesta: Todos los marcadores disponibles (hasta 100)
{
  markers: [
    // Puede incluir marcadores de TODOS los países
    // Limitado solo por zoom (máximo 100 en zoom bajo)
  ]
}
```

**Resultado**: Puedes ver etiquetas de **múltiples países** desde el inicio ✅

---

## 🌍 Ejemplos Reales

### **Ejemplo 1: Usuario en Madrid (Zoom 12)**
```
Área visible: Solo Madrid y alrededores
→ Muestra: Etiquetas de precio solo de Madrid
→ Países: Solo España
```

### **Ejemplo 2: Usuario desamplia (Zoom 8)**
```
Área visible: Toda España + Sur de Francia
→ Muestra: Etiquetas de precio de España Y Francia
→ Países: España + Francia
```

### **Ejemplo 3: Usuario desamplia mucho (Zoom 5)**
```
Área visible: Toda Europa
→ Muestra: Etiquetas de precio de múltiples países
→ Países: España, Francia, Alemania, Italia, etc.
→ Limitado a 100 marcadores máximo (por zoom bajo)
```

---

## 📊 Límites por Zoom

| Zoom | Área Típica | Países Visibles | Límite Marcadores |
|------|-------------|-----------------|-------------------|
| **>= 15** | Barrio/Ciudad | 1 país | 500 máximo |
| **12-14** | Región | 1-2 países | 200 máximo |
| **8-11** | País/Región | 2-3 países | 100 máximo |
| **< 8** | Continente | Múltiples países | 100 máximo |

---

## ✅ Confirmación

### **¿Muestra todas las etiquetas según donde estés?**
**SÍ** ✅
- Muestra **TODAS** las etiquetas de precio dentro del área visible
- No importa de qué país sean
- Solo filtra por coordenadas (bounds), no por país

### **¿Al desampliar ves etiquetas de varios países?**
**SÍ** ✅
- Al hacer zoom out, el área visible se expande
- Puede incluir **varios países** si el área es grande
- Muestra etiquetas de **todos los países** dentro del viewport
- Limitado por zoom (máximo 100 en zoom muy bajo)

---

## 🎨 Visualización

```
Zoom 15 (Ampliado):
┌────────────┐
│  🏷️€50    │  ← Solo un país
│  🏷️€60    │
└────────────┘

Zoom 8 (Desampliado):
┌──────────────────────────────┐
│  🏷️€50    🏷️€70    🏷️€80   │
│  🇪🇸        🇫🇷        🇩🇪     │  ← Varios países
│  🏷️€60    🏷️€55    🏷️€65   │
└──────────────────────────────┘
```

---

## 💡 Puntos Importantes

1. **No filtra por país**: El sistema solo filtra por coordenadas (bounds)
2. **Muestra todo lo visible**: Si el área incluye varios países, los muestra todos
3. **Límite inteligente**: El zoom limita cuántos marcadores se cargan (para no sobrecargar)
4. **Dinámico**: Se actualiza automáticamente al mover/zoom

---

## 🚀 Ventajas de Este Enfoque

✅ **Flexible**: El usuario puede ver servicios de cualquier país  
✅ **Natural**: Funciona como Google Maps/Airbnb  
✅ **Optimizado**: Solo carga lo visible  
✅ **Escalable**: Soporta miles de servicios sin problemas  

---

**Resumen**: SÍ, muestra todas las etiquetas de precio del área visible, y SÍ, al desampliar puedes ver etiquetas de varios países simultáneamente. 🎉




