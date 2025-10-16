# 🗑️ Guía de Borrado de Cuentas

## 📋 **Resumen**

Sistema completo para el borrado de cuentas de usuarios que maneja automáticamente las contrataciones activas, crea disputas cuando es necesario y notifica a todas las partes afectadas.

## 🎯 **Características Principales**

### ✅ **Funcionalidades Implementadas:**
- **Verificación de estado** antes del borrado
- **Detección automática** de contrataciones activas
- **Creación de disputas** automáticas cuando hay contrataciones
- **Eliminación segura** de todos los datos del usuario
- **Notificaciones** a usuarios afectados
- **Soporte para administradores** para borrar cuentas de cualquier usuario

### 🔒 **Estados de Contratación que Requieren Atención:**
- `pending` - Pendiente
- `awaiting_client_decision` - Esperando decisión del cliente  
- `disputed` - En disputa

## 🚀 **Endpoints Disponibles**

### **1. Verificar Estado de Borrado**
```http
GET /api/AccountDeletion/status
Authorization: Bearer {token}
```

**Respuesta:**
```json
{
  "canDeleteImmediately": false,
  "hasActiveContracts": true,
  "activeContractsCount": 2,
  "activeContracts": [
    {
      "searchHireId": 123,
      "status": "pending",
      "serviceName": "Reparación de Móvil",
      "amount": 50.00,
      "createdAt": "2024-01-15T10:30:00Z",
      "otherPartyName": "Juan Pérez",
      "otherPartyEmail": "juan@email.com",
      "hasAppointment": true,
      "appointmentDate": "2024-01-20T14:00:00Z"
    }
  ],
  "message": "Se encontraron 2 contrataciones activas que requieren atención"
}
```

### **2. Eliminar Cuenta**
```http
POST /api/AccountDeletion/delete
Authorization: Bearer {token}
Content-Type: application/json

{
  "reason": "Ya no necesito el servicio"  // Opcional
}
```

**O sin razón:**
```http
POST /api/AccountDeletion/delete
Authorization: Bearer {token}
Content-Type: application/json

{
}
```

**Respuesta:**
```json
{
  "success": true,
  "message": "Cuenta eliminada. Se crearon 2 disputas para contrataciones activas.",
  "activeContracts": [...],
  "disputesCreated": [
    {
      "disputeId": 456,
      "searchHireId": 123,
      "reason": "Cliente eliminó su cuenta. Razón: Ya no necesito el servicio",
      "affectedPartyName": "Juan Pérez",
      "affectedPartyEmail": "juan@email.com"
    }
  ],
  "requiresManualReview": true
}
```

### **3. Endpoints de Administrador**

#### **Verificar Estado de Cualquier Usuario:**
```http
GET /api/AccountDeletion/admin/status/{userId}
Authorization: Bearer {adminToken}
```

#### **Eliminar Cuenta de Cualquier Usuario:**
```http
POST /api/AccountDeletion/admin/delete/{userId}
Authorization: Bearer {adminToken}
Content-Type: application/json

{
  "reason": "Violación de términos de servicio"
}
```

## 🔄 **Flujo de Proceso**

### **1. Verificación Inicial**
- Usuario solicita verificar estado de borrado
- Sistema busca contrataciones activas
- Retorna información sobre contrataciones encontradas

### **2. Proceso de Borrado**
1. **Validación de identidad** (contraseña)
2. **Detección de contrataciones activas**
3. **Creación de disputas automáticas** (si aplica)
4. **Eliminación de datos del usuario**
5. **Envío de notificaciones**
6. **Confirmación de transacción**

### **3. Manejo de Contrataciones Activas**

#### **Si el CLIENTE elimina su cuenta:**
- Se crea disputa automática a favor del **EXPERTO**
- Razón: "Cliente eliminó su cuenta. Razón: {razón_proporcionada}"
- El experto tiene 48 horas para responder

#### **Si el EXPERTO elimina su cuenta:**
- Se crea disputa automática a favor del **CLIENTE**
- Razón: "Experto eliminó su cuenta. Razón: {razón_proporcionada}"
- El cliente tiene 48 horas para responder

## 📧 **Sistema de Notificaciones**

### **Notificaciones Enviadas:**

1. **Al usuario que elimina su cuenta:**
   - Título: "Cuenta Eliminada"
   - Mensaje: "Tu cuenta ha sido eliminada exitosamente. Razón: {razón}"

