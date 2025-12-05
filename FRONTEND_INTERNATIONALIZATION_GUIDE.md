# Guía de Internacionalización para Frontend

## 📋 Resumen de Cambios

Se ha implementado la internacionalización completa de fechas y horas en la API. Todos los endpoints ahora devuelven información de timezone y país para manejar correctamente las fechas según la ubicación del experto.

### 🆕 Nuevas Mejoras (Última Actualización)

1. **País en Reseñas (Reviews)**: Cada reseña ahora incluye el campo `Country` que indica el país donde se realizó la contratación. Úsalo para mostrar la bandera correspondiente en cada reseña.

2. **Precio en Mapa de Expertos**: El endpoint `GET /api/searchservice/map-experts` ahora incluye el precio del servicio para cada experto, permitiendo mostrar precios directamente en el mapa sin necesidad de seleccionar un rango de ubicación.

---

## 🆕 Campos Nuevos en los DTOs

### 1. **AppointmentDto** (Citas)

```typescript
interface AppointmentDto {
  // ... campos existentes ...
  
  // ✅ NUEVOS: Fechas en hora local del experto
  ProposedDateLocal?: Date;        // Fecha propuesta en hora local
  ProposedTimeLocal?: TimeSpan;   // Hora propuesta en hora local
  
  // ✅ NUEVOS: Información de internacionalización
  Timezone?: string;               // Timezone IANA (ej: "Europe/Madrid", "America/Mexico_City")
  Country?: string;                // País ISO 3166-1 alpha-2 (ej: "ES", "MX")
  
  // Campos existentes (en UTC)
  ProposedDate: Date;              // UTC (guardada en BD)
  ProposedTime: TimeSpan;          // UTC (guardada en BD)
}
```

**Ejemplo de respuesta:**
```json
{
  "id": 123,
  "searchHireId": 456,
  "status": "appointment_proposed",
  "proposedDate": "2025-01-15T00:00:00Z",           // UTC
  "proposedTime": "14:00:00",                        // UTC
  "proposedDateLocal": "2025-01-15T00:00:00",        // Local (España)
  "proposedTimeLocal": "15:00:00",                   // Local (España, UTC+1)
  "timezone": "Europe/Madrid",
  "country": "ES",
  "location": "Calle Principal 123",
  // ... otros campos ...
}
```

---

### 2. **SearchHireDto** y **SearchHireResponseDto** (Contrataciones)

```typescript
interface SearchHireDto {
  // ... campos existentes ...
  
  // ✅ NUEVOS: Información de internacionalización
  ExpertTimezone?: string;         // Timezone IANA del experto al momento de la contratación
  ExpertCountry?: string;          // País ISO del experto al momento de la contratación
}

interface SearchHireResponseDto {
  // ... campos existentes ...
  
  // ✅ NUEVOS: Información de internacionalización
  ExpertTimezone?: string;         // Timezone IANA del experto al momento de la contratación
  ExpertCountry?: string;          // País ISO del experto al momento de la contratación
}
```

**Ejemplo de respuesta:**
```json
{
  "id": 456,
  "clientId": 1,
  "expertId": 2,
  "status": "pending",
  "amount": 150.00,
  "createdAt": "2025-01-10T10:00:00Z",
  "expertTimezone": "Europe/Madrid",
  "expertCountry": "ES",
  "service": {
    // ... información del servicio ...
  }
}
```

---

### 3. **ExpertProfileDto** (Perfil del Experto)

```typescript
interface ExpertProfileDto {
  // ... campos existentes ...
  
  // ✅ NUEVOS: Información de internacionalización
  Timezone?: string;               // Timezone IANA actual del experto
  Country?: string;                // País ISO actual del experto
}
```

---

### 4. **ReviewDto** (Reseñas)

```typescript
interface ReviewDto {
  id: number;
  score: number;
  description: string;
  createdAt: Date;
  reviewer: UserDto;
  imageUrls: string[];
  
  // ✅ NUEVO: País donde se realizó la contratación
  Country?: string;                // País ISO (ej: "ES", "MX")
}
```

**Ejemplo de respuesta:**
```json
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
  "country": "ES"
}
```

**Ejemplo de respuesta:**
```json
{
  "id": 789,
  "profilePictureUrl": "https://...",
  "description": "Experto en inspecciones",
  "latitude": "40.4168",
  "longitude": "-3.7038",
  "timezone": "Europe/Madrid",
  "country": "ES",
  "user": {
    "id": 2,
    "name": "Juan Pérez",
    "email": "juan@example.com"
  }
}
```

---

### 5. **ExpertMapDto** (Expertos en el Mapa)

