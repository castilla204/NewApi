# 🗺️ Guía Frontend - Validación de Ubicación en Citas

## 📋 **Nuevos Campos en la Respuesta**

El endpoint `GET /api/Search/{searchId}/details-complete` ahora incluye información adicional en el objeto `appointment` para ayudar al frontend a validar ubicaciones:

### **Nuevos Campos Agregados:**
```typescript
interface AppointmentDto {
  // ... campos existentes ...
  
  // ✅ NUEVOS CAMPOS para validación de ubicación
  expertLatitude?: number;    // Coordenadas del experto al momento de la contratación
  expertLongitude?: number;   // Coordenadas del experto al momento de la contratación  
  locationRange?: number;     // Rango máximo permitido en km (por defecto: 50)
}
```

## 🎯 **Ejemplo de Respuesta Actualizada**

```json
{
  "search": {
    "id": 210,
    "userId": 28,
    "title": "revisión presencial",
    "description": "wqewqeqwe",
    // ... otros campos ...
    "searchHire": {
      "id": 82,
      "expertId": 34,
      "status": "pending",
      "expert": {
        "id": 34,
        "email": "a26865@svalero.com",
        "name": "Diego Castilla Abella",
        "profilePictureUrl": null
      },
      "service": {
        "id": 123,
        "serviceTypeId": 1,
        "serviceTypeName": "Revisión presencial",
        "price": 213
      }
    }
  },
  "appointment": {
    "id": 12,
    "searchHireId": 82,
    "status": "awaiting_appointment",
    "proposedDate": "0001-01-01T00:00:00",
    "proposedTime": "00:00:00",
    "location": "",
    "latitude": null,           // Ubicación propuesta para la cita (aún no definida)
    "longitude": null,          // Ubicación propuesta para la cita (aún no definida)
    
    // ✅ NUEVOS CAMPOS para validación
    "expertLatitude": 40.4168,  // Ubicación del experto (Madrid)
    "expertLongitude": -3.7038, // Ubicación del experto (Madrid)
    "locationRange": 50,        // Rango máximo: 50km
    
    "doorNumber": null,
    "ownerPhone": null,
    "siteDetails": null,
    "clientName": "patata nocaliente",
    "expertName": "Diego Castilla Abella",
    "amount": 213,
    "timers": []
  }
}
```

## 🗺️ **Implementación Frontend**

### **1. Validación en el Cliente**

```typescript
// Función para calcular distancia entre dos puntos
function calculateDistance(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const R = 6371; // Radio de la Tierra en km
  const dLat = (lat2 - lat1) * Math.PI / 180;
  const dLon = (lon2 - lon1) * Math.PI / 180;
  const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
            Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
            Math.sin(dLon / 2) * Math.sin(dLon / 2);
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return R * c;
}

// Función para validar ubicación antes de enviar
function validateAppointmentLocation(
  appointmentData: any,
  expertLat: number,
  expertLon: number,
  maxRange: number
): { isValid: boolean; message?: string; distance?: number } {
  
  if (!appointmentData.latitude || !appointmentData.longitude) {
    return { isValid: false, message: "Debe seleccionar una ubicación para la cita" };
  }
  
  const distance = calculateDistance(
    expertLat,
    expertLon,
    appointmentData.latitude,
    appointmentData.longitude
  );
  
  if (distance > maxRange) {
    return {
      isValid: false,
      message: `La ubicación está fuera del rango del experto. Distancia: ${distance.toFixed(1)} km, Rango máximo: ${maxRange} km`,
      distance
    };
  }
  
  return { isValid: true, distance };
}
```

### **2. Componente de Mapa con Validación**

