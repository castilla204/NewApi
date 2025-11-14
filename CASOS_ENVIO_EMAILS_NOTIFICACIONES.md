# 📧 **CASOS COMPLETOS DE ENVÍO DE EMAILS Y NOTIFICACIONES**

## ✅ **CONDICIONES PARA ENVÍO**

Un email y notificación se envían cuando se cumplen **TODAS** estas condiciones:
1. ✅ Se llama a `LoggingService` con `notifyUser: true`
2. ✅ Se proporciona un `userId` válido
3. ✅ El usuario existe y tiene `Email` configurado
4. ✅ La configuración SMTP está completa (host, username, password)

---

## 📋 **LISTA COMPLETA DE CASOS**

### 🏦 **1. STRIPE WEBHOOKS - Cuenta de Experto**

#### ✅ **Cuenta Aprobada** (`account.updated` - `charges_enabled: true`)
- **Archivo**: `Controllers/SubscriptionController.cs` línea ~1314-1322
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`expertProfile.UserId`)
- **Cuándo**: Cuando Stripe aprueba la cuenta del experto (transición a `StripeStatus.Approved`)
- **Mensaje**: "Cuenta de Stripe aprobada"
- **Detalles**: Mensaje con detalles de la aprobación

#### ❌ **Cuenta Rechazada** (`account.updated` - `isRejected: true`)
- **Archivo**: `Controllers/SubscriptionController.cs` línea ~1337-1345
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`expertProfile.UserId`)
- **Cuándo**: Cuando Stripe rechaza la cuenta del experto (transición a `StripeStatus.Rejected`)
- **Mensaje**: "Cuenta de Stripe rechazada"
- **Detalles**: Mensaje con razón del rechazo y detalles del error
- **Nivel**: ERROR (rojo)

#### ⏳ **Cuenta Pendiente** (`account.updated` - `StripeStatus.Pending`)
- **Archivo**: `Controllers/SubscriptionController.cs` línea ~1362-1370
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`expertProfile.UserId`)
- **Cuándo**: Cuando la cuenta pasa a estado pendiente con requirements pendientes
- **Mensaje**: "Cuenta de Stripe pendiente de verificación"
- **Detalles**: Lista de requirements pendientes
- **Nivel**: WARNING (amarillo)

#### 🛒 **Contratación Confirmada** (`checkout.session.completed`)
- **Archivo**: `Controllers/SubscriptionController.cs` línea ~1492-1497, ~1510-1515, ~1534-1538
- **Quién recibe EMAIL y NOTIFICACIÓN**: 
  - **Cliente** (`userId` del webhook)
  - **Experto** (`profileByUserId.UserId`)
- **Cuándo**: Cuando se confirma el pago de una contratación
- **Mensaje Cliente**: "Pago procesado - El experto ha sido notificado"
- **Mensaje Experto**: "Nueva contratación recibida - Monto: X€"

---

### 💼 **2. SERVICIOS (SearchHire)**

#### ✅ **Servicio Creado** (`CreateSearchHire`)
- **Archivo**: `Controllers/SearchHireController.cs` línea ~177-185
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`searchHire.ClientId`)
- **Cuándo**: Cuando se crea una nueva contratación
- **Mensaje**: "Contratación creada"
- **Detalles**: "Tu contratación #{searchHire.Id} ha sido creada exitosamente. El experto ha sido notificado."
- **FACTURA**: Se envía factura por email al cliente (PDF adjunto) vía Hangfire

#### ✅ **Servicio Creado - Notificación al Experto**
- **Archivo**: `Controllers/SearchHireController.cs` línea ~199-207
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`searchHire.ExpertId.Value`)
- **Cuándo**: Cuando se crea una nueva contratación
- **Mensaje**: "Nueva contratación recibida"
- **Detalles**: "Has recibido una nueva contratación #{searchHire.Id} por {price}€. Revisa los detalles y contacta con el cliente."

