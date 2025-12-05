# Ejemplos de DTOs Modificados - Internacionalización

## 📋 Ejemplos JSON de Respuestas

### 1. AppointmentDto - GET /api/appointments/{id}

```json
{
  "id": 123,
  "searchHireId": 456,
  "status": "appointment_proposed",
  
  // ✅ CAMPOS NUEVOS: Fechas en hora local
  "proposedDateLocal": "2025-01-15T00:00:00",
  "proposedTimeLocal": "15:00:00",
  "timezone": "Europe/Madrid",
  "country": "ES",
  
  // Campos existentes (UTC)
  "proposedDate": "2025-01-15T00:00:00Z",
  "proposedTime": "14:00:00",
  
  "location": "Calle Gran Vía 123, Madrid",
  "latitude": 40.4168,
  "longitude": -3.7038,
  "doorNumber": "3º B",
  "ownerPhone": "+34612345678",
  "siteDetails": "Edificio con ascensor",
  
  "rejectionCount": 0,
  "clientCancellationCount": 0,
  "expertCancellationCount": 0,
  "lastRejectionAt": null,
  "lastClientCancellationAt": null,
  "lastExpertCancellationAt": null,
  "lastProposalAt": "2025-01-10T10:00:00Z",
  "lastResponseAt": null,
  "createdAt": "2025-01-10T09:00:00Z",
  "updatedAt": "2025-01-10T10:00:00Z",
  
  "clientName": "María García",
  "expertName": "Juan Pérez",
  "amount": 150.00,
  
  "expertLatitude": "40.4168",
  "expertLongitude": "-3.7038",
  "locationRange": 50,
  
  "timers": [
    {
      "id": 1,
      "appointmentId": 123,
      "timerType": "proposal",
      "startTime": "2025-01-10T10:00:00Z",
      "endTime": "2025-01-11T10:00:00Z",
      "isExpired": false,
      "expiredAt": null,
      "notes": null,
      "createdAt": "2025-01-10T10:00:00Z"
    }
  ],
  
  "statusInfo": {
    "id": 5,
    "statusType": "AppointmentStatus",
    "statusName": "Appointment Proposed",
    "statusValue": "appointment_proposed",
    "displayName": "Cita Propuesta",
    "description": "El cliente ha propuesto una fecha y hora para la cita",
    "color": "#FFA500",
    "isActive": true,
    "isFinalizationStatus": false
  }
}
```

---

### 2. SearchHireResponseDto - GET /api/searchhire

