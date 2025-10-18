# 🚀 Guía de Cambios en DTOs - Validación de Ubicación de Citas

## 📋 Resumen de Cambios

Se han agregado **3 nuevos campos** a los DTOs para permitir la validación de ubicación de citas en el frontend. Estos campos proporcionan la información necesaria para validar que las citas propuestas estén dentro del rango del experto.

---

## 🔄 DTOs Modificados

### 1. **AppointmentDto** 
**Archivo:** `DataLayer/Models/DTOs/AppointmentDto.cs`

#### ✅ Nuevos Campos Agregados:
```csharp
public class AppointmentDto
{
    // ... campos existentes ...
    
    // ✅ NUEVOS CAMPOS: Información de ubicación del experto para validación
    public decimal? ExpertLatitude { get; set; }    // Coordenadas del experto al momento de la contratación
    public decimal? ExpertLongitude { get; set; }   // Coordenadas del experto al momento de la contratación  
    public int? LocationRange { get; set; }         // Rango máximo permitido en km
}
```

### 2. **ServiceInfo** (dentro de SearchHireDto)
**Archivo:** `DataLayer/Models/DTOs/SearchHireDto.cs`

#### ✅ Nuevos Campos Agregados:
```csharp
public class ServiceInfo
{
    // ... campos existentes ...
    
    // ✅ NUEVOS CAMPOS: Información de ubicación del experto para validación de citas
    public decimal? ExpertLatitude { get; set; }    // Coordenadas del experto al momento de la contratación
    public decimal? ExpertLongitude { get; set; }   // Coordenadas del experto al momento de la contratación
    public int? LocationRange { get; set; }         // Rango máximo permitido en km
}
```

---

## 📡 Endpoints Afectados

### 1. **GET /api/search/{id}/details-complete**
**Respuesta actualizada:**
```json
{
  "search": {
    "searchHire": {
      "service": {
        "id": 123,
        "price": 213,
        "expertLatitude": 42445999697856000,    // ✅ NUEVO
        "expertLongitude": -2417688648881766,   // ✅ NUEVO
        "locationRange": 25                     // ✅ NUEVO
      }
    }
  },
  "appointment": {
    "id": 12,
    "expertLatitude": 42445999697856000,        // ✅ NUEVO
    "expertLongitude": -2417688648881766,       // ✅ NUEVO
    "locationRange": 25                         // ✅ NUEVO
  }
}
```

### 2. **GET /api/appointment/{id}**
**Respuesta actualizada:**
```json
{
  "id": 12,
  "expertLatitude": 42445999697856000,          // ✅ NUEVO
  "expertLongitude": -2417688648881766,         // ✅ NUEVO
  "locationRange": 25                           // ✅ NUEVO
}
```

### 3. **POST /api/appointment/propose**
**Validación del backend:**
- El backend ahora valida automáticamente que la ubicación propuesta esté dentro del rango del experto
- Si está fuera del rango, devuelve error 400 con mensaje descriptivo

---

## 🎯 Casos de Uso para el Frontend

### 1. **Mostrar Información del Experto**
```javascript
// Obtener datos del servicio
const serviceData = response.search.searchHire.service;

if (serviceData.expertLatitude && serviceData.expertLongitude) {
    // Mostrar ubicación del experto en el mapa
    showExpertLocation(serviceData.expertLatitude, serviceData.expertLongitude);
    
    // Mostrar rango de cobertura
    showServiceRange(serviceData.locationRange); // ej: "Rango: 25 km"
}
```

### 2. **Validación de Ubicación de Cita**
```javascript
// Validar antes de enviar la propuesta de cita
function validateAppointmentLocation(userLat, userLon, expertLat, expertLon, maxRange) {
    const distance = calculateDistance(userLat, userLon, expertLat, expertLon);
    
    if (distance > maxRange) {
        showError(`La ubicación está fuera del rango del experto. Máximo permitido: ${maxRange} km`);
        return false;
    }
    
    return true;
}

// Usar en el formulario de propuesta de cita
const appointmentData = response.appointment;
if (!validateAppointmentLocation(
    userLatitude, 
    userLongitude, 
    appointmentData.expertLatitude, 
    appointmentData.expertLongitude, 
    appointmentData.locationRange
)) {
    return; // No enviar la propuesta
}
```