#### ✅ **Servicio Completado y Aprobado** (`HandlePendingHireCompleted`)
- **Archivo**: `Controllers/SubscriptionController.cs` línea ~2116-2124
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`userId` del webhook)
- **Cuándo**: Cuando el pago se confirma y la contratación se activa
- **Mensaje**: "Contratación confirmada"
- **Detalles**: "Tu pago se procesó correctamente. La contratación #{searchHire.Id} está activa y el experto ha sido notificado."
- **FACTURA**: Se envía factura por email al cliente (PDF adjunto) vía Hangfire

#### ✅ **Servicio Completado - Notificación al Experto**
- **Archivo**: `Controllers/SubscriptionController.cs` línea ~2137-2145
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`expertuserid`)
- **Cuándo**: Cuando el pago se confirma y la contratación se activa
- **Mensaje**: "Nueva contratación recibida"
- **Detalles**: "Has recibido una nueva contratación #{searchHire.Id} por {price}€. Revisa los detalles y contacta con el cliente."

---

### 📅 **3. CITAS (Appointments)**

#### 📝 **Propuesta de Cita Creada** (`ProposeAppointmentAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~866-874
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`updatedAppointment.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando el cliente propone una fecha/hora para la cita
- **Mensaje**: "Nueva propuesta de cita recibida"
- **Detalles**: "El cliente ha propuesto una cita para el {fecha} en {ubicación}. Tienes 24 horas para aceptar o rechazar."

#### ❌ **Cita Rechazada - Primera Vez** (`RejectAppointmentAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~1836-1852
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el experto rechaza la propuesta de cita por primera vez
- **Mensaje**: "Cita rechazada"
- **Detalles**: "El experto rechazó la propuesta de cita. Puedes proponer otra fecha y hora."

#### ❌ **Cita Rechazada - Segunda Vez (Finalización)** (`RejectAppointmentAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~1808-1826
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el experto rechaza la propuesta de cita por segunda vez (finaliza el servicio)
- **Mensaje**: "Cita cancelada definitivamente"
- **Detalles**: "El experto rechazó la propuesta de cita por segunda vez. El servicio ha sido cancelado y se procesará tu reembolso."
- **NOTA**: Se procesa reembolso automático

#### 🚫 **Cita Cancelada por Cliente** (`CancelAppointmentAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~2458-2474
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`appointment.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando el cliente cancela la cita
- **Mensaje**: "Cita cancelada por el cliente"
- **Detalles**: "El cliente canceló la cita #{appointment.Id}. Puedes proponer otra fecha y hora."

#### 🚫 **Cita Cancelada por Experto** (`CancelAppointmentAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~2492-2508
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el experto cancela la cita
- **Mensaje**: "Cita cancelada por el experto"
- **Detalles**: "El experto canceló la cita #{appointment.Id}. Se procesará tu reembolso."
- **Nivel**: WARNING (amarillo)

#### ✅ **Cita Confirmada por Experto** (`ConfirmAppointmentAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~1200-1215
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`updatedAppointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el experto confirma la propuesta de cita
- **Mensaje**: "Cita confirmada por el experto"
- **Detalles**: "El experto confirmó la cita para el {fecha} a las {hora} en {ubicación}."

#### 📄 **Reporte de Experto Enviado** (`SubmitExpertReportAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~4370-4385
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el experto envía el reporte de la cita
- **Mensaje**: "Reporte del experto recibido"
- **Detalles**: "El experto envió el reporte del servicio #{searchHireId}. Tienes 24 horas para aprobar o disputar el servicio."

---

### ⏰ **4. TIMERS DE CITAS (Appointment Timers)**

