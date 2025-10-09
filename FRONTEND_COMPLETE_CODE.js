// 🚨 CÓDIGO COMPLETO PARA EL FRONTEND - IMPLEMENTAR INMEDIATAMENTE

// ========================================
// 1. useAdminConfig.ts - FUNCIÓN CORREGIDA
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

    // OPCIÓN A: Intentar PUT primero (si el servidor se reinicia)
    try {
      const putResponse = await fetchApi(`/api/AppointmentConfig/appointment-status/${configId}`, {
        method: 'PUT',
        body: JSON.stringify(submitData)
      });
      
      console.log('✅ PUT exitoso:', putResponse);
      return putResponse;
      
    } catch (putError) {
      console.log('⚠️ PUT falló, usando POST como fallback');
      
      // OPCIÓN B: Usar POST como fallback (eliminar y crear)
      // Primero eliminar la configuración existente
      await deleteConfig(configId);
      
      // Luego crear una nueva
      const postResponse = await fetchApi('/api/AppointmentConfig/appointment-status-configs', {
        method: 'POST',
        body: JSON.stringify(submitData)
      });
      
      console.log('✅ POST exitoso:', postResponse);
      return postResponse;
    }

  } catch (error) {
    console.error('❌ Error updating config:', error);
    throw error;
  }
};

// Función para eliminar configuración
export const deleteConfig = async (configId) => {
  try {
    console.log('🗑️ Eliminando configuración ID:', configId);
    
    // OPCIÓN A: Intentar DELETE primero
    try {
      const deleteResponse = await fetchApi(`/api/AppointmentConfig/appointment-status/${configId}`, {
        method: 'DELETE'
      });
      
      console.log('✅ DELETE exitoso:', deleteResponse);
      return deleteResponse;
      
    } catch (deleteError) {
      console.log('⚠️ DELETE no disponible, usando método alternativo');
      
      // OPCIÓN B: Desactivar en lugar de eliminar
      const deactivateResponse = await fetchApi(`/api/AppointmentConfig/appointment-status/${configId}`, {
        method: 'PUT',
        body: JSON.stringify({
          statusId: 1, // ID temporal
          categoryId: null,
          serviceTypeCategoryId: null,
          clientPercentage: 0,
          expertPercentage: 0,
          platformPercentage: 0,
          isActive: false // Desactivar
        })
      });
      
      console.log('✅ Desactivación exitosa:', deactivateResponse);
      return deactivateResponse;
    }

  } catch (error) {
    console.error('❌ Error deleting config:', error);
    throw error;
  }
};

// ========================================
// 2. AdminPanel.tsx - FUNCIÓN CORREGIDA
// ========================================

const handleUpdateConfig = async (configId, formData) => {
  try {
    setLoading(true);
    setError(null);

    console.log('🔄 Actualizando configuración:', { configId, formData });

    // Validar datos
    if (!formData.statusId || formData.statusId <= 0) {
      throw new Error('Debe seleccionar un estado válido');
    }

    const total = formData.clientPercentage + formData.expertPercentage + formData.platformPercentage;
    if (total !== 100) {
      throw new Error(`Los porcentajes deben sumar 100%. Actual: ${total}%`);
    }

    // Llamar a la función de actualización
    await updateConfig(configId, formData);
    
    // Cerrar modal y recargar datos
    setEditingConfig(null);
    await loadConfigs();
    
    // Mostrar mensaje de éxito
    alert('Configuración actualizada correctamente');
    
  } catch (error) {
    console.error('Error updating config:', error);
    setError(error.message);
    alert(`Error: ${error.message}`);
  } finally {
    setLoading(false);
  }
};

// ========================================
// 3. COMPONENTE DE FORMULARIO CORREGIDO
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

      // ✅ Preparar datos para enviar (con null para categorías no especificadas)
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

      // ✅ Llamar a la función onSave (que manejará PUT o POST según corresponda)
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
// 4. TABLA DE CONFIGURACIONES CORREGIDA
// ========================================

const AppointmentStatusConfigTable = ({ configs, onEdit, onDelete }) => {
  return (
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
                onClick={() => onEdit(config)}
              >
                ✏️ Editar
              </button>
              <button 
                className="btn btn-sm btn-danger"
                onClick={() => onDelete(config.id)}
              >
                🗑️ Eliminar
              </button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
};

// ========================================
// 5. COMPONENTE PRINCIPAL CORREGIDO
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
      setConfigs(data);
      
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

