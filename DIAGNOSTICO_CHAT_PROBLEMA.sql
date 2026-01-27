-- 🔍 DIAGNÓSTICO: Verificar conversaciones y mensajes para SearchServiceId = 72
-- Ejecutar esta consulta en la base de datos PRINCIPAL (no Supabase)

-- 1. Ver todas las conversaciones para SearchServiceId = 72
SELECT 
    c."Id" as conversation_id,
    c."SearchServiceId",
    c."ClientId",
    c."ExpertId",
    c."CreatedAt",
    c."UpdatedAt",
    c."IsActive",
    u_client."Name" as client_name,
    u_client."Email" as client_email,
    u_expert."Name" as expert_name,
    u_expert."Email" as expert_email,
    COUNT(m."Id") as total_messages
FROM "Conversations" c
LEFT JOIN "Users" u_client ON u_client."Id" = c."ClientId"
LEFT JOIN "Users" u_expert ON u_expert."Id" = c."ExpertId"
LEFT JOIN "Messages" m ON m."ConversationId" = c."Id"
WHERE c."SearchServiceId" = 72
GROUP BY c."Id", c."SearchServiceId", c."ClientId", c."ExpertId", c."CreatedAt", c."UpdatedAt", c."IsActive",
         u_client."Name", u_client."Email", u_expert."Name", u_expert."Email"
ORDER BY c."CreatedAt" DESC;

-- 2. Ver TODOS los mensajes de todas las conversaciones para SearchServiceId = 72
-- Esto mostrará quién envió cada mensaje
SELECT 
    m."Id" as message_id,
    m."ConversationId",
    m."SenderId",
    m."Content",
    m."SentAt",
    m."IsRead",
    u_sender."Name" as sender_name,
    u_sender."Email" as sender_email,
    c."ClientId" as conversation_client_id,
    c."ExpertId" as conversation_expert_id,
    u_client."Name" as conversation_client_name,
    u_expert."Name" as conversation_expert_name
FROM "Messages" m
INNER JOIN "Conversations" c ON c."Id" = m."ConversationId"
LEFT JOIN "Users" u_sender ON u_sender."Id" = m."SenderId"
LEFT JOIN "Users" u_client ON u_client."Id" = c."ClientId"
LEFT JOIN "Users" u_expert ON u_expert."Id" = c."ExpertId"
WHERE c."SearchServiceId" = 72
ORDER BY m."SentAt" ASC;

-- 3. Verificar si hay mensajes de otros clientes en la conversación del usuario actual
-- Reemplazar {TU_USER_ID} con tu ID de usuario
SELECT 
    c."Id" as conversation_id,
    c."ClientId" as conversation_client_id,
    m."Id" as message_id,
    m."SenderId" as message_sender_id,
    m."Content",
    m."SentAt",
    u_sender."Name" as sender_name,
    u_sender."Email" as sender_email,
    CASE 
        WHEN m."SenderId" = c."ClientId" THEN 'Cliente de esta conversación'
        WHEN m."SenderId" = c."ExpertId" THEN 'Experto'
        ELSE '⚠️ OTRO USUARIO (PROBLEMA!)'
    END as sender_type
FROM "Messages" m
INNER JOIN "Conversations" c ON c."Id" = m."ConversationId"
LEFT JOIN "Users" u_sender ON u_sender."Id" = m."SenderId"
WHERE c."SearchServiceId" = 72
  AND c."ClientId" = {TU_USER_ID}  -- ⚠️ REEMPLAZAR con tu userId
ORDER BY m."SentAt" ASC;

-- 4. Verificar si hay múltiples conversaciones para el mismo SearchServiceId con diferentes clientes
SELECT 
    c."SearchServiceId",
    COUNT(DISTINCT c."Id") as total_conversations,
    COUNT(DISTINCT c."ClientId") as total_different_clients,
    STRING_AGG(DISTINCT c."ClientId"::text, ', ') as client_ids,
    STRING_AGG(DISTINCT u_client."Name", ', ') as client_names
FROM "Conversations" c
LEFT JOIN "Users" u_client ON u_client."Id" = c."ClientId"
WHERE c."SearchServiceId" = 72
GROUP BY c."SearchServiceId";
