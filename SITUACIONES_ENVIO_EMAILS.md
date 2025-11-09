# 📧 **SITUACIONES DONDE SE ENVÍAN EMAILS**

## ✅ **CONDICIONES PARA ENVÍO DE EMAIL**

Un email se envía cuando se cumplen **TODAS** estas condiciones:
1. ✅ Se llama a `LoggingService` con `notifyUser: true`
2. ✅ Se proporciona un `userId` válido
3. ✅ El usuario existe y tiene `Email` configurado
4. ✅ La configuración SMTP está completa (host, username, password)

---

## 📋 **LISTA COMPLETA DE SITUACIONES**

### 🏦 **1. STRIPE WEBHOOKS - Cuenta de Experto**

#### ✅ **Cuenta Aprobada** (`account.updated` - `charges_enabled: true`)
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando Stripe aprueba la cuenta del experto
- **Ubicación**: `Controllers/SubscriptionController.cs` líneas ~1316-1321, ~1339-1344
- **Mensaje Cliente**: "Cuenta de experto aprobada - El experto ya puede recibir pagos"
- **Mensaje Experto**: "Tu cuenta Stripe ha sido aprobada - Ya puedes recibir pagos"

#### ❌ **Cuenta Rechazada** (`account.updated` - `isRejected: true`)
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando Stripe rechaza la cuenta del experto
- **Ubicación**: `Controllers/SubscriptionController.cs` líneas ~1364-1368
- **Mensaje Cliente**: "Cuenta de experto rechazada - El experto no puede recibir pagos"
- **Mensaje Experto**: "Tu cuenta Stripe ha sido rechazada - Revisa tu configuración"

#### 🛒 **Contratación Confirmada** (`checkout.session.completed`)
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando se confirma el pago de una contratación
- **Ubicación**: `Controllers/SubscriptionController.cs` líneas ~1492-1497, ~1510-1515
- **Mensaje Cliente**: "Pago procesado - El experto ha sido notificado"
- **Mensaje Experto**: "Nueva contratación recibida - Monto: X€"

---

### 💼 **2. SERVICIOS (SearchHire)**

#### ✅ **Servicio Completado y Aprobado**
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando el cliente aprueba el servicio completado
- **Ubicación**: `Controllers/SearchHireController.cs` líneas ~497-514
- **Mensaje Cliente**: "Servicio completado - El experto recibirá el pago"
- **Mensaje Experto**: "Servicio aprobado - Has recibido el pago de X€"

#### ❌ **Servicio Rechazado (Disputa Creada)**
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando el cliente rechaza el servicio (se crea disputa)
- **Ubicación**: `Controllers/SearchHireController.cs` líneas ~523-540
- **Mensaje Cliente**: "Servicio rechazado - Se ha abierto una disputa"
- **Mensaje Experto**: "El cliente ha rechazado tu servicio - Se ha abierto una disputa"

#### ⏰ **Servicio Expirado (Experto no responde)**
- **Quién recibe**: Cliente
- **Cuándo**: Cuando el experto no responde en 2 días
- **Ubicación**: `Services/SubscriptionService.cs` líneas ~130-134
- **Mensaje**: "Servicio cancelado - experto no respondió. Reembolso de X€ procesado"

---

### 💰 **3. DINERO - Reembolsos y Transferencias**

#### 💸 **Reembolso Procesado**
- **Quién recibe**: Cliente
- **Cuándo**: Cuando se procesa un reembolso exitoso
- **Ubicación**: `Services/RefundService.cs` líneas ~642-646
- **Mensaje**: "Reembolso procesado - Se ha devuelto X€ a tu método de pago"

#### 💵 **Transferencia Procesada**
- **Quién recibe**: Experto
- **Cuándo**: Cuando se transfiere dinero al experto
- **Ubicación**: `Services/RefundService.cs` líneas ~656-660
- **Mensaje**: "Transferencia procesada - Has recibido X€ en tu cuenta"

---

### ⚖️ **4. DISPUTAS**

#### 📝 **Disputa Creada por Cliente**
- **Quién recibe**: Experto
- **Cuándo**: Cuando el cliente crea una disputa
- **Ubicación**: `Controllers/DisputeController.cs` líneas ~798-802
- **Mensaje**: "Disputa creada - Tienes 48 horas para responder"

#### 📝 **Disputa Creada por Experto**
- **Quién recibe**: Cliente
- **Cuándo**: Cuando el experto crea una disputa
- **Ubicación**: `Controllers/DisputeController.cs` líneas ~812-816
- **Mensaje**: "El experto ha creado una disputa"

