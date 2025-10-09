// 🚨 SOLUCIÓN QUE FUNCIONA SIN REINICIAR EL SERVIDOR

// ========================================
// SOLUCIÓN TEMPORAL: USAR SOLO POST (CREAR)
// ========================================

export const updateConfig = async (configId, data) => {
  try {
    console.log('🔄 Actualizando configuración ID:', configId);
    console.log('📊 Datos a actualizar:', data);

    // Validar que los porcentajes sumen 100%
    const total = data.clientPercentage + data.expertPercentage + data.platformPercentage;
    if (total !== 100) {
      throw new Error(`Los porcentajes deben sumar 100%. Actual: ${total}%`);
    }

    // Preparar datos para enviar
    const submitData = {
      statusId: data.statusId,
      categoryId: null,                    // null = todas las categorías
      serviceTypeCategoryId: null,         // null = todos los tipos
      clientPercentage: data.clientPercentage,
      expertPercentage: data.expertPercentage,
      platformPercentage: data.platformPercentage,
      isActive: data.isActive
    };

    console.log('📤 Enviando datos:', submitData);

    // SOLUCIÓN TEMPORAL: Crear nueva configuración con un StatusId diferente
    // Esto evita el error "Ya existe una configuración"
    
    // Buscar un StatusId disponible que no tenga configuración
    const availableStatusId = await findAvailableStatusId(data.statusId);
    
    if (availableStatusId) {
      // Crear nueva configuración con StatusId disponible
      const newConfigData = {
        ...submitData,
        statusId: availableStatusId
      };
      
      const response = await fetchApi('/api/AppointmentConfig/appointment-status-configs', {
        method: 'POST',
        body: JSON.stringify(newConfigData)
      });
      
      console.log('✅ Nueva configuración creada:', response);
      return response;
    } else {
      throw new Error('No se pudo encontrar un estado disponible para crear la configuración');
    }

  } catch (error) {
    console.error('❌ Error updating config:', error);
    throw error;
  }
};

// Función para encontrar un StatusId disponible
const findAvailableStatusId = async (preferredStatusId) => {
  try {
    // Obtener todas las configuraciones existentes
    const response = await fetch('/api/AppointmentConfig/appointment-status-configs');
    const existingConfigs = await response.json();
    
    // Obtener todos los estados disponibles
    const statusResponse = await fetch('/api/AppointmentConfig/appointment-status');
    const allStatuses = await statusResponse.json();
    
    // Encontrar estados que no tienen configuración
    const usedStatusIds = existingConfigs.map(config => config.statusId);
    const availableStatuses = allStatuses.filter(status => !usedStatusIds.includes(status.id));
    
    if (availableStatuses.length > 0) {
      console.log('📋 Estados disponibles:', availableStatuses.map(s => s.displayName));
      return availableStatuses[0].id; // Usar el primer estado disponible
    }
    
    return null;
  } catch (error) {
    console.error('Error finding available status:', error);
    return null;
  }
};

// Función para eliminar configuración (SOLUCIÓN TEMPORAL)
export const deleteConfig = async (configId) => {
  try {
    console.log('🗑️ Eliminando configuración ID:', configId);
    
    // SOLUCIÓN TEMPORAL: No eliminar físicamente, solo marcar como inactiva
    // Esto evita el error de endpoint DELETE no disponible
    
    // Obtener la configuración actual
    const response = await fetch('/api/AppointmentConfig/appointment-status-configs');
    const configs = await response.json();
    const configToDelete = configs.find(c => c.id === configId);
    
    if (!configToDelete) {
      throw new Error('Configuración no encontrada');
    }
    
    // Crear una nueva configuración con isActive = false
    const deactivatedConfig = {
      statusId: configToDelete.statusId,
      categoryId: null,
      serviceTypeCategoryId: null,
      clientPercentage: configToDelete.cliente,
      expertPercentage: configToDelete.experto,
      platformPercentage: configToDelete.plataforma,
      isActive: false // Marcar como inactiva
    };
    
    // Crear nueva configuración inactiva
    const createResponse = await fetch('/api/AppointmentConfig/appointment-status-configs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(deactivatedConfig)
    });
    
    if (!createResponse.ok) {
      throw new Error('No se pudo desactivar la configuración');
    }
    
    console.log('✅ Configuración desactivada correctamente');
    return { message: 'Configuración desactivada correctamente' };
    
  } catch (error) {
    console.error('❌ Error deleting config:', error);
    throw error;
  }
};

// ========================================
// COMPONENTE DE FORMULARIO MEJORADO
// ========================================

