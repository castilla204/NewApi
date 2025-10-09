# 🎯 Endpoint de Disputas - Guía Simple para Frontend

## 📍 **Endpoint**
```
POST /api/Dispute/dispute-service
```

## 🔧 **Configuración**
- **Content-Type:** `multipart/form-data`
- **Authorization:** `Bearer {token}`

## 📤 **Datos a Enviar**

### **Campos Obligatorios:**
- `SearchHireId` (number) - ID del servicio contratado
- `Reason` (string) - Razón de la disputa (máx 1000 caracteres)

### **Campos Opcionales:**
- `Files` (File[]) - Archivos de evidencia

## 📁 **Archivos Permitidos**
- **Imágenes:** JPG, JPEG, PNG, GIF
- **Documentos:** PDF, DOC, DOCX  
- **Videos:** MP4, AVI, MOV
- **Tamaño máximo:** 10MB por archivo

## ☁️ **Almacenamiento**
Los archivos se suben a **Google Cloud Storage** igual que las imágenes de expertos y servicios.

## 💻 **Ejemplo de Uso**

### **Sin archivos:**
```javascript
const formData = new FormData();
formData.append('SearchHireId', '71');
formData.append('Reason', 'no me ha gustado mucho');

fetch('/api/Dispute/dispute-service', {
  method: 'POST',
  headers: { 'Authorization': `Bearer ${token}` },
  body: formData
});
```

### **Con archivos:**
```javascript
const formData = new FormData();
formData.append('SearchHireId', '71');
formData.append('Reason', 'no me ha gustado mucho');
formData.append('Files', archivo1);
formData.append('Files', archivo2);

fetch('/api/Dispute/dispute-service', {
  method: 'POST',
  headers: { 'Authorization': `Bearer ${token}` },
  body: formData
});
```

## ✅ **Respuesta Exitosa**
```json
{
  "message": "Dispute opened successfully",
  "disputeId": 123
}
```

## ❌ **Errores Comunes**
- **400:** Datos inválidos o archivo no permitido
- **401:** Token de autorización inválido
- **404:** Servicio no encontrado
- **500:** Error del servidor
