# 📚 Documentación API: Crear Servicio de Experto

## 🎯 Endpoint

```
POST /api/SearchService
```

**Autenticación**: Requerida (Bearer Token)  
**Rol**: Solo `Expert`  
**Content-Type**: `multipart/form-data`

---

## 📦 Request Body (FormData)

### **Campos Requeridos:**

| Campo | Tipo | Descripción | Validación |
|-------|------|-------------|------------|
| `ExpertProfileId` | `number` | ID del perfil de experto del usuario autenticado | Debe coincidir con el experto autenticado |
| `CategoryId` | `number` | ID de la categoría (puede ser categoría padre o subcategoría) | Debe existir y estar activa |
| `ServiceTypeId` | `number` | ID del tipo de servicio | Debe ser > 0 |
| `Price` | `decimal` | **Precio CON IVA incluido** (precio final que pagará el cliente) | Debe ser > 0 |
| `Conditions` | `string` | Condiciones del servicio | No puede estar vacío |
| `DurationInHours` | `number` | Duración del servicio en horas | Debe ser > 0 |

### **Campos Opcionales:**

| Campo | Tipo | Descripción |
|-------|------|-------------|
| `Images` | `File[]` | Array de imágenes del servicio (máximo según configuración) |
| `SelectedDeliverableTypes` | `string` | **JSON string** con array de IDs de tipos de entregables seleccionados |

---

## 📝 Ejemplo de Request (JavaScript/TypeScript)

### **Con Fetch API:**

```typescript
const createService = async (serviceData: {
  expertProfileId: number;
  categoryId: number;
  serviceTypeId: number;
  price: number;
  conditions: string;
  durationInHours: number;
  images?: File[];
  selectedDeliverableTypes?: number[];
}) => {
  const formData = new FormData();
  
  // Campos requeridos
  formData.append('ExpertProfileId', serviceData.expertProfileId.toString());
  formData.append('CategoryId', serviceData.categoryId.toString());
  formData.append('ServiceTypeId', serviceData.serviceTypeId.toString());
  formData.append('Price', serviceData.price.toString());
  formData.append('Conditions', serviceData.conditions);
  formData.append('DurationInHours', serviceData.durationInHours.toString());
  
  // Imágenes (opcional)
  if (serviceData.images && serviceData.images.length > 0) {
    serviceData.images.forEach((image, index) => {
      formData.append('Images', image);
    });
  }
  
  // SelectedDeliverableTypes (opcional) - Debe ser JSON string
  if (serviceData.selectedDeliverableTypes && serviceData.selectedDeliverableTypes.length > 0) {
    formData.append('SelectedDeliverableTypes', JSON.stringify(serviceData.selectedDeliverableTypes));
  }
  
  const response = await fetch('/api/SearchService', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}` // Token JWT del experto
    },
    body: formData
  });
  
  return await response.json();
};
```

### **Con Axios:**

```typescript
import axios from 'axios';

