# 📸 Documentación: Actualización de Servicios - Conservación de Imágenes

## 🎯 Resumen

Al actualizar un servicio, **las imágenes existentes se conservan automáticamente** a menos que se eliminen explícitamente. Esto permite actualizar otros campos del servicio sin perder las imágenes ya subidas.

---

## 📋 Lógica Completa

### **Flujo de Actualización de Imágenes**

1. **Cargar imágenes existentes**: Se obtienen todas las imágenes del servicio que se está actualizando
2. **Procesar eliminaciones explícitas**: Si se proporciona `ImagesToDelete`, se filtran las imágenes a eliminar
3. **Conservar imágenes restantes**: Todas las imágenes que NO están en `ImagesToDelete` se copian al nuevo servicio
4. **Agregar nuevas imágenes**: Si se proporcionan nuevas imágenes en `Images`, se suben a GCS y se agregan al servicio
5. **Guardar todo**: Se guardan todas las imágenes (conservadas + nuevas) en una sola transacción

---

## 🔌 API Endpoint

### **PUT `/api/SearchService/update`**

**Content-Type**: `multipart/form-data`

### **Parámetros del FormData**

| Campo | Tipo | Requerido | Descripción |
|-------|------|-----------|-------------|
| `ServiceId` | `int` | ✅ Sí | ID del servicio a actualizar |
| `CategoryId` | `int` | ✅ Sí | ID de la categoría |
| `ServiceTypeId` | `int` | ✅ Sí | ID del tipo de servicio |
| `Price` | `decimal` | ✅ Sí | Precio del servicio (con IVA incluido) |
| `Conditions` | `string` | ✅ Sí | Condiciones del servicio |
| `DurationInHours` | `int?` | ❌ No | Duración en horas |
| `Images` | `File[]` | ❌ No | **Nuevas imágenes a agregar** |
| `ImagesToDelete` | `string` | ❌ No | **JSON array de IDs de imágenes a eliminar** (ej: `"[1, 2, 3]"`) |
| `SelectedDeliverableTypes` | `string` | ✅ Sí | JSON array de IDs de tipos de entregables (ej: `"[1, 2]"`) |

---

## 💡 Casos de Uso

### **Caso 1: Actualizar solo datos (sin tocar imágenes)**

```javascript
const formData = new FormData();
formData.append('ServiceId', '123');
formData.append('CategoryId', '5');
formData.append('ServiceTypeId', '2');
formData.append('Price', '100.00');
formData.append('Conditions', 'Nuevas condiciones');
formData.append('SelectedDeliverableTypes', '[1, 2]');
// ❌ NO incluir Images ni ImagesToDelete
// ✅ Resultado: Todas las imágenes existentes se conservan
```

### **Caso 2: Agregar nuevas imágenes (conservar existentes)**

```javascript
const formData = new FormData();
formData.append('ServiceId', '123');
formData.append('CategoryId', '5');
formData.append('ServiceTypeId', '2');
formData.append('Price', '100.00');
formData.append('Conditions', 'Nuevas condiciones');
formData.append('SelectedDeliverableTypes', '[1, 2]');

// ✅ Agregar nuevas imágenes
formData.append('Images', file1);
formData.append('Images', file2);
// ❌ NO incluir ImagesToDelete
// ✅ Resultado: Imágenes existentes + nuevas imágenes
```

### **Caso 3: Eliminar imágenes específicas (conservar el resto)**

```javascript
const formData = new FormData();
formData.append('ServiceId', '123');
formData.append('CategoryId', '5');
formData.append('ServiceTypeId', '2');
formData.append('Price', '100.00');
formData.append('Conditions', 'Nuevas condiciones');
formData.append('SelectedDeliverableTypes', '[1, 2]');

// ✅ Eliminar imágenes específicas por ID
formData.append('ImagesToDelete', '[5, 7]'); // Elimina imágenes con ID 5 y 7
// ❌ NO incluir Images (si no quieres agregar nuevas)
// ✅ Resultado: Todas las imágenes excepto las eliminadas
```

### **Caso 4: Eliminar algunas y agregar nuevas**

