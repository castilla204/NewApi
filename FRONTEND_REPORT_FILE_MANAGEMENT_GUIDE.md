# Guía Completa de Gestión de Archivos y Reportes - Frontend

## Resumen de Mejoras Implementadas

Se han implementado las siguientes mejoras para resolver los problemas identificados:

1. **✅ Mensajes de error específicos**: Ahora especifica exactamente qué archivos faltan (PDF, MP4, etc.)
2. **✅ Funcionalidad unificada**: Un solo endpoint para subir archivos y enviar reporte
3. **✅ Gestión completa de archivos**: Endpoints para ver, subir y eliminar archivos antes de enviar
4. **✅ Validación previa**: Endpoint para verificar archivos requeridos antes del envío

## Nuevos Endpoints Disponibles

### 1. Validar Archivos Requeridos
**GET** `/api/appointment/validate-files/{appointmentId}`

Verifica qué archivos están subidos y cuáles faltan antes de enviar el reporte.

**Respuesta exitosa:**
```json
{
  "isValid": false,
  "missingFiles": ["PDF", "MP4"],
  "uploadedFiles": [
    {
      "id": 123,
      "type": "pdf",
      "fileName": "reporte_final.pdf",
      "createdAt": "2024-01-15T10:30:00Z"
    }
  ],
  "requiredTypes": ["PDF", "Video"]
}
```

**Ejemplo de uso en JavaScript:**
```javascript
async function validateFiles(appointmentId) {
  try {
    const response = await fetch(`/api/appointment/validate-files/${appointmentId}`, {
      method: 'GET',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      }
    });
    
    const result = await response.json();
    
    if (result.isValid) {
      console.log('✅ Todos los archivos requeridos están subidos');
      return { canSubmit: true, message: 'Listo para enviar reporte' };
    } else {
      const missingText = result.missingFiles.join(' y ');
      return { 
        canSubmit: false, 
        message: `Faltan archivos obligatorios: ${missingText}` 
      };
    }
  } catch (error) {
    console.error('Error validando archivos:', error);
    return { canSubmit: false, message: 'Error al validar archivos' };
  }
}
```

### 2. Obtener Archivos Subidos
**GET** `/api/appointment/files/{appointmentId}`

Obtiene la lista de archivos ya subidos para una cita.

**Respuesta exitosa:**
```json
{
  "files": [
    {
      "id": 123,
      "fileName": "reporte_final.pdf",
      "fileType": "pdf",
      "url": "https://storage.googleapis.com/bucket/deliverables/uuid.pdf",
      "createdAt": "2024-01-15T10:30:00Z"
    },
    {
      "id": 124,
      "fileName": "video_explicativo.mp4",
      "fileType": "video",
      "url": "https://storage.googleapis.com/bucket/deliverables/uuid.mp4",
      "createdAt": "2024-01-15T10:35:00Z"
    }
  ]
}
```

**Ejemplo de uso en JavaScript:**
```javascript
async function getUploadedFiles(appointmentId) {
  try {
    const response = await fetch(`/api/appointment/files/${appointmentId}`, {
      method: 'GET',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      }
    });
    
    const result = await response.json();
    return result.files;
  } catch (error) {
    console.error('Error obteniendo archivos:', error);
    return [];
  }
}
```

### 3. Eliminar Archivo
**DELETE** `/api/appointment/files/{appointmentId}/{deliverableId}`

Elimina un archivo específico antes de enviar el reporte.

**Respuesta exitosa:**
```json
{
  "message": "Archivo eliminado exitosamente"
}
```

**Ejemplo de uso en JavaScript:**
```javascript
async function deleteFile(appointmentId, deliverableId) {
  try {
    const response = await fetch(`/api/appointment/files/${appointmentId}/${deliverableId}`, {
      method: 'DELETE',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (response.ok) {
      const result = await response.json();
      console.log('✅ Archivo eliminado:', result.message);
      return { success: true, message: result.message };
    } else {
      const error = await response.json();
      return { success: false, message: error.message };
    }
  } catch (error) {
    console.error('Error eliminando archivo:', error);
    return { success: false, message: 'Error al eliminar archivo' };
  }
}
```

### 4. Subir Archivos y Enviar Reporte (UNIFICADO)
**POST** `/api/appointment/submit-report-with-files/{appointmentId}`

**NUEVO ENDPOINT PRINCIPAL** - Sube archivos y envía el reporte en una sola operación.

**FormData requerido:**
- `notes` (string, opcional): Notas del reporte
- `files` (File[], opcional): Archivos a subir (PDF y/o MP4)

**Respuesta exitosa:**
```json
{
  "message": "Reporte enviado exitosamente con archivos adjuntos",
  "appointment": {
    "id": 123,
    "status": "appointment_report_sent",
    // ... resto de datos de la cita
  }
}
```

