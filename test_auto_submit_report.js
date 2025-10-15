const { Client } = require('pg');

const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: 'Pedrohabo1//'
});

async function testAutoSubmitReport() {
  try {
    await client.connect();
    console.log('🔌 Conectado a la base de datos');
    
    // 1. Buscar citas en awaiting_report con timers activos
    console.log('\n🔍 Buscando citas en awaiting_report con timers activos...');
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
    
    console.log(`📊 Citas en awaiting_report: ${appointmentsResult.rows.length}`);
    appointmentsResult.rows.forEach(row => {
      const timerStatus = row.timer_id ? '⏰ Timer activo' : '❌ Sin timer';
      console.log(`  - Cita ${row.appointment_id} (SearchHire ${row.searchhire_id}): ${timerStatus}`);
    });
    
    // 2. Para cada cita, verificar archivos subidos
    if (appointmentsResult.rows.length > 0) {
      console.log('\n📁 Verificando archivos subidos para cada cita...');
      
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
        
        console.log(`\n  📋 Cita ${appointment.appointment_id} (SearchHire ${appointment.searchhire_id}):`);
        console.log(`    Requeridos: ${requiredTypes.join(', ') || 'Ninguno'}`);
        console.log(`    Subidos: ${uploadedTypes.join(', ') || 'Ninguno'}`);
        
        const missingTypes = requiredTypes.filter(type => !uploadedTypes.includes(type));
        
        if (missingTypes.length === 0 && requiredTypes.length > 0) {
          console.log(`    ✅ TODOS LOS ARCHIVOS REQUERIDOS ESTÁN SUBIDOS`);
          console.log(`    🚀 Si expira el timer → Se enviará automáticamente a awaiting_client_decision`);
        } else if (missingTypes.length > 0) {
          console.log(`    ❌ Faltan archivos: ${missingTypes.join(', ')}`);
          console.log(`    🚫 Si expira el timer → Se cancelará por no reporte`);
        } else {
          console.log(`    ℹ️  No hay archivos requeridos para este servicio`);
          console.log(`    🚀 Si expira el timer → Se enviará automáticamente a awaiting_client_decision`);
        }
        
        // Mostrar tiempo restante del timer
        if (appointment.timer_id) {
          const timeRemaining = new Date(appointment.end_time) - new Date();
          const hoursRemaining = Math.max(0, Math.floor(timeRemaining / (1000 * 60 * 60)));
          const minutesRemaining = Math.max(0, Math.floor((timeRemaining % (1000 * 60 * 60)) / (1000 * 60)));
          
          if (timeRemaining > 0) {
            console.log(`    ⏰ Tiempo restante: ${hoursRemaining}h ${minutesRemaining}m`);
          } else {
            console.log(`    ⏰ Timer expirado - debería procesarse en la próxima ejecución`);
          }
        }
      }
    }
    
    // 3. Simular escenarios de prueba
    console.log('\n🧪 ESCENARIOS DE PRUEBA:');
    console.log('📝 Escenario 1: Experto sube todos los archivos pero se olvida de enviar reporte');
    console.log('   → Timer expira → Sistema detecta archivos completos → Auto-envía a awaiting_client_decision');
    console.log('');
    console.log('📝 Escenario 2: Experto no sube archivos requeridos');
    console.log('   → Timer expira → Sistema detecta archivos faltantes → Cancela por no reporte');
    console.log('');
    console.log('📝 Escenario 3: Experto envía reporte manualmente antes de que expire el timer');
    console.log('   → Timer se cancela → Cita pasa a awaiting_client_decision');
    
    // 4. Estadísticas de timers
    console.log('\n📈 Estadísticas de timers expert_report...');
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
      console.log(`  - ${row.metric}: ${row.count}`);
    });
    
    // 5. Verificar estado de la base de datos
    console.log('\n🔍 Verificando configuración del sistema...');
    const systemStatusResult = await client.query(`
      SELECT "StatusValue", "DisplayName"
      FROM "SystemStatuses" 
      WHERE "StatusValue" IN ('appointment_awaiting_report', 'awaiting_client_decision', 'appointment_cancelled_by_no_report')
      ORDER BY "StatusValue"
    `);
    
    console.log('📋 Estados del sistema:');
    systemStatusResult.rows.forEach(row => {
      console.log(`  - ${row.status_value}: ${row.display_name}`);
    });
    
  } catch (err) {
    console.error('❌ Error:', err.message);
  } finally {
    await client.end();
    console.log('\n🔌 Conexión cerrada');
  }
}

testAutoSubmitReport();