2. **A usuarios afectados por disputas:**
   - Título: "Contratación en Disputa - Cuenta Eliminada"
   - Mensaje: "El {cliente/experto} del servicio '{nombre}' ha eliminado su cuenta. Se ha creado una disputa automática para proteger tus intereses. Tienes 48 horas para responder."

## 🗃️ **Datos Eliminados**

### **Datos del Usuario Eliminados:**
- ✅ Mensajes enviados
- ✅ Conversaciones (como cliente y experto)
- ✅ Likes dados
- ✅ Reseñas dadas
- ✅ Búsquedas creadas
- ✅ Servicios ofrecidos (si es experto)
- ✅ Imágenes de servicios
- ✅ Perfil de experto
- ✅ Configuraciones de usuario
- ✅ Suscripciones
- ✅ Transacciones financieras
- ✅ Notificaciones
- ✅ Usuario principal

### **Datos Preservados:**
- 🔒 Contrataciones (SearchHire) - Se mantienen para disputas
- 🔒 Disputas creadas - Para resolución manual
- 🔒 Reseñas recibidas - Para historial de otros usuarios
- 🔒 Citas (Appointment) - Vinculadas a contrataciones

## 🛡️ **Seguridad y Validaciones**

### **Validaciones Implementadas:**
- ✅ Verificación de identidad del usuario (JWT token)
- ✅ Transacciones de base de datos para integridad
- ✅ Logging completo de todas las operaciones
- ✅ Autorización por roles (Admin vs Usuario)

### **Manejo de Errores:**
- ✅ Rollback automático en caso de error
- ✅ Logging detallado de errores
- ✅ Respuestas informativas al usuario
- ✅ Validación de datos de entrada

## 📱 **Ejemplo de Uso Frontend**

```javascript
// 1. Verificar estado antes de mostrar opción de borrado
async function checkDeletionStatus() {
    const response = await fetch('/api/AccountDeletion/status', {
        headers: {
            'Authorization': `Bearer ${token}`
        }
    });
    
    const status = await response.json();
    
    if (status.hasActiveContracts) {
        showWarning(`Tienes ${status.activeContractsCount} contrataciones activas. 
                    Al eliminar tu cuenta, se crearán disputas automáticas.`);
    }
    
    return status;
}

// 2. Eliminar cuenta (con o sin razón)
async function deleteAccount(reason = null) {
    const requestBody = {};
    if (reason) {
        requestBody.reason = reason;
    }
    
    const response = await fetch('/api/AccountDeletion/delete', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify(requestBody)
    });
    
    const result = await response.json();
    
    if (result.success) {
        if (result.requiresManualReview) {
            showInfo(`Cuenta eliminada. Se crearon ${result.disputesCreated.length} disputas. 
                     Los usuarios afectados han sido notificados.`);
        } else {
            showSuccess('Cuenta eliminada exitosamente');
        }
        
        // Redirigir al login
        window.location.href = '/login';
    } else {
        showError(result.message);
    }
}
```

## 🔧 **Configuración Requerida**

### **Servicios Registrados:**
```csharp
// En Program.cs
builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();
builder.Services.AddScoped<IAccountDeletionNotificationService, AccountDeletionNotificationService>();
```

### **Dependencias:**
- ✅ `AppDbContext` - Acceso a base de datos
- ✅ `IRabbitMQService` - Notificaciones
- ✅ `ILogger` - Logging
- ✅ `IConfiguration` - Configuración

## 📊 **Monitoreo y Logs**

### **Logs Importantes:**
- `User {UserId} requesting account deletion with reason: {Reason}`
- `Created automatic dispute {DisputeId} for SearchHire {SearchHireId} due to account deletion`
- `Successfully deleted account for user {UserId}`
- `Sent dispute notification to affected user {UserId} for SearchHire {SearchHireId}`

### **Métricas a Monitorear:**
- Número de cuentas eliminadas por día
- Número de disputas creadas automáticamente
- Tiempo de respuesta del proceso de borrado
- Errores en el proceso de borrado

## ⚠️ **Consideraciones Importantes**

1. **Disputas Automáticas:** Las disputas creadas automáticamente requieren revisión manual por parte de administradores.

2. **Tiempo de Respuesta:** Los usuarios afectados tienen 48 horas para responder a las disputas.

3. **Integridad de Datos:** El proceso usa transacciones para garantizar la integridad de los datos.

4. **Notificaciones:** Las notificaciones se envían tanto por base de datos como por RabbitMQ para notificaciones push/email.

5. **Logging:** Todos los procesos están completamente loggeados para auditoría y debugging.