#### ⏰ **Timer "response" Expirado - Cliente No Respondió** (`CheckAppointmentTimersAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~2800-2818
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`timer.Appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el cliente no responde a la propuesta de cita en 24 horas
- **Mensaje**: "Cita cancelada - no respondiste"
- **Detalles**: "No respondiste a la propuesta de cita en 24 horas. La cita fue cancelada automáticamente. Se procesará tu reembolso."

#### ⏰ **Timer "response" Expirado - Notificación al Experto**
- **Archivo**: `Services/AppointmentService.cs` línea ~2828-2844
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`timer.Appointment.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando el cliente no responde a la propuesta de cita en 24 horas
- **Mensaje**: "Cita cancelada - cliente no respondió"
- **Detalles**: "El cliente no respondió a tu propuesta de cita en 24 horas. La cita fue cancelada automáticamente."

#### ⏰ **Timer "expert_report" Expirado - Experto No Envió Reporte** (`CheckAppointmentTimersAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~3112-3132
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`timer.Appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el experto no envía el reporte en 24 horas después de la cita
- **Mensaje**: "Cita cancelada - experto no envió reporte"
- **Detalles**: "El experto no envió el reporte a tiempo. La cita fue cancelada automáticamente. Se procesará tu reembolso."

#### ⏰ **Timer "expert_report" Expirado - Notificación al Experto**
- **Archivo**: `Services/AppointmentService.cs` línea ~3142-3158
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`timer.Appointment.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando el experto no envía el reporte en 24 horas después de la cita
- **Mensaje**: "Cita cancelada - no enviaste el reporte"
- **Detalles**: "No enviaste el reporte de la cita #{appointment.Id} en 24 horas. La cita fue cancelada automáticamente."
- **Nivel**: WARNING (amarillo)

#### ⏰ **Timer "awaiting_report_transition" - Transición a Awaiting Report** (`ProcessAppointmentToAwaitingReportAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~4070-4085
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`searchHire.ExpertId.Value`)
- **Cuándo**: Cuando pasan 3 horas desde la hora de la cita confirmada
- **Mensaje**: "Debes enviar el reporte de la cita"
- **Detalles**: "Han pasado 3 horas desde la cita. Tienes 24 horas para enviar el reporte del servicio #{searchHire.Id}. Si no lo envías a tiempo, la cita será cancelada automáticamente."

#### ⏰ **Timer "client_decision" Expirado - Cliente No Decidió** (`ProcessAppointmentTimerAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~3831-3849
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`timer.Appointment.SearchHire.ClientId`)
- **Cuándo**: Cuando el cliente no aprueba/disputa en 24 horas después de recibir el reporte
- **Mensaje**: "Appointment timer expired - client no response, auto-completed"
- **Detalles**: "Appointment {appointment.Id} completed automatically in favor of expert due to client not responding within 24h"
- **NOTA**: Solo log INFO, no notifica al usuario (se añade notificación al experto)

