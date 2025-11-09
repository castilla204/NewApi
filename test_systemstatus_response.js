// Test script to verify SystemStatus information in details-complete response
const axios = require('axios');

const API_BASE_URL = 'http://localhost:5000/api';

async function testSystemStatusResponse() {
    try {
        
        // Test with the search ID from the user's example
        const searchId = 243;
        
        const response = await axios.get(`${API_BASE_URL}/Search/${searchId}/details-complete`, {
            headers: {
                'X-Development-Mode': 'true' // Enable development mode
            }
        });
        
        
        const data = response.data;
        
        // Check SearchHire status info
        if (data.search?.searchHire?.statusInfo) {
        } else {
        }
        
        // Check Appointment status info
        if (data.appointment?.statusInfo) {
        } else {
        }
        
        
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



