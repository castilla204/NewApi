const { Client } = require('pg');

const client = new Client({
  host: process.env.POSTGRES_HOST || '185.166.39.4',
  port: parseInt(process.env.POSTGRES_PORT || '30000'),
  database: process.env.POSTGRES_DATABASE || 'atrapo',
  user: process.env.POSTGRES_USER || 'admin',
  password: process.env.POSTGRES_PASSWORD || process.env.PGPASSWORD || (() => {
    console.error('ERROR: POSTGRES_PASSWORD or PGPASSWORD environment variable not set');
    process.exit(1);
  })()
});

async function checkAppointmentStates() {
  try {
    await client.connect();
    
    // Verificar estados de Appointment
    const appointmentStates = await client.query(`
      SELECT "StatusValue", "DisplayName", "Description"
      FROM "SystemStatuses" 
      WHERE "StatusType" = 'AppointmentStatus'
      ORDER BY "StatusValue"
    `);
    
    appointmentStates.rows.forEach(row => {
    });
    
    // Verificar si appointment_completed existe
    const completedExists = appointmentStates.rows.some(row => row.status_value === 'appointment_completed');
    
    if (completedExists) {
    } else {
    }
    
  } catch (err) {
    console.error('❌ Error:', err.message);
  } finally {
    await client.end();
  }
}

checkAppointmentStates();
















