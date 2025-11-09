// 🎯 EJEMPLO PRÁCTICO - Cómo usar el nuevo sistema de estados centralizados

// ========================================
// 1. CARGAR CONFIGURACIONES DESDE EL API
// ========================================

async function loadAppointmentStatusConfigs() {
  try {
    
    const response = await fetch('/api/AppointmentConfig/appointment-status-configs');
    
    if (!response.ok) {
      throw new Error(`HTTP error! status: ${response.status}`);
    }
    
    const configs = await response.json();
    
    return configs;
  } catch (error) {
    console.error('❌ Error cargando configuraciones:', error);
    return [];
  }
}

// ========================================
// 2. PROCESAR Y MOSTRAR LOS DATOS
// ========================================

function displayConfigs(configs) {
  
  configs.forEach((config, index) => {
  });
}

// ========================================
// 3. RENDERIZAR TABLA HTML
// ========================================

function renderConfigTable(configs) {
  const tableContainer = document.getElementById('config-table-container');
  
  if (!tableContainer) {
    console.error('❌ No se encontró el contenedor de la tabla');
    return;
  }
  
  const tableHTML = `
    <table class="table table-striped">
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
        ${configs.map(config => `
          <tr>
            <td>${config.estado}</td>
            <td>${config.cliente}%</td>
            <td>${config.experto}%</td>
            <td>${config.plataforma}%</td>
            <td>${config.prioridad}</td>
            <td>
              <span class="badge ${config.activo === 'Activo' ? 'badge-success' : 'badge-danger'}">
                ${config.activo}
              </span>
            </td>
            <td>
              <button class="btn btn-sm btn-primary" onclick="editConfig(${config.id})">
                ✏️ Editar
              </button>
              <button class="btn btn-sm btn-danger" onclick="deleteConfig(${config.id})">
                🗑️ Eliminar
              </button>
            </td>
          </tr>
        `).join('')}
      </tbody>
    </table>
  `;
  
  tableContainer.innerHTML = tableHTML;
}

// ========================================
// 4. CREAR NUEVA CONFIGURACIÓN
// ========================================

async function createNewConfig(configData) {
  try {
    
    // Validar que los porcentajes sumen 100%
    const total = configData.clientPercentage + configData.expertPercentage + configData.platformPercentage;
    if (total !== 100) {
      throw new Error('Los porcentajes deben sumar 100%');
    }
    
    const response = await fetch('/api/AppointmentConfig/appointment-status-configs', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(configData)
    });
    
    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.message || 'Error al crear la configuración');
    }
    
    const newConfig = await response.json();
    
    return newConfig;
  } catch (error) {
    console.error('❌ Error creando configuración:', error);
    throw error;
  }
}

// ========================================
// 5. FUNCIONES DE UTILIDAD
// ========================================

function editConfig(configId) {
  // Implementar lógica de edición
}

function deleteConfig(configId) {
  // Implementar lógica de eliminación
}

// ========================================
// 6. FUNCIÓN PRINCIPAL DE INICIALIZACIÓN
// ========================================

async function initializeAppointmentConfigPanel() {
  try {
    
    // Cargar configuraciones
    const configs = await loadAppointmentStatusConfigs();
    
    if (configs.length === 0) {
      console.warn('⚠️ No se encontraron configuraciones');
      return;
    }
    
    // Mostrar datos en consola para debug
    displayConfigs(configs);
    
    // Renderizar tabla
    renderConfigTable(configs);
    
    
  } catch (error) {
    console.error('❌ Error inicializando panel:', error);
  }
}

// ========================================
// 7. EJEMPLO DE USO CON REACT
// ========================================

// Si estás usando React, aquí tienes un ejemplo de componente:

/*
import React, { useState, useEffect } from 'react';

const AppointmentStatusConfigPanel = () => {
  const [configs, setConfigs] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    loadConfigs();
  }, []);

  const loadConfigs = async () => {
    try {
      setLoading(true);
      setError(null);
      
      const response = await fetch('/api/AppointmentConfig/appointment-status-configs');
      
      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
      }
      
      const data = await response.json();
      setConfigs(data);
      
    } catch (err) {
      setError(err.message);
      console.error('Error loading configs:', err);
    } finally {
      setLoading(false);
    }
  };

  const handleCreateConfig = async (configData) => {
    try {
      const newConfig = await createNewConfig(configData);
      setConfigs(prev => [...prev, newConfig]);
    } catch (err) {
      setError(err.message);
    }
  };

  if (loading) return <div>Cargando configuraciones...</div>;
  if (error) return <div>Error: {error}</div>;

  return (
    <div>
      <h2>Configuraciones de Estados de Cita</h2>
      
      <table className="table table-striped">
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
                <button 
                  className="btn btn-sm btn-primary"
                  onClick={() => editConfig(config.id)}
                >
                  ✏️ Editar
                </button>
                <button 
                  className="btn btn-sm btn-danger"
                  onClick={() => deleteConfig(config.id)}
                >
                  🗑️ Eliminar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

export default AppointmentStatusConfigPanel;
*/

// ========================================
// 8. INICIALIZAR CUANDO SE CARGA LA PÁGINA
// ========================================

// Para usar con JavaScript vanilla:
document.addEventListener('DOMContentLoaded', function() {
  initializeAppointmentConfigPanel();
});

// ========================================
// 9. DEBUGGING Y VERIFICACIÓN
// ========================================

// Función para verificar que los datos se están procesando correctamente
function debugConfigs(configs) {
  
  configs.forEach((config, index) => {
  });
  
}

// ========================================
// 10. EJEMPLO DE DATOS ESPERADOS
// ========================================

/*
Los datos que devuelve el API tienen esta estructura:

[
  {
    "id": 12,
    "estado": "Cita Rechazada",
    "statusId": 12,
    "statusValue": "appointment_rejected",
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
    "createdAt": "2025-09-28T10:39:27.859129Z",
    "updatedAt": "2025-09-28T10:39:27.859129Z"
  }
]

IMPORTANTE:
- Usar "config.estado" para mostrar el nombre del estado
- Usar "config.cliente", "config.experto", "config.plataforma" para los porcentajes
- Usar "config.activo" para mostrar si está activo o inactivo
- Los valores son números, no strings
*/

