# ✅ Actualización: Backend Ahora Devuelve IDs de Imágenes

## 🎯 Cambio Implementado

El backend ahora devuelve los **IDs reales de las imágenes** en la respuesta de los servicios, permitiendo que el frontend pueda eliminarlas correctamente.

---

## 📋 Cambios en el Backend

### **1. Nuevo DTO: `SearchServiceImageDto`**

```csharp
public class SearchServiceImageDto
{
    public int Id { get; set; }
    public string Url { get; set; }
}
```

### **2. Actualización de `SearchServiceResponseDto`**

```csharp
public class SearchServiceResponseDto
{
    // ... otros campos ...
    
    public List<string> ImageUrls { get; set; } // ✅ Mantiene compatibilidad
    public List<SearchServiceImageDto> Images { get; set; } = new List<SearchServiceImageDto>(); // ✅ NUEVO
}
```

### **3. Métodos Actualizados**

- ✅ `MapToResponseDto()` - Ahora incluye el campo `Images` con IDs
- ✅ `MapToDetailDto()` - Hereda de `SearchServiceResponseDto`, incluye `Images` automáticamente

---

## 📡 Respuesta del Backend (Ejemplo)

### **Antes (Solo URLs)**

```json
{
  "id": 247,
  "imageUrls": [
    "https://storage.googleapis.com/.../image1.jpg",
    "https://storage.googleapis.com/.../image2.jpg"
  ]
}
```

### **Ahora (URLs + IDs)**

```json
{
  "id": 247,
  "imageUrls": [
    "https://storage.googleapis.com/.../image1.jpg",
    "https://storage.googleapis.com/.../image2.jpg"
  ],
  "images": [
    {
      "id": 123,
      "url": "https://storage.googleapis.com/.../image1.jpg"
    },
    {
      "id": 124,
      "url": "https://storage.googleapis.com/.../image2.jpg"
    }
  ]
}
```

---

## 🔄 Endpoints Afectados

Los siguientes endpoints ahora devuelven el campo `Images` con IDs:

1. ✅ `GET /api/SearchService/expert/{expertId}` - Servicios del experto
2. ✅ `GET /api/SearchService/{id}` - Detalle de un servicio
3. ✅ `PUT /api/SearchService/update` - Respuesta después de actualizar (incluye `Images` actualizadas)

---

## ⚠️ Compatibilidad

- ✅ El campo `ImageUrls` se mantiene para **compatibilidad hacia atrás**
- ✅ El nuevo campo `Images` es **opcional** (puede estar vacío si no hay imágenes)
- ✅ El frontend puede usar `Images` cuando esté disponible, o `ImageUrls` como fallback

---

## 🎯 Próximos Pasos para el Frontend

1. **Actualizar la carga de servicios** para usar el campo `Images` cuando esté disponible
2. **Usar los IDs reales** del campo `Images` para eliminar imágenes
3. **Validar que los IDs sean positivos** antes de intentar eliminar (IDs negativos son temporales y no funcionan)

---

## 📝 Notas Técnicas

- Los IDs son los `Id` de la tabla `SearchServiceImages` en la base de datos
- Los IDs siempre son **positivos** (mayores que 0)
- Si un servicio no tiene imágenes, `Images` será un array vacío `[]`
- El campo `ImageUrls` sigue funcionando igual que antes para compatibilidad