const createService = async (serviceData: {
  expertProfileId: number;
  categoryId: number;
  serviceTypeId: number;
  price: number;
  conditions: string;
  durationInHours: number;
  images?: File[];
  selectedDeliverableTypes?: number[];
}) => {
  const formData = new FormData();
  
  formData.append('ExpertProfileId', serviceData.expertProfileId.toString());
  formData.append('CategoryId', serviceData.categoryId.toString());
  formData.append('ServiceTypeId', serviceData.serviceTypeId.toString());
  formData.append('Price', serviceData.price.toString());
  formData.append('Conditions', serviceData.conditions);
  formData.append('DurationInHours', serviceData.durationInHours.toString());
  
  if (serviceData.images) {
    serviceData.images.forEach((image) => {
      formData.append('Images', image);
    });
  }
  
  if (serviceData.selectedDeliverableTypes) {
    formData.append('SelectedDeliverableTypes', JSON.stringify(serviceData.selectedDeliverableTypes));
  }
  
  const response = await axios.post('/api/SearchService', formData, {
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'multipart/form-data'
    }
  });
  
  return response.data;
};
```

---

## ✅ Respuestas Exitosas

### **200 OK - Servicio Creado Exitosamente:**

```json
{
  "message": "Search service created successfully",
  "searchService": {
    "id": 123,
    "expertProfileId": 45,
    "categoryId": 5,
    "serviceTypeId": 2,
    "serviceTypeName": "Inspección",
    "price": 110.00,
    "conditions": "El servicio incluye inspección completa del vehículo...",
    "durationInHours": 2,
    "createdAt": "2025-01-20T10:30:00Z",
    "isActive": true,
    "imageUrls": [
      "https://storage.googleapis.com/bucket/services/123/image1.jpg",
      "https://storage.googleapis.com/bucket/services/123/image2.jpg"
    ],
    "selectedDeliverableTypes": [
      {
        "id": 1,
        "deliverableTypeId": 3,
        "isSelected": true,
        "deliverableType": {
          "id": 3,
          "name": "Informe PDF",
          "displayName": "Informe Detallado en PDF",
          "description": "Informe completo en formato PDF"
        }
      }
    ]
  }
}
```

---

## ❌ Respuestas de Error

### **400 Bad Request - Validaciones:**

#### **1. Stripe Status No Aprobado:**
```json
{
  "message": "Tu cuenta de Stripe no está verificada. Por favor, completa el proceso de verificación.",
  "stripeStatus": "Pending",
  "requiresStripeSetup": false,
  "canRetry": false
}
```

#### **2. Tipo de Servicio Inválido:**
```json
{
  "message": "El tipo de servicio es requerido"
}
```

#### **3. Condiciones Vacías:**
```json
{
  "message": "El campo Condiciones es requerido"
}
```

#### **4. Precio Inválido:**
```json
{
  "message": "El precio debe ser mayor que 0"
}
```

#### **5. Duración Inválida:**
```json
{
  "message": "La duración debe ser mayor que 0"
}
```

#### **6. Servicio Duplicado (Misma Categoría Padre + Tipo de Servicio):**
```json
{
  "message": "Ya tienes un servicio activo en la categoría 'Vehículos' (subcategoría: 'Coches') con el tipo de servicio 'Inspección'. Solo puedes tener un servicio por combinación de categoría padre y tipo de servicio. Puedes actualizar tu servicio existente, crear uno con otro tipo de servicio en la misma categoría padre, o crear uno en otra categoría padre.",
  "existingServiceId": 100,
  "parentCategoryName": "Vehículos",
  "existingCategoryName": "Coches",
  "serviceTypeName": "Inspección"
}
```

#### **7. Expert Profile ID No Coincide:**
```json
{
  "message": "Expert profile ID does not match your profile"
}
```

#### **8. Categoría No Existe:**
```json
{
  "message": "La categoría seleccionada no existe"
}
```

### **401 Unauthorized:**

```json
{
  "message": "Invalid user identification"
}
```

### **403 Forbidden:**

```json
{
  "message": "Forbidden"
}
```
*Ocurre si el usuario no tiene rol `Expert`*

### **500 Internal Server Error:**

```json
{
  "message": "Failed to create search service",
  "detail": "Error details here"
}
```

---

## 🔍 Validaciones del Backend

### **1. Autenticación y Autorización:**
- ✅ Usuario debe estar autenticado
- ✅ Usuario debe tener rol `Expert`
- ✅ `ExpertProfileId` debe coincidir con el usuario autenticado

### **2. Estado de Stripe:**
- ✅ El experto debe tener `StripeStatus = Approved` y `OnboardingCompleted = true`
- ✅ **EXCEPCIÓN**: También permite `StripeStatus = PendingVerification` (durante verificación)

### **3. Validaciones de Campos:**
- ✅ `ServiceTypeId` > 0
- ✅ `Price` > 0
- ✅ `Conditions` no vacío
- ✅ `DurationInHours` > 0
- ✅ `CategoryId` debe existir y estar activa

### **4. Validación de Duplicados:**
- ✅ **NO** puede haber dos servicios activos con:
  - Misma **categoría padre** (si es subcategoría, se usa el `ParentId`)
  - Mismo **tipo de servicio** (`ServiceTypeId`)
- ✅ **SÍ** puede haber múltiples servicios con:
  - Misma categoría padre pero **diferente tipo de servicio**
  - Diferente categoría padre pero mismo tipo de servicio

---

## 💡 Notas Importantes

### **1. Precio con IVA Incluido:**
- ⚠️ El campo `Price` debe ser el **precio final con IVA incluido**
- ⚠️ Stripe calculará automáticamente el IVA según la ubicación del comprador
- ⚠️ Ejemplo: Si quieres que el cliente pague €110, establece `Price = 110`

### **2. SelectedDeliverableTypes:**
- ⚠️ Debe enviarse como **JSON string**, no como array directo
- ⚠️ Ejemplo: `JSON.stringify([1, 2, 3])` → `"[1,2,3]"`
- ⚠️ Si no se envía, el servicio se crea sin tipos de entregables seleccionados

### **3. Imágenes:**
- ⚠️ Se aceptan múltiples imágenes
- ⚠️ El formato y tamaño máximo dependen de la configuración del servidor
- ⚠️ Las imágenes se suben a Google Cloud Storage y se retornan las URLs

### **4. Categorías:**
- ⚠️ Puedes enviar tanto categorías padre como subcategorías
- ⚠️ El sistema determina automáticamente la categoría padre para validar duplicados
- ⚠️ Si envías una subcategoría, se usa su `ParentId` como categoría padre

---

## 📋 Ejemplo Completo de Uso

```typescript
// Ejemplo completo de creación de servicio
const handleCreateService = async () => {
  try {
    const serviceData = {
      expertProfileId: 45,
      categoryId: 5, // Puede ser categoría padre o subcategoría
      serviceTypeId: 2,
      price: 110.00, // Precio con IVA incluido
      conditions: "El servicio incluye inspección completa del vehículo, informe detallado y fotos.",
      durationInHours: 2,
      images: [
        new File(['...'], 'service1.jpg', { type: 'image/jpeg' }),
        new File(['...'], 'service2.jpg', { type: 'image/jpeg' })
      ],
      selectedDeliverableTypes: [1, 2, 3] // IDs de tipos de entregables
    };
    
    const result = await createService(serviceData);
    
    if (result.searchService) {
      console.log('Servicio creado:', result.searchService.id);
      console.log('Imágenes:', result.searchService.imageUrls);
    }
  } catch (error) {
    if (error.response?.status === 400) {
      const errorData = error.response.data;
      
      // Manejar error de servicio duplicado
      if (errorData.existingServiceId) {
        console.log('Ya existe un servicio:', errorData.existingServiceId);
        // Opción: Redirigir a actualizar servicio existente
      }
      
      // Manejar error de Stripe
      if (errorData.stripeStatus) {
        console.log('Stripe no verificado:', errorData.stripeStatus);
        // Opción: Redirigir a completar onboarding de Stripe
      }
    }
  }
};
```

---

## 🔗 Endpoints Relacionados

- **GET `/api/SearchService`** - Listar servicios del experto
- **PUT `/api/SearchService`** - Actualizar servicio existente
- **DELETE `/api/SearchService/{id}`** - Eliminar servicio
- **GET `/api/Subscription/expert-status`** - Verificar estado de Stripe antes de crear servicio

---

## ✅ Checklist para el Frontend

- [ ] Validar que el usuario tenga rol `Expert`
- [ ] Verificar estado de Stripe antes de mostrar formulario (`GET /api/Subscription/expert-status`)
- [ ] Validar que `Price` > 0
- [ ] Validar que `Conditions` no esté vacío
- [ ] Validar que `DurationInHours` > 0
- [ ] Convertir `SelectedDeliverableTypes` a JSON string antes de enviar
- [ ] Manejar error de servicio duplicado (ofrecer actualizar servicio existente)
- [ ] Manejar error de Stripe no verificado (redirigir a onboarding)
- [ ] Mostrar URLs de imágenes después de crear el servicio
- [ ] Mostrar mensaje de éxito con ID del servicio creado
