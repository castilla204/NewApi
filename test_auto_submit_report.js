const { Client } = require('pg');

const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: '__REDACTED_CREDENTIAL__'
});

async function testAutoSubmitReport() {
  try {
    await client.connect();
    
    // 1. Buscar citas en awaiting_report con timers activos
    const appointmentsResult = await client.query(`
      SELECT 
        a."Id" as appointment_id,
        a."SearchHireId",
        a."ProposedDate",
        a."ProposedTime",
        s."StatusValue" as current_status,
        at."Id" as timer_id,
        at."TimerType",
        at."StartTime",
        at."EndTime",
        at."IsExpired"
      FROM "Appointments" a
      JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
      LEFT JOIN "AppointmentTimers" at ON a."Id" = at."AppointmentId" 
        AND at."TimerType" = 'expert_report' 
        AND at."IsExpired" = false
      WHERE s."StatusValue" = 'appointment_awaiting_report'
      ORDER BY a."Id" DESC
      LIMIT 5
    `);
    
    appointmentsResult.rows.forEach(row => {
      const timerStatus = row.timer_id ? '⏰ Timer activo' : '❌ Sin timer';
    });
    
    // 2. Para cada cita, verificar archivos subidos
    if (appointmentsResult.rows.length > 0) {
      
      for (const appointment of appointmentsResult.rows) {
        if (!appointment.timer_id) continue; // Solo procesar citas con timer
        
        // Obtener tipos de entregables requeridos
        const requiredTypesResult = await client.query(`
          SELECT dt."Name" as type_name
          FROM "SearchHires" sh
          JOIN "SearchServices" ss ON sh."SearchServiceId" = ss."Id"
          JOIN "SearchServiceDeliverableTypes" ssdt ON ss."Id" = ssdt."SearchServiceId" AND ssdt."IsSelected" = true
          JOIN "DeliverableTypes" dt ON ssdt."DeliverableTypeId" = dt."Id"
          WHERE sh."Id" = $1
        `, [appointment.searchhire_id]);
        
        // Obtener archivos subidos
        const uploadedFilesResult = await client.query(`
          SELECT "Type" as file_type, "Url", "CreatedAt"
          FROM "SearchHireDeliverables"
          WHERE "SearchHireId" = $1
          ORDER BY "CreatedAt" DESC
        `, [appointment.searchhire_id]);
        
        const requiredTypes = requiredTypesResult.rows.map(r => r.type_name.toLowerCase());
        const uploadedTypes = uploadedFilesResult.rows.map(r => r.file_type);
        
        
        const missingTypes = requiredTypes.filter(type => !uploadedTypes.includes(type));
        
        if (missingTypes.length === 0 && requiredTypes.length > 0) {
        } else if (missingTypes.length > 0) {
        } else {
        }
        
        // Mostrar tiempo restante del timer
        if (appointment.timer_id) {
          const timeRemaining = new Date(appointment.end_time) - new Date();
          const hoursRemaining = Math.max(0, Math.floor(timeRemaining / (1000 * 60 * 60)));
          const minutesRemaining = Math.max(0, Math.floor((timeRemaining % (1000 * 60 * 60)) / (1000 * 60)));
          
          if (timeRemaining > 0) {
          } else {
          }
        }
      }
    }
    
    // 3. Simular escenarios de prueba
    
    // 4. Estadísticas de timers
    const timerStatsResult = await client.query(`
      SELECT 
        'Timers activos' as metric,
        COUNT(*) as count
      FROM "AppointmentTimers" 
      WHERE "TimerType" = 'expert_report' 
      AND "IsExpired" = false
      
      UNION ALL
      
      SELECT 
        'Timers expirados' as metric,
        COUNT(*) as count
      FROM "AppointmentTimers" 
      WHERE "TimerType" = 'expert_report' 
      AND "IsExpired" = true
      
      UNION ALL
      
      SELECT 
        'Timers que expiran en < 1 hora' as metric,
        COUNT(*) as count
      FROM "AppointmentTimers" 
      WHERE "TimerType" = 'expert_report' 
      AND "IsExpired" = false
      AND "EndTime" <= CURRENT_TIMESTAMP + INTERVAL '1 hour'
    `);
    
    timerStatsResult.rows.forEach(row => {
    });
    
    // 5. Verificar estado de la base de datos
    const systemStatusResult = await client.query(`
      SELECT "StatusValue", "DisplayName"
      FROM "SystemStatuses" 
      WHERE "StatusValue" IN ('appointment_awaiting_report', 'awaiting_client_decision', 'appointment_cancelled_by_no_report')
      ORDER BY "StatusValue"
    `);
    
    systemStatusResult.rows.forEach(row => {
    });
    
  } catch (err) {
    console.error('❌ Error:', err.message);
  } finally {
    await client.end();
  }
}

testAutoSubmitReport();
