#### ✅ **Disputa Resuelta a Favor del Cliente**
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando se resuelve la disputa a favor del cliente
- **Ubicación**: `Controllers/DisputeController.cs` líneas ~419-423, ~431-435
- **Mensaje Cliente**: "Disputa resuelta a tu favor - Reembolso procesado"
- **Mensaje Experto**: "Disputa resuelta a favor del cliente - Se procesará el reembolso"

#### ✅ **Disputa Resuelta a Favor del Experto**
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cuando se resuelve la disputa a favor del experto
- **Ubicación**: `Controllers/DisputeController.cs` líneas ~447-451, ~458-462
- **Mensaje Cliente**: "Disputa resuelta a favor del experto - El experto recibirá el pago"
- **Mensaje Experto**: "Disputa resuelta a tu favor - Recibirás el pago"

---

### 📅 **5. CITAS (Appointments)**

#### ❌ **Cita Cancelada por Cliente**
- **Quién recibe**: Experto
- **Cuándo**: Cuando el cliente cancela una cita
- **Ubicación**: `Services/AppointmentService.cs` líneas ~1051-1055
- **Mensaje**: "Cita cancelada por el cliente - Reembolso procesado (si aplica)"

#### ❌ **Cita Cancelada por Experto**
- **Quién recibe**: Cliente
- **Cuándo**: Cuando el experto cancela una cita
- **Ubicación**: `Services/AppointmentService.cs` líneas ~1068-1072
- **Mensaje**: "Cita cancelada por el experto - Reembolso procesado (si aplica)"

#### ❌ **Cita Rechazada (Primera vez)**
- **Quién recibe**: Cliente
- **Cuándo**: Primera vez que el experto rechaza una propuesta de cita
- **Ubicación**: `Services/AppointmentService.cs` líneas ~783-787
- **Mensaje**: "El experto rechazó tu propuesta - Puedes proponer otro horario"

#### ❌ **Cita Rechazada (Segunda vez)**
- **Quién recibe**: Cliente
- **Cuándo**: Segunda vez que el experto rechaza (cancelación automática)
- **Ubicación**: `Services/AppointmentService.cs` líneas ~796-800
- **Mensaje**: "El experto rechazó por segunda vez - Cita cancelada y reembolso procesado"

#### ⏰ **Timer Expirado - Cliente no responde (24h)**
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Cliente no responde a propuesta de cita en 24 horas
- **Ubicación**: `Services/AppointmentService.cs` líneas ~1222-1226, ~1235-1239
- **Mensaje Cliente**: "Cita cancelada - No respondiste en 24h. Reembolso procesado"
- **Mensaje Experto**: "Cita cancelada - Cliente no respondió en 24h"

#### ⏰ **Timer Expirado - Experto no envía reporte (24h)**
- **Quién recibe**: Cliente y Experto
- **Cuándo**: Experto no envía reporte después de la cita en 24 horas
- **Ubicación**: `Services/AppointmentService.cs` líneas ~1378-1382, ~1391-1395
- **Mensaje Cliente**: "Cita cancelada - Experto no envió reporte. Reembolso procesado"
- **Mensaje Experto**: "Cita cancelada - No enviaste el reporte en 24h"

---

## 📊 **RESUMEN POR TIPO DE EVENTO**

| Tipo de Evento | Cantidad de Situaciones | Quién Recibe Email |
|---------------|------------------------|-------------------|
| **Stripe Webhooks** | 3 | Cliente y/o Experto |
| **Servicios** | 3 | Cliente y/o Experto |
| **Dinero** | 2 | Cliente o Experto |
| **Disputas** | 4 | Cliente y/o Experto |
| **Citas** | 6 | Cliente y/o Experto |
| **TOTAL** | **18 situaciones** | |

---

## 🔔 **NOTAS IMPORTANTES**

1. **Email es opcional**: Si el usuario no tiene email configurado, solo se crea la notificación en BD
2. **Fallo silencioso**: Si el email falla, no interrumpe el proceso (solo se crea la notificación)
3. **Desarrollo**: Si no hay configuración SMTP, no se envían emails (modo desarrollo)
4. **HTML**: Todos los emails se envían en formato HTML con estilos

---

## 🎯 **PRÓXIMOS PASOS**

Si quieres agregar más situaciones de envío de emails, simplemente llama a `LoggingService` con `notifyUser: true` y el `userId` correspondiente.

