using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using Newtonsoft.Json;

namespace newApi.RabbitMQ
{
    public class RabbitMQService : IRabbitMQService, IDisposable
    {
        private IConnection _connection;
        private IChannel _channel;
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests;
        private const int DEFAULT_TIMEOUT = 120000; // 2 minutes default timeout
        private static readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);
        private bool _initialized = false;

        public RabbitMQService(IConnectionFactory connectionFactory)
        {
            _pendingRequests = new Dictionary<string, TaskCompletionSource<string>>();
            
            // Initialize asynchronously - use GetAwaiter().GetResult() to make it synchronous in constructor
            InitializeAsync(connectionFactory).GetAwaiter().GetResult();
        }

        private async Task InitializeAsync(IConnectionFactory connectionFactory)
        {
            await _initSemaphore.WaitAsync();
            try
            {
                if (_initialized) return;

                var factory = (ConnectionFactory)connectionFactory;
                factory.RequestedHeartbeat = TimeSpan.FromSeconds(60);
                factory.NetworkRecoveryInterval = TimeSpan.FromSeconds(10);
                factory.AutomaticRecoveryEnabled = true;
                factory.RequestedConnectionTimeout = TimeSpan.FromSeconds(30);

                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync();

                // Configure QoS
                await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

                _initialized = true;
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        private async Task EnsureQueueExistsAsync(string queueName)
        {
            try
            {
                // Declare queue without any special arguments to ensure compatibility
                await _channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: false,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public void PublishMessage<T>(string queueName, T message)
        {
            try
            {
                PublishMessageAsync(queueName, message).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private async Task PublishMessageAsync<T>(string queueName, T message)
        {
            await EnsureQueueExistsAsync(queueName);

            var json = JsonConvert.SerializeObject(message);
            var body = Encoding.UTF8.GetBytes(json);

            var properties = new BasicProperties
            {
                Persistent = false,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Transient
            };

            await _channel.BasicPublishAsync(
                exchange: "",
                routingKey: queueName,
                mandatory: false,
                basicProperties: properties,
                body: body);
        }

        public async Task<T> SendAndReceiveAsync<T>(string requestQueueName, string replyQueueName, object message, int timeout = DEFAULT_TIMEOUT)
        {
            var correlationId = Guid.NewGuid().ToString();
            var replyEvent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string consumerTag = null;

            try
            {
                // Ensure both queues exist
                await EnsureQueueExistsAsync(requestQueueName);
                await EnsureQueueExistsAsync(replyQueueName);

                _pendingRequests[correlationId] = replyEvent;

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumerTag = await _channel.BasicConsumeAsync(
                    queue: replyQueueName,
                    autoAck: true,
                    consumer: consumer);

                consumer.ReceivedAsync += async (model, ea) =>
                {
                    try
                    {
                        if (ea.BasicProperties.CorrelationId == correlationId)
                        {
                            var response = Encoding.UTF8.GetString(ea.Body.ToArray());
                            replyEvent.TrySetResult(response);
                        }
                    }
                    catch (Exception ex)
                    {
                        replyEvent.TrySetException(ex);
                    }
                    await Task.CompletedTask;
                };

                var props = new BasicProperties
                {
                    CorrelationId = correlationId,
                    ReplyTo = replyQueueName,
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Transient
                };

                var json = JsonConvert.SerializeObject(message);
                var body = Encoding.UTF8.GetBytes(json);

                await _channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: requestQueueName,
                    mandatory: false,
                    basicProperties: props,
                    body: body);
                using var cts = new CancellationTokenSource(timeout);

                var timeoutTask = Task.Delay(timeout, cts.Token);
                var responseTask = replyEvent.Task;

                var completedTask = await Task.WhenAny(responseTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException($"The request timed out after {timeout}ms. The scrapper service is taking longer than expected. Please try reducing the number of pages to scrape.");
                }

                cts.Cancel(); // Cancel the timeout task

                var responseJson = await responseTask;
                var result = JsonConvert.DeserializeObject<T>(responseJson);
                return result;
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                _pendingRequests.Remove(correlationId);
                if (!string.IsNullOrEmpty(consumerTag))
                {
                    try
                    {
                        await _channel.BasicCancelAsync(consumerTag);
                    }
                    catch (Exception ex)
                    {
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
                    _channel.CloseAsync().GetAwaiter().GetResult();
                }
                _channel?.Dispose();

                if (_connection?.IsOpen == true)
                {
                    _connection.CloseAsync().GetAwaiter().GetResult();
                }
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
            }
        }
    }
}