# Guía del Endpoint de Disputas para Frontend

## 🎯 Endpoint Principal

### Crear Disputa con Archivos
**URL:** `POST /api/Subscription/dispute-service`  
**Content-Type:** `multipart/form-data` (para archivos)  
**Autenticación:** Bearer Token requerido

## 📋 Estructura de la Petición

### Datos Obligatorios
```typescript
interface DisputeRequest {
  SearchHireId: number;    // ID del servicio contratado
  Reason: string;          // Razón de la disputa (máx 1000 caracteres)
  Files?: File[];          // Archivos opcionales (máx 10MB cada uno)
}
```

### Tipos de Archivo Permitidos
- **Imágenes:** `.jpg`, `.jpeg`, `.png`, `.gif`
- **Documentos:** `.pdf`, `.doc`, `.docx`
- **Videos:** `.mp4`, `.avi`, `.mov`

## 💻 Implementación Frontend

### 1. Función Principal para Crear Disputa

```typescript
interface DisputeFormData {
  searchHireId: number;
  reason: string;
  files?: File[];
}

const createDispute = async (formData: DisputeFormData): Promise<any> => {
  // Crear FormData para enviar archivos
  const requestFormData = new FormData();
  
  // Agregar datos obligatorios
  requestFormData.append('SearchHireId', formData.searchHireId.toString());
  requestFormData.append('Reason', formData.reason);
  
  // Agregar archivos si existen
  if (formData.files && formData.files.length > 0) {
    formData.files.forEach((file) => {
      requestFormData.append('Files', file);
    });
  }
  
  try {
    const response = await fetch('/api/Subscription/dispute-service', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${getAuthToken()}`, // Tu función para obtener el token
        // NO incluir Content-Type, el navegador lo establece automáticamente para FormData
      },
      body: requestFormData
    });
    
    const result = await response.json();
    
    if (!response.ok) {
      throw new Error(result.message || 'Error al crear la disputa');
    }
    
    return result;
  } catch (error) {
    console.error('Error creating dispute:', error);
    throw error;
  }
};
```

### 2. Validación de Archivos

```typescript
const validateFile = (file: File): string | null => {
  // Tipos permitidos
  const allowedTypes = [
    'image/jpeg', 'image/jpg', 'image/png', 'image/gif',
    'application/pdf', 
    'application/msword',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'video/mp4', 'video/avi', 'video/quicktime'
  ];
  
  // Tamaño máximo: 10MB
  const maxSize = 10 * 1024 * 1024;
  
  // Validar tipo
  if (!allowedTypes.includes(file.type)) {
    return `Tipo de archivo no permitido. Tipos válidos: JPG, PNG, GIF, PDF, DOC, DOCX, MP4, AVI, MOV`;
  }
  
  // Validar tamaño
  if (file.size > maxSize) {
    return `El archivo "${file.name}" excede el límite de 10MB`;
  }
  
  return null; // Archivo válido
};

const validateFiles = (files: File[]): string[] => {
  const errors: string[] = [];
  
  files.forEach((file) => {
    const error = validateFile(file);
    if (error) {
      errors.push(error);
    }
  });
  
  return errors;
};
```

### 3. Componente React Completo

```tsx
import React, { useState } from 'react';

interface DisputeFormProps {
  searchHireId: number;
  onSuccess?: (disputeId: number) => void;
  onError?: (error: string) => void;
}