#### ⏰ **Timer "client_decision" Expirado - Notificación al Experto** (`ProcessAppointmentTimerAsync`)
- **Archivo**: `Services/AppointmentService.cs` línea ~3850-3865
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`timer.Appointment.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando el cliente no aprueba/disputa en 24 horas después de recibir el reporte
- **Mensaje**: "Servicio completado automáticamente a tu favor"
- **Detalles**: "El cliente no respondió en 24 horas. El servicio #{searchHireId} se completó automáticamente a tu favor y se procesó tu pago."

---

### 💰 **5. DISTRIBUCIÓN DE DINERO (ProcessMoneyDistributionAsync)**

#### ✅ **Reembolso Procesado Exitosamente**
- **Archivo**: `Services/RefundService.cs` línea ~930-938
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`searchHire.ClientId`)
- **Cuándo**: Cuando se procesa exitosamente un reembolso al cliente
- **Mensaje**: "Reembolso procesado"
- **Detalles**: "Se procesó tu reembolso de {amount}€ por el servicio #{searchHireId}. El dinero llegará a tu cuenta en 5-10 días hábiles."

#### ✅ **Pago a Experto Procesado Exitosamente**
- **Archivo**: `Services/RefundService.cs` línea ~944-952
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`searchHire.ExpertId.Value`)
- **Cuándo**: Cuando se procesa exitosamente una transferencia al experto
- **Mensaje**: "Pago recibido"
- **Detalles**: "Has recibido {amount}€ por el servicio #{searchHireId}. El dinero está disponible en tu cuenta de Stripe."

#### 🚨 **Error de Stripe - Estado Ya Actualizado** (CRITICAL)
- **Archivo**: `Services/RefundService.cs` línea ~968-980
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`searchHire.ExpertId.Value`)
- **Cuándo**: Cuando el estado se actualiza correctamente pero falla el procesamiento de dinero en Stripe
- **Mensaje**: "CRITICAL: Stripe error - state already updated"
- **Detalles**: "El estado del servicio #{searchHireId} se actualizó correctamente, pero hubo un error al procesar el pago. Error de Stripe: {error}. Se requiere procesamiento manual del pago."
- **Nivel**: CRITICAL (rojo) - Requiere intervención manual

---

### ⚖️ **6. DISPUTAS (Disputes)**

#### ⚠️ **Disputa Creada por Cliente**
- **Archivo**: `Controllers/DisputeController.cs` línea ~1018-1026
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`searchHire.ExpertId.Value`)
- **Cuándo**: Cuando el cliente abre una disputa sobre el servicio
- **Mensaje**: "Disputa abierta por el cliente"
- **Detalles**: "El cliente ha abierto una disputa sobre el servicio #{searchHire.Id}. Tienes 48 horas para responder. Razón: {reason}."
- **Nivel**: WARNING (amarillo)

#### ⚠️ **Disputa Creada por Experto**
- **Archivo**: `Controllers/DisputeController.cs` línea ~1032-1040
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`searchHire.ClientId`)
- **Cuándo**: Cuando el experto abre una disputa sobre el servicio
- **Mensaje**: "Disputa abierta por el experto"
- **Detalles**: "El experto ha abierto una disputa sobre el servicio #{searchHire.Id}. Un administrador la revisará. Razón: {reason}."
- **Nivel**: WARNING (amarillo)

#### ✅ **Disputa Resuelta a Favor del Cliente - Cliente**
- **Archivo**: `Controllers/DisputeController.cs` línea ~598-606
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`dispute.SearchHire.ClientId`)
- **Cuándo**: Cuando un administrador resuelve la disputa a favor del cliente
- **Mensaje**: "Disputa resuelta a tu favor"
- **Detalles**: "La disputa del servicio #{searchHire.Id} se resolvió a tu favor. Se procesará tu reembolso de {amount}€."

#### ⚠️ **Disputa Resuelta a Favor del Cliente - Experto**
- **Archivo**: `Controllers/DisputeController.cs` línea ~610-618
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`dispute.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando un administrador resuelve la disputa a favor del cliente
- **Mensaje**: "Disputa resuelta a favor del cliente"
- **Detalles**: "La disputa del servicio #{searchHire.Id} se resolvió a favor del cliente. El reembolso se procesará."
- **Nivel**: WARNING (amarillo)

#### ✅ **Disputa Resuelta a Favor del Experto - Experto**
- **Archivo**: `Controllers/DisputeController.cs` línea ~626-634
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Experto** (`dispute.SearchHire.ExpertId.Value`)
- **Cuándo**: Cuando un administrador resuelve la disputa a favor del experto
- **Mensaje**: "Disputa resuelta a tu favor"
- **Detalles**: "La disputa del servicio #{searchHire.Id} se resolvió a tu favor. Has recibido {amount}€."

#### ⚠️ **Disputa Resuelta a Favor del Experto - Cliente**
- **Archivo**: `Controllers/DisputeController.cs` línea ~637-645
- **Quién recibe EMAIL y NOTIFICACIÓN**: **Cliente** (`dispute.SearchHire.ClientId`)
- **Cuándo**: Cuando un administrador resuelve la disputa a favor del experto
- **Mensaje**: "Disputa resuelta a favor del experto"
- **Detalles**: "La disputa del servicio #{searchHire.Id} se resolvió a favor del experto."
- **Nivel**: WARNING (amarillo)

