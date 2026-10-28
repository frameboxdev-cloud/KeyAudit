namespace FontsTry;

public class Worker(ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Z linii komend (--events KeyDown,KeyUp) albo z appsettings.json ("Events")
        var events = configuration.GetValue("Events", KeyboardEvents.All);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hook LL potrzebuje własnego wątku z pętlą komunikatów Windows
        var hookThread = new Thread(() =>
        {
            try
            {
                KeyboardHook.Run(events, stoppingToken);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Błąd hooka klawiatury");
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "KeyboardHook"
        };
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();

        return completion.Task;
    }
}