```javascript
const formData = new FormData();
formData.append('ServiceId', '123');
formData.append('CategoryId', '5');
formData.append('ServiceTypeId', '2');
formData.append('Price', '100.00');
formData.append('Conditions', 'Nuevas condiciones');
formData.append('SelectedDeliverableTypes', '[1, 2]');

// ✅ Eliminar imágenes específicas
formData.append('ImagesToDelete', '[5, 7]');

// ✅ Agregar nuevas imágenes
formData.append('Images', file1);
formData.append('Images', file2);
// ✅ Resultado: Imágenes existentes (excepto las eliminadas) + nuevas imágenes
```

### **Caso 5: Reemplazar todas las imágenes**

```javascript
// Primero, obtener todas las imágenes del servicio actual
const currentService = await getService(123);
const allImageIds = currentService.ImageUrls.map((url, index) => 
  currentService.Images[index].Id // Asumiendo que tienes acceso a los IDs
);

const formData = new FormData();
formData.append('ServiceId', '123');
formData.append('CategoryId', '5');
formData.append('ServiceTypeId', '2');
formData.append('Price', '100.00');
formData.append('Conditions', 'Nuevas condiciones');
formData.append('SelectedDeliverableTypes', '[1, 2]');

// ✅ Eliminar TODAS las imágenes existentes
formData.append('ImagesToDelete', JSON.stringify(allImageIds));

// ✅ Agregar nuevas imágenes
formData.append('Images', newFile1);
formData.append('Images', newFile2);
// ✅ Resultado: Solo las nuevas imágenes
```

---

## 📝 Ejemplo Completo (React/TypeScript)

```typescript
interface ServiceImage {
  id: number;
  url: string;
}

interface UpdateServiceForm {
  serviceId: number;
  categoryId: number;
  serviceTypeId: number;
  price: number;
  conditions: string;
  durationInHours?: number;
  selectedDeliverableTypes: number[];
  // Nuevas imágenes a agregar
  newImages?: File[];
  // IDs de imágenes a eliminar
  imagesToDelete?: number[];
}

async function updateService(form: UpdateServiceForm) {
  const formData = new FormData();
  
  // Campos obligatorios
  formData.append('ServiceId', form.serviceId.toString());
  formData.append('CategoryId', form.categoryId.toString());
  formData.append('ServiceTypeId', form.serviceTypeId.toString());
  formData.append('Price', form.price.toString());
  formData.append('Conditions', form.conditions);
  formData.append('SelectedDeliverableTypes', JSON.stringify(form.selectedDeliverableTypes));
  
  // Duración (opcional)
  if (form.durationInHours) {
    formData.append('DurationInHours', form.durationInHours.toString());
  }
  
  // ✅ Nuevas imágenes a agregar
  if (form.newImages && form.newImages.length > 0) {
    form.newImages.forEach(file => {
      formData.append('Images', file);
    });
  }
  
  // ✅ Imágenes a eliminar explícitamente
  if (form.imagesToDelete && form.imagesToDelete.length > 0) {
    formData.append('ImagesToDelete', JSON.stringify(form.imagesToDelete));
  }
  
  const response = await fetch('/api/SearchService/update', {
    method: 'PUT',
    headers: {
      'Authorization': `Bearer ${token}`
    },
    body: formData
  });
  
  return response.json();
}

// Ejemplo de uso:
const result = await updateService({
  serviceId: 123,
  categoryId: 5,
  serviceTypeId: 2,
  price: 100.00,
  conditions: 'Nuevas condiciones',
  selectedDeliverableTypes: [1, 2],
  newImages: [file1, file2], // Agregar 2 nuevas imágenes
  imagesToDelete: [5, 7]     // Eliminar imágenes con ID 5 y 7
});
```

---

## ⚠️ Comportamiento Importante

### **Conservación Automática**

- ✅ **Por defecto, TODAS las imágenes existentes se conservan**
- ✅ Solo se eliminan las imágenes especificadas en `ImagesToDelete`
- ✅ Si `ImagesToDelete` está vacío o no se proporciona, **ninguna imagen se elimina**

### **Orden de Operaciones**

1. Primero se eliminan las imágenes especificadas en `ImagesToDelete`
2. Luego se conservan las imágenes restantes
3. Finalmente se agregan las nuevas imágenes de `Images`

### **Validación**

- Si `ImagesToDelete` contiene IDs que no existen, se ignoran silenciosamente
- Si `ImagesToDelete` tiene formato JSON inválido, se registra un warning pero el proceso continúa
- Las imágenes se validan antes de subirlas (formato, tamaño, etc.)

---

## 🔄 Respuesta del API

