const { Client } = require('pg');

const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: 'Pedrohabo1//'
});

async function testExpertReportTimeout() {
  try {
    await client.connect();
    
    // 1. Verificar que el nuevo estado existe
    const statusResult = await client.query(`
      SELECT "StatusValue", "DisplayName", "Description"
      FROM "SystemStatuses" 
      WHERE "StatusValue" = 'appointment_cancelled_by_no_report'
    `);
    
    if (statusResult.rows.length > 0) {
    } else {
    }
    
    // 2. Buscar citas en awaiting_report
    const appointmentsResult = await client.query(`
      SELECT 
        a."Id" as appointment_id,
        a."ProposedDate",
        a."ProposedTime",
        s."StatusValue" as current_status,
        sh."Id" as searchhire_id
      FROM "Appointments" a
      JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
      JOIN "SearchHires" sh ON a."SearchHireId" = sh."Id"
      WHERE s."StatusValue" = 'appointment_awaiting_report'
      LIMIT 3
    `);
    
    appointmentsResult.rows.forEach(row => {
    });
    
    // 3. Verificar timers de expert_report
    const timersResult = await client.query(`
      SELECT 
        at."Id" as timer_id,
        at."AppointmentId",
        at."TimerType",
        at."StartTime",
        at."EndTime",
        at."IsExpired"
      FROM "AppointmentTimers" at
      WHERE at."TimerType" = 'expert_report'
      ORDER BY at."CreatedAt" DESC
      LIMIT 5
    `);
    
    timersResult.rows.forEach(row => {
      const status = row.is_expired ? '❌ Expirado' : '⏳ Activo';
    });
    
    // 4. Estadísticas generales
    const statsResult = await client.query(`
      SELECT 
        'Citas en awaiting_report' as metric,
        COUNT(*) as count
      FROM "Appointments" a
      JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
      WHERE s."StatusValue" = 'appointment_awaiting_report'
      
      UNION ALL
      
      SELECT 
        'Timers expert_report activos' as metric,
        COUNT(*) as count
      FROM "AppointmentTimers" 
      WHERE "TimerType" = 'expert_report' 
      AND "IsExpired" = false
      
      UNION ALL
      
      SELECT 
        'Timers expert_report expirados' as metric,
        COUNT(*) as count
      FROM "AppointmentTimers" 
      WHERE "TimerType" = 'expert_report' 
      AND "IsExpired" = true
    `);
    
    statsResult.rows.forEach(row => {
    });
    
  } catch (err) {
    console.error('❌ Error:', err.message);
  } finally {
    await client.end();
  }
}

testExpertReportTimeout();
