```typescript
import React, { useState, useEffect } from 'react';
import { Map, Marker, Circle } from 'react-leaflet';

interface AppointmentLocationSelectorProps {
  appointment: AppointmentDto;
  onLocationSelect: (lat: number, lon: number) => void;
}

const AppointmentLocationSelector: React.FC<AppointmentLocationSelectorProps> = ({
  appointment,
  onLocationSelect
}) => {
  const [selectedLocation, setSelectedLocation] = useState<[number, number] | null>(null);
  const [validationMessage, setValidationMessage] = useState<string>('');

  // Validar ubicación cuando cambie
  useEffect(() => {
    if (selectedLocation && appointment.expertLatitude && appointment.expertLongitude) {
      const validation = validateAppointmentLocation(
        { latitude: selectedLocation[0], longitude: selectedLocation[1] },
        appointment.expertLatitude,
        appointment.expertLongitude,
        appointment.locationRange || 50
      );
      
      setValidationMessage(validation.message || '');
      
      if (validation.isValid) {
        onLocationSelect(selectedLocation[0], selectedLocation[1]);
      }
    }
  }, [selectedLocation, appointment, onLocationSelect]);

  return (
    <div className="appointment-location-selector">
      <h3>Seleccionar Ubicación para la Cita</h3>
      
      {/* Información del experto */}
      <div className="expert-info">
        <p><strong>Experto:</strong> {appointment.expertName}</p>
        <p><strong>Ubicación del experto:</strong> {appointment.expertLatitude}, {appointment.expertLongitude}</p>
        <p><strong>Rango máximo:</strong> {appointment.locationRange || 50} km</p>
      </div>

      {/* Mapa */}
      <div className="map-container">
        <Map
          center={[appointment.expertLatitude || 40.4168, appointment.expertLongitude || -3.7038]}
          zoom={10}
          onClick={(e) => setSelectedLocation([e.latlng.lat, e.latlng.lng])}
        >
          {/* Marcador del experto */}
          <Marker 
            position={[appointment.expertLatitude || 40.4168, appointment.expertLongitude || -3.7038]}
            title="Ubicación del experto"
          />
          
          {/* Círculo del rango permitido */}
          <Circle
            center={[appointment.expertLatitude || 40.4168, appointment.expertLongitude || -3.7038]}
            radius={(appointment.locationRange || 50) * 1000} // Convertir km a metros
            color="green"
            fillColor="green"
            fillOpacity={0.1}
          />
          
          {/* Marcador de ubicación seleccionada */}
          {selectedLocation && (
            <Marker 
              position={selectedLocation}
              title="Ubicación propuesta para la cita"
            />
          )}
        </Map>
      </div>

      {/* Mensaje de validación */}
      {validationMessage && (
        <div className={`validation-message ${validationMessage.includes('fuera') ? 'error' : 'success'}`}>
          {validationMessage}
        </div>
      )}

      {/* Instrucciones */}
      <div className="instructions">
        <p>💡 <strong>Instrucciones:</strong></p>
        <ul>
          <li>Haz clic en el mapa para seleccionar la ubicación de la cita</li>
          <li>La ubicación debe estar dentro del círculo verde (rango del experto)</li>
          <li>El experto solo puede realizar citas dentro de su rango de servicio original</li>
        </ul>
      </div>
    </div>
  );
};
```

### **3. Manejo de Errores del Backend**

```typescript
// Al proponer una cita
const proposeAppointment = async (appointmentData: ProposeAppointmentDto) => {
  try {
    const response = await fetch(`/api/appointment/propose/${searchHireId}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(appointmentData)
    });

    if (!response.ok) {
      const error = await response.json();
      
      // Mostrar el mensaje específico del backend
      if (error.message.includes('fuera del rango')) {
        showError(error.message);
        // Opcional: resaltar el área fuera del rango en el mapa
        highlightOutOfRangeArea();
      } else {
        showError(error.message);
      }
      return;
    }

    const result = await response.json();
    showSuccess('Cita propuesta exitosamente');
    
  } catch (error) {
    showError('Error de conexión');
  }
};
```

## 🎨 **Estilos CSS**

```css
.appointment-location-selector {
  max-width: 800px;
  margin: 0 auto;
  padding: 20px;
}

.expert-info {
  background: #f8f9fa;
  padding: 15px;
  border-radius: 8px;
  margin-bottom: 20px;
}

.map-container {
  height: 400px;
  border: 2px solid #dee2e6;
  border-radius: 8px;
  margin-bottom: 20px;
}

.validation-message {
  padding: 10px;
  border-radius: 4px;
  margin-bottom: 15px;
}

.validation-message.error {
  background-color: #f8d7da;
  color: #721c24;
  border: 1px solid #f5c6cb;
}

.validation-message.success {
  background-color: #d4edda;
  color: #155724;
  border: 1px solid #c3e6cb;
}

.instructions {
  background: #e3f2fd;
  padding: 15px;
  border-radius: 8px;
  border-left: 4px solid #2196f3;
}

.instructions ul {
  margin: 10px 0 0 20px;
}
```

## 🔄 **Flujo Completo**

1. **Usuario abre la página de citas** → Frontend obtiene datos del endpoint `details-complete`
2. **Frontend muestra mapa** → Con ubicación del experto y círculo de rango
3. **Usuario selecciona ubicación** → Frontend valida en tiempo real
4. **Usuario propone cita** → Backend valida nuevamente y devuelve error específico si es necesario
5. **Frontend muestra resultado** → Mensaje claro del backend

## ✅ **Beneficios**

- **Validación en tiempo real** en el frontend
- **Mensajes de error específicos** del backend
- **Visualización clara** del rango permitido
- **Experiencia de usuario mejorada**
- **Prevención de errores** antes de enviar al servidor




