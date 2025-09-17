using newApi.Services;

namespace newApi.Services
{
    public class AppointmentTimerBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AppointmentTimerBackgroundService> _logger;
        private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5); // Verificar cada 5 minutos

        public AppointmentTimerBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<AppointmentTimerBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AppointmentTimerBackgroundService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var appointmentService = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
                    
                    await appointmentService.CheckAppointmentTimersAsync();
                    
                    _logger.LogDebug("Appointment timers checked successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking appointment timers");
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }

            _logger.LogInformation("AppointmentTimerBackgroundService stopped");
        }
    }
}
