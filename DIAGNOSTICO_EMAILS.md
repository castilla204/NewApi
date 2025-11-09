# 🔍 **DIAGNÓSTICO: Emails y Notificaciones No Enviadas**

## ❌ **PROBLEMA REPORTADO**
- ✅ Notificación al cliente: **SÍ llega**
- ❌ Email al cliente: **NO llega**
- ❌ Notificación al experto: **NO llega**
- ❌ Email al experto: **NO llega**

---

## 🔍 **PASOS DE DIAGNÓSTICO**

### **1. Verificar Configuración SMTP**

Verifica que los secretos de email estén configurados en Google Cloud Secret Manager:

```bash
# En el servidor, verifica las variables de entorno o configuración
# O revisa los logs de la aplicación para ver si hay errores de SMTP
```

**Secretos requeridos:**
- `email-smtp-host` → `smtp.hostinger.com`
- `email-smtp-port` → `465`
- `email-smtp-username` → `info@inspecciono.com`
- `email-smtp-password` → `Pedrohabo1///`
- `email-from-email` → `info@inspecciono.com`
- `email-from-name` → `Inspecciono`

**Si falta alguno**: El `EmailService` retorna silenciosamente sin enviar (línea 39-42).

---

### **2. Verificar que el Cliente Tiene Email**

Consulta en la base de datos:

```sql
SELECT "Id", "Email", "PhoneVerified" 
FROM "Users" 
WHERE "Id" = [TU_USER_ID];
```

**Si `Email` es NULL o vacío**: No se enviará email, solo notificación en BD.

---

### **3. Verificar que el Experto Existe y Tiene Email**

El código obtiene el `expertuserid` así (línea 2054):
```csharp
var expertuserid = expertProfile?.UserId ?? 0;
```

Si `expertuserid` es 0, la notificación al experto NO se envía (línea 2122).

**Consulta para verificar:**
```sql
-- Verificar el experto del servicio
SELECT 
    ss."Id" as ServiceId,
    ss."ExpertProfileId",
    ep."UserId" as ExpertUserId,
    u."Email" as ExpertEmail,
    u."PhoneVerified" as ExpertPhoneVerified
FROM "SearchServices" ss
JOIN "ExpertProfiles" ep ON ss."ExpertProfileId" = ep."Id"
LEFT JOIN "Users" u ON ep."UserId" = u."Id"
WHERE ss."Id" = [SERVICE_ID_DE_LA_CONTRATACION];
```

**Problemas posibles:**
- `ExpertProfileId` es NULL → `expertProfile` es NULL → `expertuserid = 0`
- `ExpertUserId` es NULL → `expertuserid = 0`
- `ExpertEmail` es NULL → No se envía email al experto

---

### **4. Verificar Logs de la Aplicación**

Los errores de email se capturan silenciosamente. Para verlos, revisa los logs:

```bash
# En Kubernetes
kubectl logs -n default -l app=new-api --tail=100 | grep -i email
kubectl logs -n default -l app=new-api --tail=100 | grep -i smtp
kubectl logs -n default -l app=new-api --tail=100 | grep -i "HandlePendingHireCompleted"
```

---

### **5. Verificar Notificaciones en BD**

Consulta las notificaciones creadas:

```sql
-- Notificaciones del cliente
SELECT * FROM "Notifications" 
WHERE "UserId" = [CLIENT_USER_ID]
ORDER BY "CreatedAt" DESC 
LIMIT 10;

-- Notificaciones del experto
SELECT * FROM "Notifications" 
WHERE "UserId" = [EXPERT_USER_ID]
ORDER BY "CreatedAt" DESC 
LIMIT 10;
```

**Si no hay notificación del experto**: El problema está en la obtención del `expertuserid` (línea 2054 o 2122).

---

## 🛠️ **SOLUCIONES**

### **Solución 1: Agregar Logging de Errores de Email**

Modificar `EmailService.cs` para loguear errores:

```csharp
catch (Exception ex)
{
    // Agregar logging aquí para ver errores
    // await _loggingService.LogErrorAsync(...)
}
```

### **Solución 2: Verificar expertuserid**

Agregar logging antes de la notificación al experto:

```csharp
// En HandlePendingHireCompleted, línea ~2121
if (expertuserid > 0)
{
    // Agregar log aquí para verificar
    await _loggingService.LogInfoAsync(
        message: "DEBUG: Notificando experto",
        details: $"ExpertUserId: {expertuserid}, ServiceId: {serviceId}",
        userId: expertuserid,
        source: "SubscriptionController.HandlePendingHireCompleted",
        relatedEntityType: "Debug",
        relatedEntityId: serviceId
    );
    
    await _loggingService.LogInfoAsync(
        message: "Nueva contratación recibida",
        details: $"Has recibido una nueva contratación #{searchHire.Id} por {service.Price}€. Revisa los detalles y contacta con el cliente.",
        userId: expertuserid,
        source: "SubscriptionController.HandlePendingHireCompleted",
        relatedEntityType: "SearchHire",
        relatedEntityId: searchHire.Id,
        notifyUser: true
    );
}
else
{
    // Agregar log de error si expertuserid es 0
    await _loggingService.LogErrorAsync(
        message: "ERROR: expertuserid es 0 - No se puede notificar al experto",
        details: $"ServiceId: {serviceId}, ExpertProfileId: {service.ExpertProfileId}",
        userId: userId,
        source: "SubscriptionController.HandlePendingHireCompleted",
        relatedEntityType: "SearchService",
        relatedEntityId: serviceId
    );
}
```

---

## 📋 **CHECKLIST DE VERIFICACIÓN**

- [ ] Secretos de email configurados en Google Cloud Secret Manager
- [ ] Cliente tiene `Email` configurado en BD
- [ ] Experto existe y tiene `UserId` válido
- [ ] Experto tiene `Email` configurado en BD
- [ ] `expertuserid > 0` cuando se ejecuta la notificación
- [ ] No hay errores en los logs de la aplicación
- [ ] Notificaciones se crean en BD (verificar con SQL)

---

## 🚨 **PROBLEMA MÁS PROBABLE**

Basándome en el código, el problema más probable es:

1. **`expertuserid = 0`**: El servicio no tiene un `ExpertProfileId` válido o el perfil no tiene `UserId`
2. **Configuración SMTP incompleta**: Los secretos de email no están configurados en producción
3. **Experto sin email**: El experto no tiene `Email` configurado en BD

**Recomendación**: Ejecuta las consultas SQL arriba para identificar el problema exacto.

