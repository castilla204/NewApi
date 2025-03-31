using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using Newtonsoft.Json;

namespace newApi.RabbitMQ
{
    public class RabbitMQService : IRabbitMQService, IDisposable
    {
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly ILogger<RabbitMQService> _logger;
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests;
        private const int DEFAULT_TIMEOUT = 120000; // 2 minutes default timeout

        public RabbitMQService(IConnectionFactory connectionFactory, ILogger<RabbitMQService> logger)
        {
            _logger = logger;
            _pendingRequests = new Dictionary<string, TaskCompletionSource<string>>();

            try
            {
                var factory = (ConnectionFactory)connectionFactory;
                factory.RequestedHeartbeat = TimeSpan.FromSeconds(60);
                factory.NetworkRecoveryInterval = TimeSpan.FromSeconds(10);
                factory.AutomaticRecoveryEnabled = true;
                factory.RequestedConnectionTimeout = TimeSpan.FromSeconds(30);

                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                // Configure QoS
                _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

                _logger.LogInformation("RabbitMQ connection initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing RabbitMQ connection");
                throw;
            }
        }

        private void EnsureQueueExists(string queueName)
        {
            try
            {
                // Declare queue without any special arguments to ensure compatibility
                _channel.QueueDeclare(
                    queue: queueName,
                    durable: false,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                _logger.LogInformation($"Queue {queueName} declared/verified successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error declaring queue {queueName}");
                throw;
            }
        }

        public void PublishMessage<T>(string queueName, T message)
        {
            try
            {
                EnsureQueueExists(queueName);

                var json = JsonConvert.SerializeObject(message);
                var body = Encoding.UTF8.GetBytes(json);

                var properties = _channel.CreateBasicProperties();
                properties.Persistent = false;
                properties.ContentType = "application/json";
                properties.DeliveryMode = 1; // Non-persistent

                _channel.BasicPublish(
                    exchange: "",
                    routingKey: queueName,
                    basicProperties: properties,
                    body: body);

                _logger.LogInformation($"Message published to queue {queueName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error publishing message to queue {queueName}");
                throw;
            }
        }

        public async Task<T> SendAndReceiveAsync<T>(string requestQueueName, string replyQueueName, object message, int timeout = DEFAULT_TIMEOUT)
        {
            var correlationId = Guid.NewGuid().ToString();
            var replyEvent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string consumerTag = null;

            try
            {
                // Ensure both queues exist
                EnsureQueueExists(requestQueueName);
                EnsureQueueExists(replyQueueName);

                _pendingRequests[correlationId] = replyEvent;

                var consumer = new EventingBasicConsumer(_channel);
                consumerTag = _channel.BasicConsume(
                    queue: replyQueueName,
                    autoAck: true,
                    consumer: consumer);

                consumer.Received += (model, ea) =>
                {
                    try
                    {
                        if (ea.BasicProperties.CorrelationId == correlationId)
                        {
                            var response = Encoding.UTF8.GetString(ea.Body.ToArray());
                            replyEvent.TrySetResult(response);
                            _logger.LogInformation($"Received response for correlation ID {correlationId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing received message");
                        replyEvent.TrySetException(ex);
                    }
                };

                var props = _channel.CreateBasicProperties();
                props.CorrelationId = correlationId;
                props.ReplyTo = replyQueueName;
                props.ContentType = "application/json";
                props.DeliveryMode = 1; // Non-persistent

                var json = JsonConvert.SerializeObject(message);
                var body = Encoding.UTF8.GetBytes(json);

                _channel.BasicPublish(
                    exchange: "",
                    routingKey: requestQueueName,
                    basicProperties: props,
                    body: body);

                _logger.LogInformation($"Request sent to {requestQueueName} with correlation ID {correlationId}");

                using var cts = new CancellationTokenSource(timeout);

                var timeoutTask = Task.Delay(timeout, cts.Token);
                var responseTask = replyEvent.Task;

                var completedTask = await Task.WhenAny(responseTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning($"Request timed out after {timeout}ms for correlation ID {correlationId}");
                    throw new TimeoutException($"The request timed out after {timeout}ms. The scrapper service is taking longer than expected. Please try reducing the number of pages to scrape.");
                }

                cts.Cancel(); // Cancel the timeout task

                var responseJson = await responseTask;
                var result = JsonConvert.DeserializeObject<T>(responseJson);
                _logger.LogInformation($"Response received and deserialized for correlation ID {correlationId}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in SendAndReceiveAsync for correlation ID {correlationId}");
                throw;
            }
            finally
            {
                _pendingRequests.Remove(correlationId);
                if (!string.IsNullOrEmpty(consumerTag))
                {
                    try
                    {
                        _channel.BasicCancel(consumerTag);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error canceling consumer");
                    }
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_channel?.IsOpen == true)
                {
                    _channel.Close();
                }
                _channel?.Dispose();

                if (_connection?.IsOpen == true)
                {
                    _connection.Close();
                }
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing RabbitMQ resources");
            }
        }
    }
}