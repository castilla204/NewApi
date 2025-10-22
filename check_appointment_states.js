const { Client } = require('pg');

const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: '__REDACTED_CREDENTIAL__'
});

async function checkAppointmentStates() {
  try {
    await client.connect();
    console.log('🔌 Conectado a la base de datos');
    
    // Verificar estados de Appointment
    console.log('\n📋 Estados de Appointment:');
    const appointmentStates = await client.query(`
      SELECT "StatusValue", "DisplayName", "Description"
      FROM "SystemStatuses" 
      WHERE "StatusType" = 'AppointmentStatus'
      ORDER BY "StatusValue"
    `);
    
    appointmentStates.rows.forEach(row => {
      console.log(`  - ${row.status_value}: ${row.display_name}`);
    });
    
    // Verificar si appointment_completed existe
    const completedExists = appointmentStates.rows.some(row => row.status_value === 'appointment_completed');
    
    if (completedExists) {
      console.log('\n✅ Estado appointment_completed existe');
    } else {
      console.log('\n❌ Estado appointment_completed NO existe - necesitamos crearlo');
    }
    
  } catch (err) {
    console.error('❌ Error:', err.message);
  } finally {
    await client.end();
    console.log('\n🔌 Conexión cerrada');
  }
}

checkAppointmentStates();














