// Ejemplo práctico de implementación para el frontend
// Archivo: components/StatusBadge.tsx

import React from 'react';

interface SystemStatusInfo {
  id: number;
  statusType: string;
  statusName: string;
  statusValue: string;
  displayName: string;
  description: string | null;
  color: string | null;
  isActive: boolean;
  isFinalizationStatus: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

interface StatusBadgeProps {
  statusInfo: SystemStatusInfo;
  size?: 'sm' | 'md' | 'lg';
  showDescription?: boolean;
  className?: string;
}

export const StatusBadge: React.FC<StatusBadgeProps> = ({ 
  statusInfo, 
  size = 'md', 
  showDescription = false,
  className = ''
}) => {
  const sizeClasses = {
    sm: 'px-2 py-1 text-xs',
    md: 'px-3 py-1.5 text-sm', 
    lg: 'px-4 py-2 text-base'
  };

  const getStatusStyle = () => {
    const baseColor = statusInfo.color || '#6C757D';
    return {
      backgroundColor: `${baseColor}20`,
      color: baseColor,
      border: `1px solid ${baseColor}40`
    };
  };

  return (
    <div className={`inline-flex flex-col ${className}`}>
      <span
        className={`inline-flex items-center rounded-full font-medium ${sizeClasses[size]}`}
        style={getStatusStyle()}
        title={statusInfo.description || statusInfo.displayName}
      >
        {statusInfo.displayName}
      </span>
      
      {showDescription && statusInfo.description && (
        <span className="text-xs text-gray-500 mt-1 max-w-xs">
          {statusInfo.description}
        </span>
      )}
    </div>
  );
};

// Archivo: components/SearchDetails.tsx
interface SearchDetailsCompleteResponse {
  search: {
    id: number;
    userId: number;
    title: string;
    description: string;
    searchHire: {
      id: number;
      status: string;
      statusInfo: SystemStatusInfo;
      expert: {
        id: number;
        name: string;
        email: string;
        profilePictureUrl: string | null;
      } | null;
    } | null;
  };
  appointment: {
    id: number;
    status: string;
    statusInfo: SystemStatusInfo;
    proposedDate: string;
    proposedTime: string;
    location: string;
  } | null;
}

interface SearchDetailsProps {
  searchData: SearchDetailsCompleteResponse;
}

export const SearchDetails: React.FC<SearchDetailsProps> = ({ searchData }) => {
  return (
    <div className="bg-white rounded-lg shadow-md p-6 space-y-6">
      {/* Información básica */}
      <div>
        <h2 className="text-xl font-semibold text-gray-900 mb-2">
          {searchData.search.title}
        </h2>
        <p className="text-gray-600">{searchData.search.description}</p>
      </div>

      {/* Estado de Contratación */}
      {searchData.search.searchHire && (
        <div className="border-t pt-4">
          <h3 className="text-lg font-medium text-gray-900 mb-3">
            Estado de Contratación
          </h3>
          
          <div className="flex items-center gap-3">
            <StatusBadge 
              statusInfo={searchData.search.searchHire.statusInfo} 
              size="md"
              showDescription={true}
            />
            
            {searchData.search.searchHire.expert && (
              <div className="flex items-center gap-2">
                <img 
                  src={searchData.search.searchHire.expert.profilePictureUrl || '/default-avatar.png'} 
                  alt={searchData.search.searchHire.expert.name}
                  className="w-8 h-8 rounded-full"
                />
                <span className="text-sm text-gray-600">
                  {searchData.search.searchHire.expert.name}
                </span>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Estado de Cita */}
      {searchData.appointment && (
        <div className="border-t pt-4">
          <h3 className="text-lg font-medium text-gray-900 mb-3">
            Estado de Cita
          </h3>
          
          <div className="space-y-3">
            <StatusBadge 
              statusInfo={searchData.appointment.statusInfo} 
              size="md"
              showDescription={true}
            />
            
            <div className="text-sm text-gray-600">
              <p><strong>Fecha:</strong> {new Date(searchData.appointment.proposedDate).toLocaleDateString()}</p>
              <p><strong>Hora:</strong> {searchData.appointment.proposedTime}</p>
              <p><strong>Ubicación:</strong> {searchData.appointment.location}</p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

// Archivo: utils/statusUtils.ts
export const getStatusColor = (statusInfo: SystemStatusInfo): string => {
  return statusInfo.color || '#6C757D';
};

export const getStatusDisplayName = (statusInfo: SystemStatusInfo): string => {
  return statusInfo.displayName || statusInfo.statusValue;
};

export const getStatusDescription = (statusInfo: SystemStatusInfo): string => {
  return statusInfo.description || statusInfo.displayName || statusInfo.statusValue;
};

export const isFinalizationStatus = (statusInfo: SystemStatusInfo): boolean => {
  return statusInfo.isFinalizationStatus;
};

export const getStatusPriority = (statusInfo: SystemStatusInfo): 'low' | 'medium' | 'high' => {
  if (statusInfo.isFinalizationStatus) return 'high';
  if (statusInfo.statusValue.includes('pending') || statusInfo.statusValue.includes('awaiting')) return 'medium';
  return 'low';
};

// Archivo: hooks/useSearchDetails.ts
import { useState, useEffect } from 'react';

export const useSearchDetails = (searchId: number) => {
  const [searchData, setSearchData] = useState<SearchDetailsCompleteResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchSearchDetails = async () => {
      try {
        setLoading(true);
        const response = await fetch(`/api/Search/${searchId}/details-complete`, {
          headers: {
            'X-Development-Mode': 'true'
          }
        });
        
        if (!response.ok) {
          throw new Error('Error al cargar los detalles de la búsqueda');
        }
        
        const data = await response.json();
        setSearchData(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Error desconocido');
      } finally {
        setLoading(false);
      }
    };

    fetchSearchDetails();
  }, [searchId]);

  return { searchData, loading, error };
};

// Archivo: pages/SearchDetailsPage.tsx
import React from 'react';
import { useSearchDetails } from '../hooks/useSearchDetails';
import { SearchDetails } from '../components/SearchDetails';

interface SearchDetailsPageProps {
  searchId: number;
}

export const SearchDetailsPage: React.FC<SearchDetailsPageProps> = ({ searchId }) => {
  const { searchData, loading, error } = useSearchDetails(searchId);

  if (loading) {
    return (
      <div className="flex justify-center items-center h-64">
        <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600"></div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4">
        <p className="text-red-800">{error}</p>
      </div>
    );
  }

  if (!searchData) {
    return (
      <div className="bg-yellow-50 border border-yellow-200 rounded-md p-4">
        <p className="text-yellow-800">No se encontraron datos para esta búsqueda</p>
      </div>
    );
  }

  return <SearchDetails searchData={searchData} />;
};