```json
{
  "id": 456,
  "clientId": 1,
  "expertId": 2,
  "searchServiceId": 789,
  "searchId": 100,
  "status": "pending",
  "statusTranslated": "Pendiente",
  "expertTransferId": null,
  "amount": 150.00,
  "createdAt": "2025-01-10T10:00:00Z",
  "updatedAt": null,
  
  // ✅ CAMPOS NUEVOS: Internacionalización
  "expertTimezone": "Europe/Madrid",
  "expertCountry": "ES",
  
  "client": {
    "id": 1,
    "name": "María García",
    "email": "maria@example.com"
  },
  "expert": {
    "id": 2,
    "name": "Juan Pérez",
    "email": "juan@example.com"
  },
  "service": {
    "id": 789,
    "categoryId": 1,
    "serviceTypeId": 5,
    "serviceTypeName": "Inspección Técnica",
    "serviceTypeCategoryId": 2,
    "requiresAppointment": true,
    "price": 150.00,
    "conditions": "Incluye informe detallado",
    "durationInHours": 2,
    "createdAt": "2025-01-01T00:00:00Z",
    "isActive": true,
    "imageUrls": [
      "https://storage.googleapis.com/bucket/services/image1.jpg"
    ],
    "selectedDeliverableTypes": [
      {
        "id": 1,
        "name": "video",
        "displayName": "Video",
        "description": "Video de la inspección",
        "isRequired": true,
        "isActive": true,
        "sortOrder": 1
      }
    ],
    "expert": {
      "id": 789,
      "profilePictureUrl": "https://storage.googleapis.com/bucket/profiles/expert.jpg",
      "description": "Experto certificado en inspecciones técnicas",
      "stripeAccountId": "acct_1234567890",
      "createdAt": "2024-12-01T00:00:00Z",
      "user": {
        "id": 2,
        "name": "Juan Pérez",
        "email": "juan@example.com"
      },
      "reviews": [],
      "latitude": "40.4168",
      "longitude": "-3.7038",
      "stripeStatus": "Approved",
      "stripeStatusDetails": "Cuenta verificada y activa",
      "onboardingCompleted": true,
      "isOnVacation": false,
      "currentAvailability": {
        "id": 1,
        "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        "startTime": "09:00:00",
        "endTime": "18:00:00",
        "effectiveFrom": "2025-01-01T00:00:00Z"
      },
      // ✅ CAMPOS NUEVOS: Internacionalización
      "timezone": "Europe/Madrid",
      "country": "ES",
      "stripeFutureRequirements": null,
      "stripeFutureDueAt": null
    }
  },
  "serviceType": {
    "id": 5,
    "name": "Inspección Técnica",
    "description": "Inspección técnica completa",
    "isActive": true,
    "createdAt": "2024-01-01T00:00:00Z",
    "updatedAt": "2024-01-01T00:00:00Z"
  },
  "statusInfo": {
    "id": 1,
    "statusType": "SearchHireStatus",
    "statusName": "Pending",
    "statusValue": "pending",
    "displayName": "Pendiente",
    "description": "Contratación pendiente de aceptación",
    "color": "#FFA500",
    "isActive": true,
    "isFinalizationStatus": false
  },
  "searchTitle": "Necesito inspección de vehículo",
  "searchDescription": "Busco un experto para inspeccionar mi vehículo antes de comprarlo",
  "unreadMessagesCount": 2
}
```

---

### 3. SearchServiceResponseDto - GET /api/searchservice

```json
{
  "id": 789,
  "categoryId": 1,
  "serviceTypeId": 5,
  "serviceTypeName": "Inspección Técnica",
  "serviceTypeCategoryId": 2,
  "requiresAppointment": true,
  "price": 150.00,
  "conditions": "Incluye informe detallado y video",
  "durationInHours": 2,
  "createdAt": "2025-01-01T00:00:00Z",
  "isActive": true,
  "imageUrls": [
    "https://storage.googleapis.com/bucket/services/image1.jpg",
    "https://storage.googleapis.com/bucket/services/image2.jpg"
  ],
  "selectedDeliverableTypes": [
    {
      "id": 1,
      "name": "video",
      "displayName": "Video",
      "description": "Video de la inspección",
      "isRequired": true,
      "isActive": true,
      "sortOrder": 1
    },
    {
      "id": 2,
      "name": "report",
      "displayName": "Informe",
      "description": "Informe detallado",
      "isRequired": true,
      "isActive": true,
      "sortOrder": 2
    }
  ],
  "expert": {
    "id": 789,
    "profilePictureUrl": "https://storage.googleapis.com/bucket/profiles/expert.jpg",
    "description": "Experto certificado con más de 10 años de experiencia",
    "stripeAccountId": "acct_1234567890",
    "createdAt": "2024-12-01T00:00:00Z",
    "user": {
      "id": 2,
      "name": "Juan Pérez",
      "email": "juan@example.com"
    },
    "reviews": [
      {
        "id": 1,
        "score": 5,
        "description": "Excelente servicio, muy profesional",
        "createdAt": "2024-12-15T10:00:00Z",
        "reviewer": {
          "id": 1,
          "name": "María García",
          "email": "maria@example.com",
          "profilePictureUrl": null
        },
        "imageUrls": []
      }
    ],
    "latitude": "40.4168",
    "longitude": "-3.7038",
    "stripeStatus": "Approved",
    "stripeStatusDetails": "Cuenta verificada y activa",
    "onboardingCompleted": true,
    "isOnVacation": false,
    "currentAvailability": {
      "id": 1,
      "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "startTime": "09:00:00",
      "endTime": "18:00:00",
      "effectiveFrom": "2025-01-01T00:00:00Z"
    },
    // ✅ CAMPOS NUEVOS: Internacionalización
    "timezone": "Europe/Madrid",
    "country": "ES",
    "stripeFutureRequirements": null,
    "stripeFutureDueAt": null
  }
}
```

