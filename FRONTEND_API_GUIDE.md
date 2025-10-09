# 🎯 Guía del Frontend - Sistema de Estados Centralizados

## 📋 **Resumen de Cambios**

El sistema ha migrado de un sistema de estados hardcodeados a un **sistema centralizado de estados** gestionado desde la base de datos. Esto permite:

- ✅ **Gestión administrativa** de todos los estados
- ✅ **Configuración flexible** de distribución de dinero
- ✅ **Mapeo automático** entre estados de diferentes entidades
- ✅ **Escalabilidad** para futuros tipos de estados

---

## 🔗 **Endpoints Disponibles**

### **1. Configuraciones de Estados de Cita**
```http
GET /api/AppointmentConfig/appointment-status-configs
```

**Respuesta:**
```json
[
  {
    "id": 1,
    "estado": "Cita Rechazada",
    "statusId": 1,
    "statusValue": "appointment-rejected",
    "statusName": "AppointmentRejected",
    "cliente": 100,
    "experto": 0,
    "plataforma": 0,
    "prioridad": "Por Status",
    "activo": "Activo",
    "categoryId": null,
    "categoryName": "Todas las categorías",
    "serviceTypeCategoryId": null,
    "serviceTypeCategoryName": "Todos los tipos",
    "createdAt": "2025-09-28T10:00:00Z",
    "updatedAt": "2025-09-28T10:00:00Z"
  }
]
```

### **2. Estados de Cita Disponibles**
```http
GET /api/AppointmentConfig/appointment-status
```

**Respuesta:**
```json
[
  {
    "id": 1,
    "statusName": "AppointmentRejected",
    "statusValue": "appointment-rejected",
    "displayName": "Cita Rechazada",
    "description": "El experto rechazó la cita propuesta",
    "sortOrder": 1,
    "createdAt": "2025-09-28T10:00:00Z",
    "updatedAt": "2025-09-28T10:00:00Z"
  }
]
```

### **3. Tipos de Servicio**
```http
GET /api/AppointmentConfig/service-type-category
```

**Respuesta:**
```json
[
  {
    "id": 1,
    "name": "Consultoría",
    "description": "Servicios de consultoría profesional",
    "isActive": true,
    "createdAt": "2025-09-28T10:00:00Z",
    "updatedAt": "2025-09-28T10:00:00Z"
  }
]
```

### **4. Crear Nueva Configuración**
```http
POST /api/AppointmentConfig/appointment-status-configs
Content-Type: application/json

{
  "statusId": 1,
  "categoryId": null,
  "serviceTypeCategoryId": null,
  "clientPercentage": 100,
  "expertPercentage": 0,
  "platformPercentage": 0,
  "isActive": true
}
```

---

## 🎨 **Estructura de Datos para el Frontend**

### **Configuración de Estado de Cita**
```typescript
interface AppointmentStatusConfig {
  id: number;
  estado: string;           // Nombre amigable del estado
  statusId: number;         // ID del estado en SystemStatuses
  statusValue: string;      // Valor técnico del estado
  statusName: string;       // Nombre del enum
  cliente: number;          // Porcentaje para el cliente
  experto: number;          // Porcentaje para el experto
  plataforma: number;       // Porcentaje para la plataforma
  prioridad: string;        // "Por Status"
  activo: "Activo" | "Inactivo";
  categoryId?: number;      // ID de categoría (null = todas)
  categoryName: string;     // Nombre de categoría
  serviceTypeCategoryId?: number; // ID de tipo de servicio (null = todos)
  serviceTypeCategoryName: string; // Nombre del tipo de servicio
  createdAt: string;
  updatedAt: string;
}
```

### **Estado de Cita**
```typescript
interface AppointmentStatus {
  id: number;
  statusName: string;       // Nombre del enum
  statusValue: string;      // Valor técnico
  displayName: string;      // Nombre amigable
  description?: string;     // Descripción del estado
  sortOrder: number;        // Orden de visualización
  createdAt: string;
  updatedAt: string;
}
```

---

## 🔧 **Implementación en el Frontend**

### **1. Cargar Configuraciones**
```typescript
// Cargar configuraciones de estados de cita
const loadAppointmentStatusConfigs = async () => {
  try {
    const response = await fetch('/api/AppointmentConfig/appointment-status-configs');
    const configs = await response.json();
    
    // Procesar configuraciones
    configs.forEach(config => {
      console.log(`Estado: ${config.estado}`);
      console.log(`Cliente: ${config.cliente}%`);
      console.log(`Experto: ${config.experto}%`);
      console.log(`Plataforma: ${config.plataforma}%`);
    });
    
    return configs;
  } catch (error) {
    console.error('Error loading appointment status configs:', error);
  }
};
```

### **2. Renderizar Tabla de Configuraciones**
```typescript
// Componente de tabla para configuraciones
const AppointmentStatusConfigTable = ({ configs }) => {
  return (
    <table>
      <thead>
        <tr>
          <th>ESTADO</th>
          <th>CLIENTE</th>
          <th>EXPERTO</th>
          <th>PLATAFORMA</th>
          <th>PRIORIDAD</th>
          <th>ACTIVO</th>
          <th>ACCIONES</th>
        </tr>
      </thead>
      <tbody>
        {configs.map(config => (
          <tr key={config.id}>
            <td>{config.estado}</td>
            <td>{config.cliente}%</td>
            <td>{config.experto}%</td>
            <td>{config.plataforma}%</td>
            <td>{config.prioridad}</td>
            <td>
              <span className={`badge ${config.activo === 'Activo' ? 'badge-success' : 'badge-danger'}`}>
                {config.activo}
              </span>
            </td>
            <td>
              <button onClick={() => editConfig(config.id)}>✏️</button>
              <button onClick={() => deleteConfig(config.id)}>🗑️</button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
};
```

