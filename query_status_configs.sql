-- Verificar todas las StatusConfigurations
SELECT 
    sc.Id,
    sc.StatusId,
    sc.CategoryId,
    sc.ServiceTypeCategoryId,
    sc.ClientPercentage,
    sc.ExpertPercentage,
    sc.PlatformPercentage,
    sc.IsActive,
    s.DisplayName as StatusName,
    c.Name as CategoryName,
    stc.Name as ServiceTypeName
FROM StatusConfigurations sc
LEFT JOIN SystemStatuses s ON sc.StatusId = s.Id
LEFT JOIN Categories c ON sc.CategoryId = c.Id
LEFT JOIN ServiceTypeCategories stc ON sc.ServiceTypeCategoryId = stc.Id
WHERE s.StatusType = 'AppointmentStatus'
ORDER BY sc.CategoryId, sc.ServiceTypeCategoryId, sc.StatusId;
