namespace Promptly.IntegrationTests;

internal sealed class IntegrationTestHost
{
    private readonly HttpClient _client;
    private int _cleanupStarted;

    private IntegrationTestHost(PromptlyWebApplicationFactory factory, HttpClient client)
    {
        Factory = factory;
        _client = client;
    }

    public PromptlyWebApplicationFactory Factory { get; }

    public HttpClient Client => _cleanupStarted == 0
        ? _client
        : throw new ObjectDisposedException(nameof(IntegrationTestHost));

    public static async Task<IntegrationTestHost> CreateAsync(
        PromptlyWebApplicationFactory factory)
    {
        try
        {
            var client = factory.CreateClient(new()
            {
                AllowAutoRedirect = false
            });
            return new IntegrationTestHost(factory, client);
        }
        catch (Exception primaryException)
        {
            var failure = await CleanupRunner.RunAsync(
                primaryException,
                [
                    new(
                        "dispose ASP.NET Core application factory after host creation failure",
                        TimeSpan.FromSeconds(15),
                        _ => Task.Run(factory.Dispose))
                ]);
            CleanupRunner.Throw(failure ?? primaryException);
            throw new InvalidOperationException("Unreachable after rethrowing host creation failure");
        }
    }

    public async Task RunAndDisposeAsync(Func<IntegrationTestHost, Task> operation)
    {
        Exception? primaryException = null;
        try
        {
            await operation(this);
        }
        catch (Exception exception)
        {
            primaryException = exception;
        }

        var failure = await DisposeResourcesAsync(primaryException);
        if (failure is not null)
        {
            CleanupRunner.Throw(failure);
        }
    }

    internal async Task<Exception?> DisposeResourcesAsync(Exception? primaryException)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return primaryException;
        }

        return await CleanupRunner.RunAsync(
            primaryException,
            [
                new(
                    "dispose ASP.NET Core test client",
                    TimeSpan.FromSeconds(5),
                    _ => Task.Run(_client.Dispose)),
                new(
                    "dispose ASP.NET Core application factory",
                    TimeSpan.FromSeconds(15),
                    _ => Task.Run(Factory.Dispose))
            ]);
    }
}