```typescript
interface ExpertMapDto {
  id: number;
  name: string;
  profilePictureUrl: string;
  averageRating: number;
  totalReviews: number;
  completedSearches: number;
  registeredSince: Date;
  latitude: string;
  longitude: string;
  
  // ✅ NUEVO: Precio del servicio
  price: number;                   // Precio en euros
}
```

**Ejemplo de respuesta:**
```json
{
  "experts": [
    {
      "id": 40,
      "name": "Diego Castilla",
      "profilePictureUrl": "https://...",
      "averageRating": 0,
      "totalReviews": 0,
      "completedSearches": 0,
      "registeredSince": "2025-11-22T19:43:11.653346Z",
      "latitude": "41.54660957575336",
      "longitude": "-0.9480622482776679",
      "price": 150.00
    }
  ],
  "totalCount": 2
}
```

---

## 🔌 Endpoints Modificados

### 1. **Citas (Appointments)**

#### `GET /api/appointments/{id}`
**Respuesta:** `AppointmentDto` con campos `Timezone`, `Country`, `ProposedDateLocal`, `ProposedTimeLocal`

#### `GET /api/appointments/by-search-hire/{searchHireId}`
**Respuesta:** `AppointmentDto` con campos de internacionalización

#### `GET /api/appointments/user/{userId}`
**Respuesta:** `List<AppointmentDto>` con campos de internacionalización

#### `POST /api/appointments`
**Request:** `CreateAppointmentDto`
```typescript
interface CreateAppointmentDto {
  searchHireId: number;
  proposedDate: Date;              // Hora LOCAL del experto
  proposedTime: TimeSpan;          // Hora LOCAL del experto
  timezone?: string;               // Opcional: si no se envía, se usa el del experto
  location: string;
  latitude?: number;
  longitude?: number;
  // ... otros campos ...
}
```

#### `PUT /api/appointments/{searchHireId}/propose`
**Request:** `ProposeAppointmentDto`
```typescript
interface ProposeAppointmentDto {
  proposedDate: Date;              // Hora LOCAL del experto
  proposedTime: TimeSpan;          // Hora LOCAL del experto
  timezone?: string;               // Opcional: si no se envía, se usa el del experto
  location: string;
  // ... otros campos ...
}
```

---

### 2. **Contrataciones (SearchHires)**

#### `GET /api/searchhire`
**Respuesta:** `List<SearchHireResponseDto>` con `ExpertTimezone` y `ExpertCountry`

#### `GET /api/searchhire/{id}`
**Respuesta:** `SearchHireResponseDto` con campos de internacionalización

#### `GET /api/searchhire/{id}/details-complete`
**Respuesta:** `SearchDetailsCompleteResponseDto` que incluye:
- `SearchHire` con `ExpertTimezone` y `ExpertCountry`
- `Appointment` con `Timezone`, `Country`, `ProposedDateLocal`, `ProposedTimeLocal`
- `Service.Expert` con `Timezone` y `Country`

---

### 3. **Servicios (SearchServices)**

#### `GET /api/searchservice`
**Respuesta:** `List<SearchServiceResponseDto>` donde cada servicio incluye:
```json
{
  "id": 123,
  "price": 150.00,
  "expert": {
    "id": 789,
    "timezone": "Europe/Madrid",
    "country": "ES",
    // ... otros campos ...
    "reviews": [
      {
        "id": 1,
        "score": 5,
        "description": "Excelente",
        "country": "ES"  // ✅ País donde se realizó la contratación
      }
    ]
  }
}
```

#### `GET /api/searchservice/{id}`
**Respuesta:** `SearchServiceDetailDto` con `Expert.Timezone`, `Expert.Country` y `Expert.Reviews[].Country`

#### `GET /api/searchservice/GetServiceByHireId/{id}`
**Respuesta:** `SearchServiceDetailDto` con campos de internacionalización

---

### 4. **Reseñas (Reviews)**

#### `GET /api/reviews/expert/{expertId}`
**Respuesta:** `List<ReviewResponseDto>` donde cada reseña incluye:
```json
{
  "reviews": [
    {
      "id": 1,
      "score": 5,
      "description": "Excelente servicio",
      "country": "ES",  // ✅ País donde se realizó la contratación
      // ... otros campos ...
    }
  ]
}
```

**Nota:** El campo `Country` proviene de `SearchHire.ExpertCountry` (snapshot al momento de crear la contratación), por lo que cada reseña muestra el país donde se realizó esa contratación específica.

---

### 5. **Mapa de Expertos**

