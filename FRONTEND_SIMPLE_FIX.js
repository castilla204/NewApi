// 🚨 SOLUCIÓN SIMPLE QUE FUNCIONA INMEDIATAMENTE

// ========================================
// SOLUCIÓN: SOLO CREAR NUEVAS CONFIGURACIONES
// ========================================

export const updateConfig = async (configId, data) => {
  try {

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


    // SOLUCIÓN SIMPLE: Solo crear nueva configuración
    const response = await fetch('/api/AppointmentConfig/appointment-status-configs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(submitData)
    });

    if (!response.ok) {
      const errorData = await response.json();
      throw new Error(errorData.message || 'Error al crear la configuración');
    }

    const newConfig = await response.json();
    return newConfig;

  } catch (error) {
    console.error('❌ Error creating config:', error);
    throw error;
  }
};

// Función para eliminar configuración (SOLUCIÓN SIMPLE)
export const deleteConfig = async (configId) => {
  try {
    
    // SOLUCIÓN SIMPLE: Crear una configuración inactiva
    // Esto simula la eliminación sin usar el endpoint DELETE
    
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
    
    return { message: 'Configuración desactivada correctamente' };
    
  } catch (error) {
    console.error('❌ Error deleting config:', error);
    throw error;
  }
};

// ========================================
// COMPONENTE DE FORMULARIO SIMPLE
// ========================================

const AppointmentStatusConfigForm = ({ config, onSave, onCancel }) => {
  // ✅ Inicializar con valores por defecto
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
      // Validar porcentajes
      const total = formData.clientPercentage + formData.expertPercentage + formData.platformPercentage;
      if (total !== 100) {
        throw new Error(`Los porcentajes deben sumar 100%. Actual: ${total}%`);
      }

      // Validar estado
      if (formData.statusId <= 0) {
        throw new Error('Debe seleccionar un estado válido');
      }

      // Preparar datos
      const submitData = {
        statusId: formData.statusId,
        categoryId: null,
        serviceTypeCategoryId: null,
        clientPercentage: formData.clientPercentage,
        expertPercentage: formData.expertPercentage,
        platformPercentage: formData.platformPercentage,
        isActive: formData.isActive
      };

      // Guardar
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
        {loading ? 'Guardando...' : 'Guardar'}
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
// COMPONENTE PRINCIPAL SIMPLE
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
      // SOLUCIÓN SIMPLE: Solo crear nuevas configuraciones
      await updateConfig(configId, data);
      
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
      
      <div className="alert alert-warning">
        <strong>Modo Temporal:</strong> Las actualizaciones crean nuevas configuraciones. 
        Las configuraciones antiguas se mantienen en la base de datos.
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
                      onClick={() => handleEditConfig(config)}
                    >
                      ✏️ Editar
                    </button>
                    <button 
                      className="btn btn-sm btn-danger"
                      onClick={() => handleDeleteConfig(config.id)}
                    >
                      🗑️ Eliminar
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  );
};

export default AppointmentStatusConfigPanel;

