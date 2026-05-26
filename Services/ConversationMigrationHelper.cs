using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Services;

namespace newApi.Services
{
    /// <summary>
    /// Migra conversaciones pre-contratación al crear un SearchHire (pago Stripe o admin).
    /// </summary>
    public static class ConversationMigrationHelper
    {
        public static async Task EnsurePostHireConversationAsync(
            AppDbContext context,
            SearchHire searchHire,
            ILoggingService? loggingService = null)
        {
            if (searchHire.Id <= 0 || !searchHire.ExpertId.HasValue)
            {
                return;
            }

            var existingPostHire = await context.Conversations
                .AnyAsync(c => c.SearchHireId == searchHire.Id && c.IsActive);

            if (existingPostHire)
            {
                return;
            }

            var preHireConversation = await context.Conversations
                .Include(c => c.Messages)
                    .ThenInclude(m => m.Attachments)
                .Where(c =>
                    c.SearchServiceId == searchHire.SearchServiceId &&
                    c.SearchHireId == null &&
                    c.ClientId == searchHire.ClientId &&
                    c.ExpertId == searchHire.ExpertId.Value &&
                    c.IsActive)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefaultAsync();

            Conversation conversation;

            if (preHireConversation != null && preHireConversation.Messages.Any())
            {
                conversation = new Conversation
                {
                    SearchHireId = searchHire.Id,
                    SearchServiceId = null,
                    ClientId = searchHire.ClientId,
                    ExpertId = searchHire.ExpertId.Value,
                    IsActive = true,
                    CreatedAt = preHireConversation.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                    Messages = new List<Message>()
                };

                context.Conversations.Add(conversation);
                await context.SaveChangesAsync();

                foreach (var oldMessage in preHireConversation.Messages)
                {
                    var newMessage = new Message
                    {
                        ConversationId = conversation.Id,
                        SenderId = oldMessage.SenderId,
                        Content = oldMessage.Content,
                        SentAt = oldMessage.SentAt,
                        IsRead = oldMessage.IsRead,
                        LocationLatitude = oldMessage.LocationLatitude,
                        LocationLongitude = oldMessage.LocationLongitude
                    };

                    context.Messages.Add(newMessage);
                    await context.SaveChangesAsync();

                    if (oldMessage.Attachments != null && oldMessage.Attachments.Any())
                    {
                        foreach (var oldAttachment in oldMessage.Attachments)
                        {
                            context.MessageAttachments.Add(new MessageAttachment
                            {
                                MessageId = newMessage.Id,
                                Url = oldAttachment.Url,
                                ObjectName = oldAttachment.ObjectName,
                                Type = oldAttachment.Type,
                                CreatedAt = oldAttachment.CreatedAt
                            });
                        }
                    }
                }

                preHireConversation.IsActive = false;
                preHireConversation.UpdatedAt = DateTime.UtcNow;

                if (loggingService != null)
                {
                    await loggingService.LogInfoAsync(
                        message: "Pre-hire conversation messages migrated (Stripe/payment)",
                        details: $"Migrated {preHireConversation.Messages.Count} messages from pre-hire conversation {preHireConversation.Id} to conversation {conversation.Id} for SearchHire {searchHire.Id}",
                        userId: searchHire.ClientId,
                        source: "ConversationMigrationHelper.EnsurePostHireConversationAsync",
                        relatedEntityType: "Conversation",
                        relatedEntityId: conversation.Id);
                }
            }
            else
            {
                conversation = new Conversation
                {
                    SearchHireId = searchHire.Id,
                    SearchServiceId = null,
                    ClientId = searchHire.ClientId,
                    ExpertId = searchHire.ExpertId.Value,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Messages = new List<Message>()
                };

                context.Conversations.Add(conversation);
            }

            await context.SaveChangesAsync();
        }
    }
}