### **3. Crear Nueva Configuración**
```typescript
// Formulario para crear configuración
const CreateConfigForm = ({ onConfigCreated }) => {
  const [formData, setFormData] = useState({
    statusId: '',
    categoryId: null,
    serviceTypeCategoryId: null,
    clientPercentage: 0,
    expertPercentage: 0,
    platformPercentage: 0,
    isActive: true
  });

  const handleSubmit = async (e) => {
    e.preventDefault();
    
    // Validar que los porcentajes sumen 100%
    const total = formData.clientPercentage + formData.expertPercentage + formData.platformPercentage;
    if (total !== 100) {
      alert('Los porcentajes deben sumar 100%');
      return;
    }

    try {
      const response = await fetch('/api/AppointmentConfig/appointment-status-configs', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(formData)
      });

      if (response.ok) {
        const newConfig = await response.json();
        onConfigCreated(newConfig);
        // Limpiar formulario
        setFormData({
          statusId: '',
          categoryId: null,
          serviceTypeCategoryId: null,
          clientPercentage: 0,
          expertPercentage: 0,
          platformPercentage: 0,
          isActive: true
        });
      } else {
        const error = await response.json();
        alert(`Error: ${error.message}`);
      }
    } catch (error) {
      console.error('Error creating config:', error);
      alert('Error al crear la configuración');
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      {/* Campos del formulario */}
      <button type="submit">Crear Configuración</button>
    </form>
  );
};
```

---

## 🚨 **Problemas Comunes y Soluciones**

### **1. Columna "ESTADO" Vacía**
**Problema:** El frontend muestra columna "ESTADO" vacía
**Solución:** Usar `config.estado` en lugar de `config.statusName`

```typescript
// ❌ Incorrecto
<td>{config.statusName}</td>

// ✅ Correcto
<td>{config.estado}</td>
```

### **2. Porcentajes Mostrando Solo "%"**
**Problema:** Las columnas de porcentajes muestran solo "%"
**Solución:** Usar los valores numéricos correctos

```typescript
// ❌ Incorrecto
<td>{config.cliente}%</td> // Si config.cliente es undefined

// ✅ Correcto
<td>{config.cliente || 0}%</td>
```

### **3. Estado "Inactivo" en Columna "ACTIVO"**
**Problema:** Todas las configuraciones muestran "Inactivo"
**Solución:** Verificar que `config.activo` se esté procesando correctamente

```typescript
// ✅ Correcto
<td>
  <span className={config.activo === 'Activo' ? 'active' : 'inactive'}>
    {config.activo}
  </span>
</td>
```

---

## 🔍 **Debugging**

### **Endpoint de Debug**
```http
GET /api/AppointmentConfig/debug-status-data
```

**Respuesta:**
```json
{
  "message": "Debug data for AppointmentStatus",
  "appointmentStatusesCount": 10,
  "appointmentStatuses": [...],
  "statusConfigsCount": 7,
  "statusConfigs": [...]
}
```

### **Verificar Datos en el Frontend**
```typescript
// Función de debug para verificar datos
const debugConfigs = (configs) => {
  console.log('=== DEBUG CONFIGS ===');
  configs.forEach((config, index) => {
    console.log(`Config ${index + 1}:`);
    console.log(`  - Estado: "${config.estado}"`);
    console.log(`  - Cliente: ${config.cliente}%`);
    console.log(`  - Experto: ${config.experto}%`);
    console.log(`  - Plataforma: ${config.plataforma}%`);
    console.log(`  - Activo: "${config.activo}"`);
    console.log('---');
  });
};
```

---

## 📝 **Checklist de Implementación**

- [ ] **Cargar configuraciones** desde `/api/AppointmentConfig/appointment-status-configs`
- [ ] **Renderizar tabla** con columnas: ESTADO, CLIENTE, EXPERTO, PLATAFORMA, PRIORIDAD, ACTIVO, ACCIONES
- [ ] **Usar `config.estado`** para la columna ESTADO
- [ ] **Usar `config.cliente`, `config.experto`, `config.plataforma`** para los porcentajes
- [ ] **Usar `config.activo`** para el estado activo/inactivo
- [ ] **Implementar formulario** para crear nuevas configuraciones
- [ ] **Validar porcentajes** que sumen 100%
- [ ] **Manejar errores** de API correctamente
- [ ] **Probar con datos reales** del backend

---

## 🎯 **Ejemplo Completo de Uso**

```typescript
// Componente principal
const AppointmentStatusConfigPanel = () => {
  const [configs, setConfigs] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    loadConfigs();
  }, []);

  const loadConfigs = async () => {
    try {
      setLoading(true);
      const response = await fetch('/api/AppointmentConfig/appointment-status-configs');
      const data = await response.json();
      setConfigs(data);
    } catch (error) {
      console.error('Error loading configs:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleConfigCreated = (newConfig) => {
    setConfigs(prev => [...prev, newConfig]);
  };

  if (loading) return <div>Cargando...</div>;

  return (
    <div>
      <h2>Configuraciones de Estados de Cita</h2>
      <CreateConfigForm onConfigCreated={handleConfigCreated} />
      <AppointmentStatusConfigTable configs={configs} />
    </div>
  );
};
```

---

## 🚀 **Próximos Pasos**

1. **Implementar** la carga de configuraciones en el frontend
2. **Renderizar** la tabla con los datos correctos
3. **Probar** la funcionalidad de crear/editar configuraciones
4. **Verificar** que los porcentajes se muestren correctamente
5. **Implementar** la funcionalidad de edición y eliminación

---

**¿Necesitas ayuda con algún aspecto específico de la implementación?** 🤔

