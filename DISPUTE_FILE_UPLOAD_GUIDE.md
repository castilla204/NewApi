# Guía para Manejo de Archivos en Disputas

## Resumen de Cambios

Se ha solucionado el error 404 en el endpoint de disputas y se ha añadido funcionalidad completa para subir archivos (imágenes, documentos, videos) como evidencia en las disputas.

## Endpoint Corregido

**URL:** `POST /api/Subscription/dispute-service`

**Problema anterior:** El endpoint estaba registrado en la API pero no existía en el código, causando error 404.

**Solución:** Se ha creado el endpoint completo con soporte para archivos.

## Funcionalidad de Archivos

### Tipos de Archivo Soportados
- **Imágenes:** `.jpg`, `.jpeg`, `.png`, `.gif`
- **Documentos:** `.pdf`, `.doc`, `.docx`
- **Videos:** `.mp4`, `.avi`, `.mov`

### Límites
- **Tamaño máximo:** 10MB por archivo
- **Cantidad:** Sin límite específico (limitado por el tamaño total de la petición)

## Implementación Frontend

### 1. Formulario de Disputa

```typescript
interface DisputeFormData {
  searchHireId: number;
  reason: string;
  files?: File[];
}

const createDispute = async (formData: DisputeFormData) => {
  const formDataToSend = new FormData();
  
  // Agregar datos básicos
  formDataToSend.append('SearchHireId', formData.searchHireId.toString());
  formDataToSend.append('Reason', formData.reason);
  
  // Agregar archivos
  if (formData.files && formData.files.length > 0) {
    formData.files.forEach((file, index) => {
      formDataToSend.append('Files', file);
    });
  }
  
  const response = await fetch('/api/Subscription/dispute-service', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}` // Token de autenticación
    },
    body: formDataToSend
  });
  
  return response.json();
};
```

### 2. Validación de Archivos

```typescript
const validateFile = (file: File): string | null => {
  const allowedTypes = [
    'image/jpeg', 'image/jpg', 'image/png', 'image/gif',
    'application/pdf', 'application/msword', 
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'video/mp4', 'video/avi', 'video/quicktime'
  ];
  
  const maxSize = 10 * 1024 * 1024; // 10MB
  
  if (!allowedTypes.includes(file.type)) {
    return 'Tipo de archivo no permitido. Tipos válidos: JPG, PNG, GIF, PDF, DOC, DOCX, MP4, AVI, MOV';
  }
  
  if (file.size > maxSize) {
    return 'El archivo no puede exceder 10MB';
  }
  
  return null;
};

const validateFiles = (files: File[]): string[] => {
  const errors: string[] = [];
  
  files.forEach((file, index) => {
    const error = validateFile(file);
    if (error) {
      errors.push(`Archivo ${index + 1}: ${error}`);
    }
  });
  
  return errors;
};
```

### 3. Componente React de Ejemplo

```tsx
import React, { useState } from 'react';

interface DisputeFormProps {
  searchHireId: number;
  onSubmit: (data: DisputeFormData) => void;
}

