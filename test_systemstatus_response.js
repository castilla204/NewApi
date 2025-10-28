// Test script to verify SystemStatus information in details-complete response
const axios = require('axios');

const API_BASE_URL = 'http://localhost:5000/api';

async function testSystemStatusResponse() {
    try {
        console.log('🧪 Testing SystemStatus information in details-complete response...\n');
        
        // Test with the search ID from the user's example
        const searchId = 243;
        
        const response = await axios.get(`${API_BASE_URL}/Search/${searchId}/details-complete`, {
            headers: {
                'X-Development-Mode': 'true' // Enable development mode
            }
        });
        
        console.log('✅ Response received successfully!');
        console.log('📊 Response structure:');
        
        const data = response.data;
        
        // Check SearchHire status info
        if (data.search?.searchHire?.statusInfo) {
            console.log('\n🔍 SearchHire Status Info:');
            console.log(`  - Status: ${data.search.searchHire.status}`);
            console.log(`  - Display Name: ${data.search.searchHire.statusInfo.displayName}`);
            console.log(`  - Description: ${data.search.searchHire.statusInfo.description}`);
            console.log(`  - Color: ${data.search.searchHire.statusInfo.color}`);
            console.log(`  - Status Type: ${data.search.searchHire.statusInfo.statusType}`);
            console.log(`  - Is Active: ${data.search.searchHire.statusInfo.isActive}`);
        } else {
            console.log('❌ SearchHire StatusInfo not found');
        }
        
        // Check Appointment status info
        if (data.appointment?.statusInfo) {
            console.log('\n📅 Appointment Status Info:');
            console.log(`  - Status: ${data.appointment.status}`);
            console.log(`  - Display Name: ${data.appointment.statusInfo.displayName}`);
            console.log(`  - Description: ${data.appointment.statusInfo.description}`);
            console.log(`  - Color: ${data.appointment.statusInfo.color}`);
            console.log(`  - Status Type: ${data.appointment.statusInfo.statusType}`);
            console.log(`  - Is Active: ${data.appointment.statusInfo.isActive}`);
        } else {
            console.log('❌ Appointment StatusInfo not found');
        }
        
        console.log('\n📋 Full response structure:');
        console.log(JSON.stringify(data, null, 2));
        
    } catch (error) {
        console.error('❌ Error testing SystemStatus response:');
        if (error.response) {
            console.error(`Status: ${error.response.status}`);
            console.error(`Data:`, error.response.data);
        } else {
            console.error(error.message);
        }
    }
}

// Run the test
testSystemStatusResponse();