```json
{
  "message": "Search service updated successfully",
  "service": {
    "id": 456,
    "categoryId": 5,
    "serviceTypeId": 2,
    "price": 100.00,
    "conditions": "Nuevas condiciones",
    "imageUrls": [
      "https://storage.googleapis.com/bucket/services/image1.jpg",
      "https://storage.googleapis.com/bucket/services/image2.jpg",
      "https://storage.googleapis.com/bucket/services/image3.jpg"
    ],
    "selectedDeliverableTypes": [...]
  },
  "originalServiceId": 123
}
```

**Nota**: El `originalServiceId` es el ID del servicio anterior (ahora inactivo). El `service.id` es el ID del nuevo servicio (activo).

---

## 🎨 Recomendaciones para el Frontend

### **1. UI para Gestión de Imágenes**

```typescript
// Componente de gestión de imágenes
function ImageManager({ 
  currentImages, 
  onImagesChange 
}: {
  currentImages: ServiceImage[];
  onImagesChange: (imagesToDelete: number[], newImages: File[]) => void;
}) {
  const [imagesToDelete, setImagesToDelete] = useState<number[]>([]);
  const [newImages, setNewImages] = useState<File[]>([]);
  
  const handleDeleteImage = (imageId: number) => {
    setImagesToDelete(prev => [...prev, imageId]);
  };
  
  const handleAddImages = (files: File[]) => {
    setNewImages(prev => [...prev, ...files]);
  };
  
  // Al enviar el formulario:
  onImagesChange(imagesToDelete, newImages);
  
  return (
    <div>
      {/* Mostrar imágenes existentes con botón de eliminar */}
      {currentImages.map(img => (
        <div key={img.id}>
          <img src={img.url} />
          <button onClick={() => handleDeleteImage(img.id)}>
            Eliminar
          </button>
        </div>
      ))}
      
      {/* Input para agregar nuevas imágenes */}
      <input 
        type="file" 
        multiple 
        accept="image/*"
        onChange={(e) => handleAddImages(Array.from(e.target.files || []))}
      />
    </div>
  );
}
```

### **2. Estado del Formulario**

```typescript
// Mantener estado de imágenes
const [serviceImages, setServiceImages] = useState<ServiceImage[]>([]);
const [imagesToDelete, setImagesToDelete] = useState<number[]>([]);
const [newImages, setNewImages] = useState<File[]>([]);

// Al cargar el servicio
useEffect(() => {
  loadService(serviceId).then(service => {
    setServiceImages(service.images);
  });
}, [serviceId]);

// Al enviar
const handleSubmit = async () => {
  await updateService({
    ...formData,
    imagesToDelete: imagesToDelete,
    newImages: newImages
  });
};
```

---

## ✅ Resumen de Cambios

### **Antes (Comportamiento Anterior)**
- ❌ Al actualizar un servicio, **todas las imágenes se perdían**
- ❌ Era necesario re-subir todas las imágenes cada vez

### **Ahora (Nuevo Comportamiento)**
- ✅ Las imágenes existentes **se conservan automáticamente**
- ✅ Solo se eliminan las imágenes especificadas en `ImagesToDelete`
- ✅ Se pueden agregar nuevas imágenes sin perder las existentes
- ✅ Control total sobre qué imágenes conservar y cuáles eliminar

---

## 🚀 Ejemplo de Flujo Completo

1. **Usuario carga el formulario de edición**
   - Se muestran todas las imágenes actuales del servicio

2. **Usuario elimina algunas imágenes**
   - Marca las imágenes a eliminar (se agregan a `imagesToDelete`)

3. **Usuario agrega nuevas imágenes**
   - Selecciona nuevos archivos (se agregan a `newImages`)

4. **Usuario actualiza otros campos**
   - Cambia precio, condiciones, etc.

5. **Usuario envía el formulario**
   - Backend conserva imágenes existentes (excepto las eliminadas)
   - Backend agrega nuevas imágenes
   - Backend actualiza otros campos

6. **Resultado**
   - Servicio actualizado con: imágenes conservadas + nuevas imágenes

---

## 📞 Soporte

Si tienes dudas sobre la implementación, revisa:
- `Services/SearchServiceService.cs` - Método `UpdateSearchService`
- `DataLayer/Models/DTOs/SearchHireDto.cs` - Clase `UpdateSearchServiceRequestDto`