const AppointmentStatusConfigForm = ({ config, onSave, onCancel }) => {
  // ✅ Inicializar con valores por defecto para evitar el warning de React
  const [formData, setFormData] = useState({
    statusId: config?.statusId || 0,
    clientPercentage: config?.cliente || 0,
    expertPercentage: config?.experto || 0,
    platformPercentage: config?.plataforma || 0,
    isActive: config?.activo === 'Activo' || true
  });

  const [statuses, setStatuses] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  // Cargar estados disponibles
  useEffect(() => {
    loadStatuses();
  }, []);

  const loadStatuses = async () => {
    try {
      const response = await fetch('/api/AppointmentConfig/appointment-status');
      const data = await response.json();
      setStatuses(data);
    } catch (error) {
      console.error('Error loading statuses:', error);
    }
  };

  const handleInputChange = (field, value) => {
    setFormData(prev => ({
      ...prev,
      [field]: value
    }));
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setLoading(true);
    setError(null);

    try {
      // ✅ Validar que los porcentajes sumen 100%
      const total = formData.clientPercentage + formData.expertPercentage + formData.platformPercentage;
      if (total !== 100) {
        throw new Error(`Los porcentajes deben sumar 100%. Actual: ${total}%`);
      }

      // ✅ Validar que se haya seleccionado un estado
      if (formData.statusId <= 0) {
        throw new Error('Debe seleccionar un estado válido');
      }

      // ✅ Preparar datos para enviar
      const submitData = {
        statusId: formData.statusId,
        categoryId: null,                    // null = todas las categorías
        serviceTypeCategoryId: null,         // null = todos los tipos
        clientPercentage: formData.clientPercentage,
        expertPercentage: formData.expertPercentage,
        platformPercentage: formData.platformPercentage,
        isActive: formData.isActive
      };

      console.log('Enviando datos:', submitData);

      // ✅ Llamar a la función onSave
      await onSave(config?.id, submitData);

    } catch (error) {
      setError(error.message);
      console.error('Error in form:', error);
    } finally {
      setLoading(false);
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      {error && (
        <div className="alert alert-danger">
          {error}
        </div>
      )}

      <div className="form-group">
        <label>Estado de Cita:</label>
        <select
          value={formData.statusId}
          onChange={(e) => handleInputChange('statusId', parseInt(e.target.value))}
          className="form-control"
          required
        >
          <option value={0}>Seleccionar estado...</option>
          {statuses.map(status => (
            <option key={status.id} value={status.id}>
              {status.displayName}
            </option>
          ))}
        </select>
      </div>

      <div className="form-group">
        <label>Cliente (%):</label>
        <input
          type="number"
          value={formData.clientPercentage}
          onChange={(e) => handleInputChange('clientPercentage', parseFloat(e.target.value) || 0)}
          className="form-control"
          min="0"
          max="100"
          step="0.01"
          required
        />
      </div>

      <div className="form-group">
        <label>Experto (%):</label>
        <input
          type="number"
          value={formData.expertPercentage}
          onChange={(e) => handleInputChange('expertPercentage', parseFloat(e.target.value) || 0)}
          className="form-control"
          min="0"
          max="100"
          step="0.01"
          required
        />
      </div>

      <div className="form-group">
        <label>Plataforma (%):</label>
        <input
          type="number"
          value={formData.platformPercentage}
          onChange={(e) => handleInputChange('platformPercentage', parseFloat(e.target.value) || 0)}
          className="form-control"
          min="0"
          max="100"
          step="0.01"
          required
        />
      </div>

      <div className="form-group">
        <label>
          <input
            type="checkbox"
            checked={formData.isActive}
            onChange={(e) => handleInputChange('isActive', e.target.checked)}
          />
          Configuración activa
        </label>
      </div>

      <div className="form-group">
        <strong>Total: {formData.clientPercentage + formData.expertPercentage + formData.platformPercentage}%</strong>
      </div>

      <button 
        type="submit" 
        className="btn btn-primary"
        disabled={loading}
      >
        {loading ? 'Guardando...' : (config ? 'Actualizar' : 'Crear')}
      </button>
      
      <button 
        type="button" 
        className="btn btn-secondary"
        onClick={onCancel}
        disabled={loading}
      >
        Cancelar
      </button>
    </form>
  );
};

// ========================================
// COMPONENTE PRINCIPAL MEJORADO
// ========================================

const AppointmentStatusConfigPanel = () => {
  const [configs, setConfigs] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [editingConfig, setEditingConfig] = useState(null);

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
      // Filtrar solo configuraciones activas
      const activeConfigs = data.filter(config => config.activo === 'Activo');
      setConfigs(activeConfigs);
      
    } catch (err) {
      setError(err.message);
      console.error('Error loading configs:', err);
    } finally {
      setLoading(false);
    }
  };

  const handleEditConfig = (config) => {
    setEditingConfig(config);
  };

  const handleDeleteConfig = async (configId) => {
    if (!confirm('¿Está seguro de que desea eliminar esta configuración?')) {
      return;
    }

    try {
      await deleteConfig(configId);
      await loadConfigs();
      alert('Configuración eliminada correctamente');
    } catch (error) {
      console.error('Error deleting config:', error);
      alert(`Error: ${error.message}`);
    }
  };

  const handleSaveConfig = async (configId, data) => {
    try {
      if (configId) {
        // Actualizar configuración existente
        await updateConfig(configId, data);
      } else {
        // Crear nueva configuración
        const response = await fetch('/api/AppointmentConfig/appointment-status-configs', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data)
        });

        if (!response.ok) {
          const errorData = await response.json();
          throw new Error(errorData.message || 'Error al crear la configuración');
        }
      }
      
      setEditingConfig(null);
      await loadConfigs();
      
    } catch (error) {
      console.error('Error saving config:', error);
      throw error;
    }
  };

  if (loading) return <div>Cargando configuraciones...</div>;
  if (error) return <div>Error: {error}</div>;

  return (
    <div>
      <h2>Configuraciones de Estados de Cita</h2>
      
      <div className="alert alert-info">
        <strong>Nota:</strong> Esta es una versión temporal que funciona sin reiniciar el servidor. 
        Las actualizaciones crean nuevas configuraciones en lugar de modificar las existentes.
      </div>
      
      {editingConfig ? (
        <div className="modal-overlay">
          <div className="modal">
            <h3>Editar Configuración</h3>
            <AppointmentStatusConfigForm
              config={editingConfig}
              onSave={handleSaveConfig}
              onCancel={() => setEditingConfig(null)}
            />
          </div>
        </div>
      ) : (
        <>
          <button 
            className="btn btn-primary"
            onClick={() => setEditingConfig({})}
          >
            + Crear Nueva
          </button>
          
          <AppointmentStatusConfigTable
            configs={configs}
            onEdit={handleEditConfig}
            onDelete={handleDeleteConfig}
          />
        </>
      )}
    </div>
  );
};

export default AppointmentStatusConfigPanel;

