namespace newApi.RabbitMQ
{
    public interface IRabbitMQService : IDisposable
    {
        void PublishMessage<T>(string queueName, T message);
        Task<T> SendAndReceiveAsync<T>(string requestQueueName, string replyQueueName, object message, int timeout = 120000); // Increased to 2 minutes
    }
}