**Ejemplo de uso en JavaScript:**
```javascript
async function submitReportWithFiles(appointmentId, files, notes = '') {
  try {
    const formData = new FormData();
    
    // Agregar notas si las hay
    if (notes) {
      formData.append('notes', notes);
    }
    
    // Agregar archivos
    if (files && files.length > 0) {
      files.forEach(file => {
        formData.append('files', file);
      });
    }
    
    const response = await fetch(`/api/appointment/submit-report-with-files/${appointmentId}`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`
        // NO incluir Content-Type para FormData
      },
      body: formData
    });
    
    if (response.ok) {
      const result = await response.json();
      console.log('✅ Reporte enviado:', result.message);
      return { success: true, appointment: result.appointment };
    } else {
      const error = await response.json();
      console.error('❌ Error enviando reporte:', error.message);
      return { success: false, message: error.message };
    }
  } catch (error) {
    console.error('Error enviando reporte:', error);
    return { success: false, message: 'Error al enviar reporte' };
  }
}
```

## Implementación Completa en el Frontend

### Componente React de Ejemplo

```jsx
import React, { useState, useEffect } from 'react';

const ReportSubmission = ({ appointmentId }) => {
  const [files, setFiles] = useState([]);
  const [uploadedFiles, setUploadedFiles] = useState([]);
  const [notes, setNotes] = useState('');
  const [validation, setValidation] = useState(null);
  const [loading, setLoading] = useState(false);

  // Cargar archivos existentes al montar el componente
  useEffect(() => {
    loadUploadedFiles();
    validateFiles();
  }, [appointmentId]);

  const loadUploadedFiles = async () => {
    const files = await getUploadedFiles(appointmentId);
    setUploadedFiles(files);
  };

  const validateFiles = async () => {
    const result = await validateFiles(appointmentId);
    setValidation(result);
  };

  const handleFileSelect = (event) => {
    const selectedFiles = Array.from(event.target.files);
    setFiles(selectedFiles);
  };

  const removeFile = async (deliverableId) => {
    const result = await deleteFile(appointmentId, deliverableId);
    if (result.success) {
      await loadUploadedFiles();
      await validateFiles();
    }
  };

  const handleSubmit = async () => {
    setLoading(true);
    
    try {
      // Validar antes de enviar
      const validation = await validateFiles(appointmentId);
      if (!validation.canSubmit) {
        alert(validation.message);
        return;
      }

      // Enviar reporte con archivos
      const result = await submitReportWithFiles(appointmentId, files, notes);
      
      if (result.success) {
        alert('✅ Reporte enviado exitosamente');
        // Redirigir o actualizar estado
      } else {
        alert(`❌ Error: ${result.message}`);
      }
    } catch (error) {
      alert('❌ Error inesperado al enviar reporte');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="report-submission">
      <h3>Enviar Reporte</h3>
      
      {/* Validación de archivos */}
      {validation && (
        <div className={`validation ${validation.canSubmit ? 'success' : 'error'}`}>
          {validation.canSubmit ? (
            <p>✅ {validation.message}</p>
          ) : (
            <p>❌ {validation.message}</p>
          )}
        </div>
      )}

      {/* Archivos ya subidos */}
      {uploadedFiles.length > 0 && (
        <div className="uploaded-files">
          <h4>Archivos Subidos:</h4>
          {uploadedFiles.map(file => (
            <div key={file.id} className="file-item">
              <span>{file.fileName} ({file.fileType})</span>
              <button 
                onClick={() => removeFile(file.id)}
                className="btn-remove"
              >
                Eliminar
              </button>
            </div>
          ))}
        </div>
      )}

      {/* Selección de nuevos archivos */}
      <div className="file-upload">
        <label htmlFor="files">Seleccionar Archivos (PDF, MP4):</label>
        <input
          id="files"
          type="file"
          multiple
          accept=".pdf,.mp4"
          onChange={handleFileSelect}
        />
        {files.length > 0 && (
          <div className="selected-files">
            <p>Archivos seleccionados:</p>
            {files.map((file, index) => (
              <p key={index}>{file.name}</p>
            ))}
          </div>
        )}
      </div>

      {/* Notas */}
      <div className="notes">
        <label htmlFor="notes">Notas del Reporte:</label>
        <textarea
          id="notes"
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
          placeholder="Opcional: Agrega notas adicionales..."
        />
      </div>

      {/* Botón de envío */}
      <button 
        onClick={handleSubmit}
        disabled={loading || (validation && !validation.canSubmit)}
        className="btn-submit"
      >
        {loading ? 'Enviando...' : 'Enviar Reporte'}
      </button>
    </div>
  );
};

export default ReportSubmission;
```

### Estilos CSS de Ejemplo

```css
.report-submission {
  max-width: 600px;
  margin: 0 auto;
  padding: 20px;
}

.validation {
  padding: 10px;
  border-radius: 4px;
  margin-bottom: 20px;
}

.validation.success {
  background-color: #d4edda;
  color: #155724;
  border: 1px solid #c3e6cb;
}

.validation.error {
  background-color: #f8d7da;
  color: #721c24;
  border: 1px solid #f5c6cb;
}

.uploaded-files {
  margin-bottom: 20px;
}

.file-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px;
  border: 1px solid #ddd;
  border-radius: 4px;
  margin-bottom: 10px;
}