const DisputeForm: React.FC<DisputeFormProps> = ({ searchHireId, onSubmit }) => {
  const [reason, setReason] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [errors, setErrors] = useState<string[]>([]);

  const handleFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const selectedFiles = Array.from(event.target.files || []);
    const validationErrors = validateFiles(selectedFiles);
    
    if (validationErrors.length > 0) {
      setErrors(validationErrors);
      return;
    }
    
    setErrors([]);
    setFiles(selectedFiles);
  };

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault();
    
    if (!reason.trim()) {
      setErrors(['La razón de la disputa es obligatoria']);
      return;
    }
    
    onSubmit({
      searchHireId,
      reason,
      files
    });
  };

  return (
    <form onSubmit={handleSubmit} className="dispute-form">
      <div className="form-group">
        <label htmlFor="reason">Razón de la disputa:</label>
        <textarea
          id="reason"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          maxLength={1000}
          required
          rows={4}
        />
        <small>{reason.length}/1000 caracteres</small>
      </div>

      <div className="form-group">
        <label htmlFor="files">Archivos de evidencia (opcional):</label>
        <input
          type="file"
          id="files"
          multiple
          accept=".jpg,.jpeg,.png,.gif,.pdf,.doc,.docx,.mp4,.avi,.mov"
          onChange={handleFileChange}
        />
        <small>
          Tipos permitidos: JPG, PNG, GIF, PDF, DOC, DOCX, MP4, AVI, MOV. 
          Tamaño máximo: 10MB por archivo.
        </small>
      </div>

      {files.length > 0 && (
        <div className="file-list">
          <h4>Archivos seleccionados:</h4>
          <ul>
            {files.map((file, index) => (
              <li key={index}>
                {file.name} ({(file.size / 1024 / 1024).toFixed(2)} MB)
              </li>
            ))}
          </ul>
        </div>
      )}

      {errors.length > 0 && (
        <div className="error-messages">
          {errors.map((error, index) => (
            <div key={index} className="error">{error}</div>
          ))}
        </div>
      )}

      <button type="submit" className="btn btn-primary">
        Crear Disputa
      </button>
    </form>
  );
};
```

### 4. Manejo de Respuestas

```typescript
const handleDisputeSubmission = async (formData: DisputeFormData) => {
  try {
    const response = await createDispute(formData);
    
    if (response.message === 'Dispute opened successfully') {
      // Éxito
      showSuccessMessage('Disputa creada exitosamente');
      // Redirigir o actualizar la UI
    } else {
      // Error del servidor
      showErrorMessage(response.message || 'Error al crear la disputa');
    }
  } catch (error) {
    // Error de red o validación
    if (error.response?.status === 400) {
      showErrorMessage(error.response.data.message);
    } else if (error.response?.status === 404) {
      showErrorMessage('Servicio no encontrado o no autorizado');
    } else {
      showErrorMessage('Error de conexión. Inténtalo de nuevo.');
    }
  }
};
```

### 5. Visualización de Archivos en Disputas

```typescript
interface DisputeFile {
  id: number;
  fileName: string;
  filePath: string;
  fileType: string;
  fileSize: number;
  createdAt: string;
  fileUrl: string;
}

const DisputeFileViewer: React.FC<{ files: DisputeFile[] }> = ({ files }) => {
  const getFileIcon = (fileType: string) => {
    if (fileType.startsWith('image/')) return '🖼️';
    if (fileType.includes('pdf')) return '📄';
    if (fileType.includes('word')) return '📝';
    if (fileType.startsWith('video/')) return '🎥';
    return '📎';
  };

  const formatFileSize = (bytes: number) => {
    return (bytes / 1024 / 1024).toFixed(2) + ' MB';
  };

  return (
    <div className="dispute-files">
      <h4>Archivos adjuntos:</h4>
      {files.length === 0 ? (
        <p>No hay archivos adjuntos</p>
      ) : (
        <div className="file-grid">
          {files.map((file) => (
            <div key={file.id} className="file-item">
              <div className="file-icon">{getFileIcon(file.fileType)}</div>
              <div className="file-info">
                <div className="file-name">{file.fileName}</div>
                <div className="file-size">{formatFileSize(file.fileSize)}</div>
              </div>
              <a 
                href={file.fileUrl} 
                target="_blank" 
                rel="noopener noreferrer"
                className="btn btn-sm btn-outline"
              >
                Ver
              </a>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
```

## Estructura de Respuesta

### Éxito (200)
```json
{
  "message": "Dispute opened successfully",
  "disputeId": 123
}
```

### Errores Comunes

#### 400 - Bad Request
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

## Consideraciones de Seguridad

1. **Validación del lado del cliente:** Siempre validar archivos antes de enviarlos
2. **Autenticación:** Incluir token de autorización en todas las peticiones
3. **Autorización:** Solo el cliente que contrató el servicio puede crear disputas
4. **Límites de tamaño:** Respetar el límite de 10MB por archivo
5. **Tipos de archivo:** Solo permitir tipos seguros y relevantes

## Notas Adicionales

- Los archivos se almacenan en `wwwroot/uploads/disputes/`
- Los nombres de archivo se generan automáticamente para evitar conflictos
- Se mantiene el nombre original en la base de datos para referencia
- Los archivos se asocian automáticamente con la disputa creada
- La transacción es atómica: si falla la subida de archivos, se revierte toda la operación

## Testing

Para probar la funcionalidad:

1. Crear una disputa sin archivos
2. Crear una disputa con archivos válidos
3. Intentar subir archivos no válidos (debería fallar)
4. Intentar subir archivos muy grandes (debería fallar)
5. Verificar que los archivos se muestren correctamente en la UI