const DisputeForm: React.FC<DisputeFormProps> = ({ 
  searchHireId, 
  onSuccess, 
  onError 
}) => {
  const [reason, setReason] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  const handleFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const selectedFiles = Array.from(event.target.files || []);
    
    // Validar archivos
    const validationErrors = validateFiles(selectedFiles);
    
    if (validationErrors.length > 0) {
      setErrors(validationErrors);
      return;
    }
    
    setErrors([]);
    setFiles(selectedFiles);
  };

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    setErrors([]);
    
    // Validar razón
    if (!reason.trim()) {
      setErrors(['La razón de la disputa es obligatoria']);
      return;
    }
    
    if (reason.length > 1000) {
      setErrors(['La razón no puede exceder 1000 caracteres']);
      return;
    }
    
    setIsSubmitting(true);
    
    try {
      const result = await createDispute({
        searchHireId,
        reason: reason.trim(),
        files
      });
      
      // Éxito
      console.log('Disputa creada:', result);
      onSuccess?.(result.disputeId);
      
      // Limpiar formulario
      setReason('');
      setFiles([]);
      
    } catch (error: any) {
      console.error('Error:', error);
      onError?.(error.message || 'Error al crear la disputa');
    } finally {
      setIsSubmitting(false);
    }
  };

  const removeFile = (index: number) => {
    setFiles(files.filter((_, i) => i !== index));
  };

  return (
    <div className="dispute-form-container">
      <h3>Crear Disputa</h3>
      
      <form onSubmit={handleSubmit} className="dispute-form">
        {/* Campo de razón */}
        <div className="form-group">
          <label htmlFor="reason">
            Razón de la disputa <span className="required">*</span>
          </label>
          <textarea
            id="reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={1000}
            required
            rows={4}
            placeholder="Describe el problema con el servicio..."
            className="form-control"
          />
          <small className="text-muted">
            {reason.length}/1000 caracteres
          </small>
        </div>

        {/* Campo de archivos */}
        <div className="form-group">
          <label htmlFor="files">
            Archivos de evidencia (opcional)
          </label>
          <input
            type="file"
            id="files"
            multiple
            accept=".jpg,.jpeg,.png,.gif,.pdf,.doc,.docx,.mp4,.avi,.mov"
            onChange={handleFileChange}
            className="form-control"
          />
          <small className="text-muted">
            Tipos permitidos: JPG, PNG, GIF, PDF, DOC, DOCX, MP4, AVI, MOV. 
            Tamaño máximo: 10MB por archivo.
          </small>
        </div>

        {/* Lista de archivos seleccionados */}
        {files.length > 0 && (
          <div className="selected-files">
            <h5>Archivos seleccionados:</h5>
            <ul className="file-list">
              {files.map((file, index) => (
                <li key={index} className="file-item">
                  <span className="file-name">{file.name}</span>
                  <span className="file-size">
                    ({(file.size / 1024 / 1024).toFixed(2)} MB)
                  </span>
                  <button
                    type="button"
                    onClick={() => removeFile(index)}
                    className="btn btn-sm btn-danger"
                  >
                    Eliminar
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}

        {/* Mensajes de error */}
        {errors.length > 0 && (
          <div className="alert alert-danger">
            <ul className="mb-0">
              {errors.map((error, index) => (
                <li key={index}>{error}</li>
              ))}
            </ul>
          </div>
        )}

        {/* Botón de envío */}
        <button
          type="submit"
          disabled={isSubmitting}
          className="btn btn-primary"
        >
          {isSubmitting ? 'Creando disputa...' : 'Crear Disputa'}
        </button>
      </form>
    </div>
  );
};

export default DisputeForm;
```

### 4. Hook Personalizado para Disputas

```typescript
import { useState } from 'react';

interface UseDisputeProps {
  onSuccess?: (disputeId: number) => void;
  onError?: (error: string) => void;
}

export const useDispute = ({ onSuccess, onError }: UseDisputeProps = {}) => {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const createDispute = async (formData: DisputeFormData) => {
    setIsLoading(true);
    setError(null);
    
    try {
      const result = await createDispute(formData);
      onSuccess?.(result.disputeId);
      return result;
    } catch (err: any) {
      const errorMessage = err.message || 'Error al crear la disputa';
      setError(errorMessage);
      onError?.(errorMessage);
      throw err;
    } finally {
      setIsLoading(false);
    }
  };

  return {
    createDispute,
    isLoading,
    error,
    clearError: () => setError(null)
  };
};
```

## 📤 Ejemplos de Uso

### Ejemplo 1: Disputa Simple (sin archivos)
```typescript
const handleSimpleDispute = async () => {
  try {
    const result = await createDispute({
      searchHireId: 71,
      reason: "El servicio no cumplió con las expectativas acordadas"
    });
    
    console.log('Disputa creada:', result.disputeId);
    alert('Disputa creada exitosamente');
  } catch (error) {
    console.error('Error:', error);
    alert('Error al crear la disputa');
  }
};
```

### Ejemplo 2: Disputa con Archivos
```typescript
const handleDisputeWithFiles = async (selectedFiles: File[]) => {
  try {
    const result = await createDispute({
      searchHireId: 71,
      reason: "El servicio no cumplió con las expectativas acordadas",
      files: selectedFiles
    });
    
    console.log('Disputa creada con archivos:', result.disputeId);
    alert('Disputa creada exitosamente con evidencia');
  } catch (error) {
    console.error('Error:', error);
    alert('Error al crear la disputa');
  }
};
```

## 📥 Respuestas del Servidor

### ✅ Éxito (200)
```json
{
  "message": "Dispute opened successfully",
  "disputeId": 123
}
```

### ❌ Errores Comunes

#### 400 - Razón faltante
```json
{
  "message": "Dispute reason is required"
}
```

#### 400 - Archivo no válido
```json
{
  "message": "File type .exe is not allowed. Allowed types: .jpg, .jpeg, .png, .gif, .pdf, .doc, .docx, .mp4, .avi, .mov"
}
```

#### 400 - Archivo muy grande
```json
{
  "message": "File size cannot exceed 10MB"
}
```

#### 404 - Servicio no encontrado
```json
{
  "message": "Service not found or unauthorized"
}
```

#### 400 - Estado incorrecto
```json
{
  "message": "Service is not awaiting client decision"
}
```

#### 401 - No autorizado
```json
{
  "message": "Invalid user identification"
}
```

## 🎨 Estilos CSS Sugeridos

```css
.dispute-form-container {
  max-width: 600px;
  margin: 0 auto;
  padding: 20px;
}

.dispute-form .form-group {
  margin-bottom: 20px;
}

.dispute-form label {
  display: block;
  margin-bottom: 5px;
  font-weight: bold;
}

.dispute-form .required {
  color: red;
}

.dispute-form .form-control {
  width: 100%;
  padding: 8px 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
}

.dispute-form textarea.form-control {
  resize: vertical;
  min-height: 100px;
}

.selected-files {
  margin: 15px 0;
  padding: 15px;
  background-color: #f8f9fa;
  border-radius: 4px;
}

.file-list {
  list-style: none;
  padding: 0;
  margin: 10px 0 0 0;
}

.file-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 0;
  border-bottom: 1px solid #eee;
}

.file-item:last-child {
  border-bottom: none;
}

.file-name {
  font-weight: 500;
  flex: 1;
}

.file-size {
  color: #666;
  margin: 0 10px;
}

.alert {
  padding: 12px 16px;
  border-radius: 4px;
  margin: 15px 0;
}

.alert-danger {
  background-color: #f8d7da;
  border: 1px solid #f5c6cb;
  color: #721c24;
}

.btn {
  padding: 10px 20px;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  font-size: 14px;
  text-decoration: none;
  display: inline-block;
}

.btn-primary {
  background-color: #007bff;
  color: white;
}

.btn-primary:hover:not(:disabled) {
  background-color: #0056b3;
}

.btn-primary:disabled {
  background-color: #6c757d;
  cursor: not-allowed;
}

.btn-danger {
  background-color: #dc3545;
  color: white;
  padding: 4px 8px;
  font-size: 12px;
}

.btn-danger:hover {
  background-color: #c82333;
}

.text-muted {
  color: #6c757d;
  font-size: 12px;
}
```

## 🔧 Funciones de Utilidad

```typescript
// Función para obtener el token de autenticación
const getAuthToken = (): string => {
  // Implementar según tu sistema de autenticación
  return localStorage.getItem('authToken') || '';
};

// Función para formatear tamaño de archivo
const formatFileSize = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

// Función para obtener icono de archivo
const getFileIcon = (fileType: string): string => {
  if (fileType.startsWith('image/')) return '🖼️';
  if (fileType.includes('pdf')) return '📄';
  if (fileType.includes('word')) return '📝';
  if (fileType.startsWith('video/')) return '🎥';
  return '📎';
};
```

## ⚠️ Puntos Importantes

1. **Content-Type:** NO incluyas `Content-Type` en los headers, el navegador lo establece automáticamente para `FormData`
2. **Autenticación:** Siempre incluye el token Bearer en el header `Authorization`
3. **Validación:** Valida archivos en el cliente antes de enviarlos
4. **Límites:** Respeta el límite de 10MB por archivo
5. **Estados:** Solo se pueden crear disputas en servicios con estado `awaiting_client_decision`
6. **Autorización:** Solo el cliente que contrató el servicio puede crear disputas

## 🧪 Testing

Para probar la funcionalidad:

```typescript
// Test 1: Disputa sin archivos
await createDispute({
  searchHireId: 71,
  reason: "Test dispute without files"
});

// Test 2: Disputa con archivos válidos
const testFile = new File(['test content'], 'test.jpg', { type: 'image/jpeg' });
await createDispute({
  searchHireId: 71,
  reason: "Test dispute with files",
  files: [testFile]
});

// Test 3: Archivo muy grande (debería fallar)
const largeFile = new File([new ArrayBuffer(11 * 1024 * 1024)], 'large.jpg', { type: 'image/jpeg' });
await createDispute({
  searchHireId: 71,
  reason: "Test with large file",
  files: [largeFile]
});
```

¡Con esta guía el frontend tiene todo lo necesario para implementar la funcionalidad de disputas con archivos!