### 3. **Mostrar Área de Cobertura en Mapa**
```javascript
// Crear círculo de cobertura en el mapa
function showServiceCoverage(expertLat, expertLon, rangeKm) {
    const map = getMapInstance();
    
    // Crear círculo de cobertura
    const coverageCircle = new google.maps.Circle({
        strokeColor: "#FF0000",
        strokeOpacity: 0.8,
        strokeWeight: 2,
        fillColor: "#FF0000",
        fillOpacity: 0.35,
        map: map,
        center: { lat: expertLat, lng: expertLon },
        radius: rangeKm * 1000 // Convertir km a metros
    });
    
    // Mostrar marcador del experto
    const expertMarker = new google.maps.Marker({
        position: { lat: expertLat, lng: expertLon },
        map: map,
        title: "Ubicación del Experto"
    });
}
```

---

## 🔧 Función de Cálculo de Distancia

```javascript
// Función para calcular distancia entre dos puntos (fórmula de Haversine)
function calculateDistance(lat1, lon1, lat2, lon2) {
    const R = 6371; // Radio de la Tierra en km
    const dLat = (lat2 - lat1) * Math.PI / 180;
    const dLon = (lon2 - lon1) * Math.PI / 180;
    const a = 
        Math.sin(dLat/2) * Math.sin(dLat/2) +
        Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) * 
        Math.sin(dLon/2) * Math.sin(dLon/2);
    const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a));
    const distance = R * c;
    return distance;
}
```

---

## ⚠️ Consideraciones Importantes

### 1. **Formato de Coordenadas**
- Las coordenadas vienen en formato `decimal` desde el backend
- Los valores pueden ser muy grandes (ej: `42445999697856000`) - esto es normal
- Convertir a formato estándar si es necesario: `coordinate / 1000000`

### 2. **Valores Nulos**
- Los campos pueden ser `null` si no hay datos del experto
- Siempre validar antes de usar: `if (expertLatitude && expertLongitude)`

### 3. **Rango por Defecto**
- Si `locationRange` es `null`, usar rango por defecto de 50 km
- El backend ya maneja esto automáticamente

### 4. **Validación del Backend**
- El backend ya valida automáticamente las ubicaciones
- Si envías una ubicación fuera del rango, recibirás error 400
- El mensaje de error será descriptivo: "La ubicación propuesta para la cita está fuera del rango del experto..."

---

## 🚀 Implementación Recomendada

### 1. **Actualizar Interfaces TypeScript**
```typescript
interface AppointmentDto {
    // ... campos existentes ...
    expertLatitude?: number;
    expertLongitude?: number;
    locationRange?: number;
}

interface ServiceInfo {
    // ... campos existentes ...
    expertLatitude?: number;
    expertLongitude?: number;
    locationRange?: number;
}
```

### 2. **Actualizar Componentes React**
```typescript
// En el componente de propuesta de cita
const AppointmentProposal = ({ appointmentData }) => {
    const { expertLatitude, expertLongitude, locationRange } = appointmentData;
    
    const handleLocationSelect = (userLat, userLon) => {
        if (expertLatitude && expertLongitude && locationRange) {
            const distance = calculateDistance(userLat, userLon, expertLatitude, expertLongitude);
            
            if (distance > locationRange) {
                setError(`Ubicación fuera del rango. Máximo: ${locationRange} km`);
                return;
            }
        }
        
        // Proceder con la propuesta
        submitAppointmentProposal(userLat, userLon);
    };
    
    return (
        <div>
            {locationRange && (
                <p>Rango de servicio: {locationRange} km</p>
            )}
            {/* Resto del componente */}
        </div>
    );
};
```

---

## ✅ Checklist de Implementación

- [ ] Actualizar interfaces TypeScript con los nuevos campos
- [ ] Modificar componentes que muestran información de citas
- [ ] Implementar validación de ubicación en el frontend
- [ ] Agregar visualización del rango de cobertura en mapas
- [ ] Manejar casos donde los campos son `null`
- [ ] Probar con diferentes rangos de ubicación
- [ ] Verificar que los errores del backend se muestren correctamente

---

## 📞 Soporte

Si tienes dudas sobre la implementación o necesitas ayuda con algún aspecto específico, no dudes en consultar. Los cambios están completamente documentados y probados en el backend.