.btn-remove {
  background-color: #dc3545;
  color: white;
  border: none;
  padding: 5px 10px;
  border-radius: 4px;
  cursor: pointer;
}

.btn-remove:hover {
  background-color: #c82333;
}

.file-upload {
  margin-bottom: 20px;
}

.selected-files {
  margin-top: 10px;
  padding: 10px;
  background-color: #f8f9fa;
  border-radius: 4px;
}

.notes {
  margin-bottom: 20px;
}

.notes textarea {
  width: 100%;
  height: 100px;
  padding: 10px;
  border: 1px solid #ddd;
  border-radius: 4px;
  resize: vertical;
}

.btn-submit {
  background-color: #007bff;
  color: white;
  border: none;
  padding: 12px 24px;
  border-radius: 4px;
  cursor: pointer;
  font-size: 16px;
}

.btn-submit:hover:not(:disabled) {
  background-color: #0056b3;
}

.btn-submit:disabled {
  background-color: #6c757d;
  cursor: not-allowed;
}
```

## Flujo de Trabajo Recomendado

### 1. **Carga Inicial**
```javascript
// Al cargar la página de envío de reporte
const loadInitialData = async (appointmentId) => {
  const [files, validation] = await Promise.all([
    getUploadedFiles(appointmentId),
    validateFiles(appointmentId)
  ]);
  
  setUploadedFiles(files);
  setValidation(validation);
};
```

### 2. **Gestión de Archivos**
```javascript
// Permitir al usuario agregar/quitar archivos
const handleFileManagement = async (action, data) => {
  switch (action) {
    case 'add':
      // Los archivos se suben al enviar el reporte
      setFiles(data.files);
      break;
    case 'remove':
      await deleteFile(appointmentId, data.deliverableId);
      await loadUploadedFiles();
      await validateFiles();
      break;
  }
};
```

### 3. **Envío Final**
```javascript
// Un solo botón que maneja todo
const handleFinalSubmit = async () => {
  // 1. Validar archivos
  const validation = await validateFiles(appointmentId);
  if (!validation.canSubmit) {
    showError(validation.message);
    return;
  }

  // 2. Enviar reporte con archivos
  const result = await submitReportWithFiles(appointmentId, files, notes);
  
  if (result.success) {
    showSuccess('Reporte enviado exitosamente');
    // Redirigir o actualizar estado
  } else {
    showError(result.message);
  }
};
```

## Mensajes de Error Mejorados

Los nuevos mensajes de error son específicos y claros:

- ❌ **Antes**: "Error al mandar reporte"
- ✅ **Ahora**: "Faltan archivos obligatorios: PDF y MP4. Debes subir estos archivos antes de enviar el reporte."

- ❌ **Antes**: "Error al mandar reporte"  
- ✅ **Ahora**: "Faltan archivos obligatorios: PDF. Debes subir estos archivos antes de enviar el reporte."

- ❌ **Antes**: "Error al mandar reporte"
- ✅ **Ahora**: "Faltan archivos obligatorios: MP4. Debes subir estos archivos antes de enviar el reporte."

## Ventajas de la Nueva Implementación

1. **🎯 UX Mejorada**: Un solo botón para todo el proceso
2. **📝 Mensajes Claros**: El usuario sabe exactamente qué archivos faltan
3. **🔄 Gestión Flexible**: Puede quitar y poner archivos antes de enviar
4. **⚡ Eficiencia**: Una sola operación para subir archivos y enviar reporte
5. **🛡️ Validación Robusta**: Verificación previa antes del envío
6. **📱 Responsive**: Funciona bien en móviles y desktop

## Endpoints Legacy (Mantenidos para Compatibilidad)

Los endpoints existentes siguen funcionando:
- `POST /api/appointment/submit-report/{appointmentId}` - Solo enviar reporte
- `POST /api/chat/deliverable/{searchHireId}` - Solo subir archivos

**Recomendación**: Usar los nuevos endpoints para mejor experiencia de usuario.