---

### 4. SearchServiceDetailDto - GET /api/searchservice/{id}

```json
{
  // Todos los campos de SearchServiceResponseDto +
  "categoryName": "Vehículos",
  "completedSearches": 45,
  "averageRating": 4.8,
  
  // El objeto "expert" incluye timezone y country
  "expert": {
    // ... todos los campos del ejemplo anterior ...
    "timezone": "Europe/Madrid",
    "country": "ES"
  }
}
```

---

### 5. CreateAppointmentDto - POST /api/appointments

**Request Body:**
```json
{
  "searchHireId": 456,
  
  // ✅ IMPORTANTE: Enviar en hora LOCAL del experto
  "proposedDate": "2025-01-15T00:00:00",  // Fecha local (no UTC)
  "proposedTime": "15:00:00",              // Hora local (15:00 en España)
  
  // ✅ Opcional: Si no se envía, se usa el timezone del experto
  "timezone": "Europe/Madrid",
  
  "location": "Calle Gran Vía 123, Madrid",
  "latitude": 40.4168,
  "longitude": -3.7038,
  "doorNumber": "3º B",
  "ownerPhone": "+34612345678",
  "siteDetails": "Edificio con ascensor"
}
```

**Response:** `AppointmentDto` con campos convertidos a UTC y también en hora local.

---

### 6. ProposeAppointmentDto - PUT /api/appointments/{searchHireId}/propose

**Request Body:**
```json
{
  // ✅ IMPORTANTE: Enviar en hora LOCAL del experto
  "proposedDate": "2025-01-15T00:00:00",  // Fecha local
  "proposedTime": "15:00:00",              // Hora local
  
  // ✅ Opcional: Si no se envía, se usa el timezone del experto
  "timezone": "Europe/Madrid",
  
  "location": "Calle Gran Vía 123, Madrid",
  "latitude": 40.4168,
  "longitude": -3.7038,
  "doorNumber": "3º B",
  "ownerPhone": "+34612345678",
  "siteDetails": "Edificio con ascensor"
}
```

---

## 🔄 Comparación: Antes vs Ahora

### ANTES (Sin Internacionalización)

```json
{
  "id": 123,
  "proposedDate": "2025-01-15T14:00:00Z",  // Solo UTC
  "proposedTime": "14:00:00"                // Solo UTC
  // ❌ No había información de timezone ni país
}
```

### AHORA (Con Internacionalización)

```json
{
  "id": 123,
  "proposedDate": "2025-01-15T14:00:00Z",      // UTC (para cálculos)
  "proposedTime": "14:00:00",                   // UTC (para cálculos)
  "proposedDateLocal": "2025-01-15T00:00:00",   // ✅ Local (para mostrar)
  "proposedTimeLocal": "15:00:00",               // ✅ Local (para mostrar)
  "timezone": "Europe/Madrid",                  // ✅ Timezone usado
  "country": "ES"                                // ✅ País del experto
}
```

---

## 7. ReviewDto - GET /api/reviews/expert/{expertId}

```json
{
  "id": 1,
  "score": 5,
  "description": "Excelente servicio, muy profesional y puntual",
  "createdAt": "2025-01-10T10:00:00Z",
  "reviewer": {
    "id": 1,
    "name": "María García",
    "email": "maria@example.com"
  },
  "imageUrls": [
    "https://storage.googleapis.com/bucket/reviews/image1.jpg"
  ],
  // ✅ NUEVO: País donde se realizó la contratación
  "country": "ES"
}
```

