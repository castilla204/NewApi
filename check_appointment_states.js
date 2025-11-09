const { Client } = require('pg');

const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: 'Pedrohabo1//'
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
