---

### 📄 **7. FACTURAS (Invoices)**

#### 📧 **Factura Enviada por Email** (`SendInvoiceByEmailBackgroundJob`)
- **Archivo**: `Services/InvoiceService.cs` línea ~409
- **Quién recibe EMAIL**: **Cliente** (`toEmail` - email del cliente)
- **Cuándo**: 
  - Cuando se crea un nuevo servicio (`CreateSearchHire`)
  - Cuando se confirma el pago (`HandlePendingHireCompleted`)
- **Contenido**: Email HTML con PDF adjunto de la factura
- **Método**: Hangfire background job (no bloquea la API)
- **Reintentos**: 3 intentos con delays de 1m, 5m, 10m

---

## 📊 **RESUMEN POR TIPO DE USUARIO**

### 👤 **CLIENTE** recibe emails/notificaciones en:
1. ✅ Contratación creada
2. ✅ Contratación confirmada (pago procesado)
3. ✅ Cita confirmada por el experto
4. ❌ Cita rechazada (primera y segunda vez)
5. 🚫 Cita cancelada por experto
6. 📄 Reporte del experto recibido
7. ⏰ Timer expirado - no respondió a propuesta
8. ⏰ Timer expirado - experto no envió reporte
9. ⏰ Timer expirado - no decidió sobre el reporte (auto-completado)
10. 💰 Reembolso procesado
11. ⚠️ Disputa creada por experto
12. ✅ Disputa resuelta a favor del cliente
13. ⚠️ Disputa resuelta a favor del experto
14. 📄 Factura (PDF adjunto)

### 👨‍💼 **EXPERTO** recibe emails/notificaciones en:
1. ✅ Nueva contratación recibida
2. 📝 Nueva propuesta de cita recibida
3. 🚫 Cita cancelada por cliente
4. ⏰ Transición a awaiting_report (3 horas después de la cita)
5. ⏰ Timer expirado - cliente no respondió
6. ⏰ Timer expirado - no envió reporte
7. ⏰ Timer expirado - cliente no decidió (servicio completado automáticamente)
8. 💰 Pago recibido
9. 🚨 Error de Stripe (CRITICAL)
10. ✅ Cuenta Stripe aprobada
11. ❌ Cuenta Stripe rechazada
12. ⏳ Cuenta Stripe pendiente
13. ⚠️ Disputa creada por cliente
14. ⚠️ Disputa resuelta a favor del cliente
15. ✅ Disputa resuelta a favor del experto

---

## 🔧 **MECANISMO DE ENVÍO**

### **Notificaciones en Base de Datos**
- Se crean registros en la tabla `Notifications`
- Se asocian al `userId` correspondiente
- Tienen título, mensaje, tipo y estado de lectura

### **Emails**
- Se envían vía `EmailService` usando SMTP (Hostinger)
- Se procesan en segundo plano con Hangfire (no bloquean la API)
- Se envían solo si el usuario tiene `Email` configurado
- Tienen formato HTML con estilos CSS
- Los emails de factura incluyen PDF adjunto

### **Condiciones para Envío**
1. `notifyUser: true` en la llamada a `LoggingService`
2. `userId` válido y existente
3. Usuario tiene `Email` configurado
4. Configuración SMTP completa

---

## 📝 **NOTAS IMPORTANTES**

- **Todos los emails se envían en segundo plano** usando Hangfire para no bloquear la API
- **Las notificaciones se crean siempre** si hay `userId` válido, independientemente del email
- **Los emails de factura** se envían automáticamente cuando se crea o confirma un servicio
- **Los errores críticos de Stripe** notifican al experto porque requiere intervención manual
- **Los timers** notifican a ambas partes cuando expiran para mantener transparencia