**Ejemplo de lista de reviews:**
```json
{
  "reviews": [
    {
      "id": 1,
      "score": 5,
      "description": "Excelente servicio",
      "createdAt": "2025-01-10T10:00:00Z",
      "reviewer": {
        "id": 1,
        "name": "María García",
        "email": "maria@example.com"
      },
      "imageUrls": [],
      "country": "ES"  // ✅ España
    },
    {
      "id": 2,
      "score": 4,
      "description": "Muy buen trabajo",
      "createdAt": "2025-01-05T10:00:00Z",
      "reviewer": {
        "id": 3,
        "name": "Carlos López",
        "email": "carlos@example.com"
      },
      "imageUrls": [],
      "country": "MX"  // ✅ México
    }
  ],
  "pagination": {
    "page": 1,
    "pageSize": 20,
    "totalCount": 2,
    "totalPages": 1,
    "hasNextPage": false,
    "hasPreviousPage": false
  }
}
```

---

## 8. ExpertMapDto - GET /api/searchservice/map-experts

```json
{
  "experts": [
    {
      "id": 40,
      "name": "Diego Castilla",
      "profilePictureUrl": "https://storage.googleapis.com/...",
      "averageRating": 0,
      "totalReviews": 0,
      "completedSearches": 0,
      "registeredSince": "2025-11-22T19:43:11.653346Z",
      "latitude": "41.54660957575336",
      "longitude": "-0.9480622482776679",
      // ✅ NUEVO: Precio del servicio
      "price": 150.00
    },
    {
      "id": 34,
      "name": "Diego Castilla Abella",
      "profilePictureUrl": "https://storage.googleapis.com/...",
      "averageRating": 4,
      "totalReviews": 5,
      "completedSearches": 0,
      "registeredSince": "2025-09-14T17:55:59.171923Z",
      "latitude": "-28.155790867269413",
      "longitude": "132.78048084507316",
      // ✅ NUEVO: Precio del servicio
      "price": 200.00
    }
  ],
  "totalCount": 2
}
```

**Nota:** El precio corresponde al primer servicio del experto que coincida con el `categoryId` y `serviceTypeId` especificados.

---

## 📝 Notas Importantes

1. **Siempre usar `*Local` para mostrar:** Usa `proposedDateLocal` y `proposedTimeLocal` para mostrar fechas al usuario.

2. **Enviar en hora local:** Al crear/proponer citas, envía las fechas en hora LOCAL del experto, no en UTC.

3. **Timezone opcional:** El campo `timezone` en requests es opcional. Si no se envía, el backend usa el timezone del experto desde `SearchHire.ExpertTimezone` o `ExpertProfile.Timezone`.

4. **Snapshot de contratación:** `SearchHire.ExpertTimezone` y `ExpertCountry` son snapshots tomados al momento de crear la contratación, por lo que no cambian aunque el experto se mueva.

5. **Timezone actual vs snapshot:** 
   - `ExpertProfile.Timezone` = timezone actual del experto
   - `SearchHire.ExpertTimezone` = timezone al momento de la contratación (snapshot)

6. **País en reviews:** El campo `Country` en `ReviewDto` indica el país donde se realizó la contratación (obtenido de `SearchHire.ExpertCountry`). Úsalo para mostrar la bandera correspondiente en cada reseña.

7. **Precio en mapa:** El campo `Price` en `ExpertMapDto` permite mostrar precios directamente en el mapa. ⚠️ **IMPORTANTE:** Usa el endpoint `GET /api/searchservice/map-experts` para obtener expertos con precios. NO uses `GET /api/searchservice` para el mapa inicial, ya que ese endpoint solo muestra expertos cuando seleccionas una ubicación dentro del rango.