#### `GET /api/searchservice/map-experts?categoryId={id}&serviceTypeId={id}`
**Respuesta:** `ExpertMapResponseDto` donde cada experto incluye:
```json
{
  "experts": [
    {
      "id": 40,
      "name": "Diego Castilla",
      "price": 150.00,  // ✅ NUEVO: Precio del servicio
      // ... otros campos ...
    }
  ],
  "totalCount": 2
}
```

**Nota:** El precio corresponde al primer servicio del experto que coincida con los filtros `categoryId` y `serviceTypeId`.

---

## 📝 Ejemplos de Uso en Frontend

### Ejemplo 1: Mostrar fecha de cita en hora local

```typescript
// ✅ CORRECTO: Usar ProposedDateLocal y ProposedTimeLocal
const appointment: AppointmentDto = await getAppointment(id);

// Mostrar fecha en hora local del experto
const displayDate = appointment.proposedDateLocal 
  ? formatDate(appointment.proposedDateLocal, appointment.timezone)
  : formatDate(appointment.proposedDate, 'UTC');

const displayTime = appointment.proposedTimeLocal 
  ? formatTime(appointment.proposedTimeLocal)
  : formatTime(appointment.proposedTime);

console.log(`Cita: ${displayDate} a las ${displayTime} (${appointment.timezone})`);
```

### Ejemplo 2: Crear nueva cita

```typescript
// El frontend envía la fecha en hora LOCAL del experto
const createAppointmentDto: CreateAppointmentDto = {
  searchHireId: 456,
  proposedDate: new Date('2025-01-15T15:00:00'),  // Hora local (15:00 en España)
  proposedTime: { hours: 15, minutes: 0 },        // Hora local
  timezone: 'Europe/Madrid',                      // Opcional: se puede omitir
  location: 'Calle Principal 123',
  latitude: 40.4168,
  longitude: -3.7038
};

// El backend convierte automáticamente a UTC antes de guardar
await createAppointment(createAppointmentDto);
```

### Ejemplo 3: Mostrar información de contratación

```typescript
const searchHire: SearchHireResponseDto = await getSearchHire(id);

// Mostrar timezone y país del experto
console.log(`Experto ubicado en: ${searchHire.expertCountry}`);
console.log(`Timezone: ${searchHire.expertTimezone}`);

// Usar esta información para formatear fechas relacionadas
if (searchHire.expertTimezone) {
  const localDate = convertToLocal(date, searchHire.expertTimezone);
  // ...
}
```

### Ejemplo 4: Listado de servicios

```typescript
const services: SearchServiceResponseDto[] = await getServices();

services.forEach(service => {
  console.log(`Servicio ${service.id}:`);
  console.log(`  Experto: ${service.expert?.user?.name}`);
  console.log(`  País: ${service.expert?.country}`);
  console.log(`  Timezone: ${service.expert?.timezone}`);
  
  // Usar esta información para mostrar disponibilidad en hora local
  if (service.expert?.timezone) {
    // Convertir horarios de disponibilidad a hora local
    // ...
  }
});
```

### Ejemplo 5: Mostrar bandera en cada reseña

```typescript
const reviews: ReviewDto[] = expertProfile.reviews;

reviews.forEach(review => {
  // Mostrar bandera según el país donde se realizó la contratación
  const flagEmoji = getCountryFlag(review.country); // 🇪🇸, 🇲🇽, etc.
  
  console.log(`${flagEmoji} ${review.reviewer.name}: ${review.description}`);
  console.log(`  País: ${review.country}`);
  console.log(`  Puntuación: ${review.score}/5`);
});
```

### Ejemplo 6: Filtrar reseñas por país

```typescript
const reviews: ReviewDto[] = await getExpertReviews(expertId);

// Filtrar solo reseñas de España
const reviewsFromSpain = reviews.filter(r => r.country === 'ES');

// Agrupar por país
const reviewsByCountry = reviews.reduce((acc, review) => {
  const country = review.country || 'Unknown';
  if (!acc[country]) acc[country] = [];
  acc[country].push(review);
  return acc;
}, {} as Record<string, ReviewDto[]>);
```

---

## ⚠️ Importante: Cambios de Comportamiento

### 1. **Fechas en Requests (POST/PUT)**

**ANTES:**
```typescript
// ❌ INCORRECTO: Enviar fecha en UTC
proposedDate: new Date('2025-01-15T14:00:00Z')  // UTC
```

**AHORA:**
```typescript
// ✅ CORRECTO: Enviar fecha en hora LOCAL del experto
proposedDate: new Date('2025-01-15T15:00:00')   // Local (España, UTC+1)
timezone: 'Europe/Madrid'                       // Opcional
```

### 2. **Fechas en Responses (GET)**

