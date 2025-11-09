// Script de prueba para verificar que los endpoints devuelven SystemStatusDto con colores
// Archivo: test_endpoints_with_colors.js

const axios = require('axios');

const BASE_URL = 'http://localhost:7124';

// Headers para desarrollo
const DEV_HEADERS = {
    'X-Development-Mode': 'true',
    'Content-Type': 'application/json'
};

async function testEndpoints() {

    try {
        // 1. Probar endpoint SearchHire/expert
        const expertResponse = await axios.get(`${BASE_URL}/api/SearchHire/expert`, {
            headers: DEV_HEADERS
        });
        
        if (expertResponse.data && expertResponse.data.length > 0) {
            const firstHire = expertResponse.data[0];
            
            if (firstHire.statusInfo) {
            } else {
            }
        }

        // 2. Probar endpoint Search con paginación
        const searchResponse = await axios.get(`${BASE_URL}/api/Search?page=1&pageSize=20&sortBy=createdAt&sortDirection=desc`, {
            headers: DEV_HEADERS
        });
        
        if (searchResponse.data && searchResponse.data.searches && searchResponse.data.searches.length > 0) {
            const firstSearch = searchResponse.data.searches[0];
            
            if (firstSearch.searchHire) {
                
                if (firstSearch.searchHire.statusInfo) {
                } else {
                }
            } else {
            }
        }

        // 3. Probar endpoint details-complete (ya sabemos que funciona)
        const detailsResponse = await axios.get(`${BASE_URL}/api/Search/243/details-complete`, {
            headers: DEV_HEADERS
        });
        
        if (detailsResponse.data && detailsResponse.data.search && detailsResponse.data.search.searchHire) {
            const searchHire = detailsResponse.data.search.searchHire;
            
            if (searchHire.statusInfo) {
            } else {
            }
        }

        // 4. Resumen de colores encontrados
        const allColors = new Set();
        
        // Buscar colores en SearchHire/expert
        if (expertResponse.data && expertResponse.data.length > 0) {
            expertResponse.data.forEach(hire => {
                if (hire.statusInfo && hire.statusInfo.color) {
                    allColors.add(`${hire.statusInfo.color} (${hire.statusInfo.displayName})`);
                }
            });
        }
        
        // Buscar colores en Search
        if (searchResponse.data && searchResponse.data.searches && searchResponse.data.searches.length > 0) {
            searchResponse.data.searches.forEach(search => {
                if (search.searchHire && search.searchHire.statusInfo && search.searchHire.statusInfo.color) {
                    allColors.add(`${search.searchHire.statusInfo.color} (${search.searchHire.statusInfo.displayName})`);
                }
            });
        }
        
        if (allColors.size > 0) {
        } else {
        }

        
    } catch (error) {
        console.error('❌ Error en las pruebas:', error.message);
        if (error.response) {
            console.error('   Status:', error.response.status);
            console.error('   Data:', error.response.data);
        }
    }
}

// Ejecutar las pruebas
testEndpoints();