**ANTES:**
```typescript
// Solo había fecha UTC
appointment.proposedDate  // UTC
```

**AHORA:**
```typescript
// Hay fecha UTC Y fecha local
appointment.proposedDate        // UTC (para cálculos)
appointment.proposedDateLocal   // Local (para mostrar)
appointment.proposedTimeLocal   // Local (para mostrar)
appointment.timezone            // Timezone usado
appointment.country             // País del experto
```

### 3. **Prioridad de Timezone**

El backend usa esta prioridad para determinar el timezone:

1. **DTO.Timezone** (si se envía en el request)
2. **SearchHire.ExpertTimezone** (snapshot al crear la contratación)
3. **ExpertProfile.Timezone** (timezone actual del experto)
4. **"UTC"** (fallback)

---

## 🔄 Migración del Frontend

### Paso 1: Actualizar interfaces TypeScript

```typescript
// Agregar campos nuevos a las interfaces existentes
interface AppointmentDto {
  // ... campos existentes ...
  proposedDateLocal?: Date;
  proposedTimeLocal?: TimeSpan;
  timezone?: string;
  country?: string;
}

interface SearchHireResponseDto {
  // ... campos existentes ...
  expertTimezone?: string;
  expertCountry?: string;
}

interface ExpertProfileDto {
  // ... campos existentes ...
  timezone?: string;
  country?: string;
}
```

### Paso 2: Actualizar componentes de visualización

```typescript
// Antes
<DateDisplay date={appointment.proposedDate} />

// Ahora (usar fecha local)
<DateDisplay 
  date={appointment.proposedDateLocal || appointment.proposedDate}
  timezone={appointment.timezone}
/>
```

### Paso 3: Actualizar formularios de creación

```typescript
// Antes: convertir a UTC manualmente
const utcDate = convertToUTC(localDate, timezone);

// Ahora: enviar directamente en hora local
const dto: CreateAppointmentDto = {
  proposedDate: localDate,  // Ya en hora local
  timezone: timezone        // Opcional
};
```

---

## 📚 Referencias

- **IANA Timezones:** https://en.wikipedia.org/wiki/List_of_tz_database_time_zones
- **ISO 3166-1 alpha-2:** https://en.wikipedia.org/wiki/ISO_3166-1_alpha-2
- **Ejemplos de timezones:**
  - `Europe/Madrid` (España)
  - `America/Mexico_City` (México)
  - `America/New_York` (EE.UU. Este)
  - `America/Los_Angeles` (EE.UU. Oeste)

---

## ✅ Checklist de Implementación

- [ ] Actualizar interfaces TypeScript con campos nuevos
- [ ] Actualizar componentes que muestran fechas (usar `*Local`)
- [ ] Actualizar formularios de creación (enviar en hora local)
- [ ] Actualizar componentes de listado de servicios (mostrar timezone/country)
- [ ] Actualizar componentes de detalles de contratación (mostrar timezone/country)
- [ ] Actualizar componentes de reseñas (mostrar bandera según `Country`)
- [ ] Probar conversión de fechas en diferentes timezones
- [ ] Verificar que las fechas se muestran correctamente según el país del experto
- [ ] Implementar función helper para obtener emoji de bandera desde código ISO

---

## 🐛 Troubleshooting

### Problema: Las fechas no se muestran correctamente

**Solución:** Asegúrate de usar `proposedDateLocal` y `proposedTimeLocal` en lugar de `proposedDate` y `proposedTime` para mostrar.

### Problema: El timezone es null

**Solución:** Verifica que el experto tenga configurado su ubicación (latitud/longitud) para que se detecte automáticamente el timezone.

### Problema: Las fechas se guardan incorrectamente

**Solución:** Asegúrate de enviar las fechas en hora LOCAL del experto, no en UTC. El backend se encarga de la conversión.

### Problema: No se muestra el precio en el mapa

**Solución:** 
1. Verifica que estás usando el endpoint `GET /api/searchservice/map-experts` (no `GetAllServices`)
2. El endpoint `GetAllServices` solo muestra expertos cuando seleccionas una ubicación, por eso no es adecuado para la vista inicial del mapa
3. Usa `map-experts` para mostrar todos los expertos con precios, y luego `GetAllServices` solo cuando el usuario filtre por ubicación

### Problema: No se muestra la bandera en las reseñas

**Solución:** 
1. Verifica que el campo `Country` está presente en la respuesta de `ReviewDto`
2. Si es `null`, muestra una bandera genérica o no muestres bandera
3. El campo `Country` proviene del `SearchHire.ExpertCountry` al momento de crear la contratación

