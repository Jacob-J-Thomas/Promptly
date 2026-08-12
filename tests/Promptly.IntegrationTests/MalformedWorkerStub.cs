using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Promptly.IntegrationTests;

internal sealed class MalformedWorkerStub : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _handlerLock = new();
    private readonly HashSet<Task> _handlers = [];
    private Task? _acceptLoop;
    private int _disposed;

    public string BaseUrl { get; private set; } = string.Empty;

    public void Start()
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _acceptLoop = AcceptLoopAsync();
    }

    public async Task RunAndDisposeAsync(Func<MalformedWorkerStub, Task> operation)
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

    public async ValueTask DisposeAsync()
    {
        var failure = await DisposeResourcesAsync(primaryException: null);
        if (failure is not null)
        {
            CleanupRunner.Throw(failure);
        }
    }

    private async Task<Exception?> DisposeResourcesAsync(Exception? primaryException)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return primaryException;
        }

        var steps = new List<CleanupStep>
        {
            new(
                "cancel malformed worker stub",
                TimeSpan.FromSeconds(2),
                _ => _cancellation.CancelAsync()),
            new(
                "stop malformed worker listener",
                TimeSpan.FromSeconds(2),
                _ =>
                {
                    _listener.Stop();
                    return Task.CompletedTask;
                })
        };
        if (_acceptLoop is not null)
        {
            steps.Add(new(
                "wait for malformed worker accept loop",
                TimeSpan.FromSeconds(5),
                _ => _acceptLoop));
        }

        steps.Add(new(
            "wait for malformed worker handlers",
            TimeSpan.FromSeconds(5),
            _ =>
            {
                Task[] handlers;
                lock (_handlerLock)
                {
                    handlers = [.. _handlers];
                }

                return Task.WhenAll(handlers);
            }));
        steps.Add(new(
            "dispose malformed worker cancellation source",
            TimeSpan.FromSeconds(2),
            _ =>
            {
                _cancellation.Dispose();
                return Task.CompletedTask;
            }));

        return await CleanupRunner.RunAsync(primaryException, steps);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                TrackHandler(HandleSafelyAsync(client, _cancellation.Token));
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (SocketException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
    }

    private static async Task HandleSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private void TrackHandler(Task handler)
    {
        lock (_handlerLock)
        {
            _handlers.Add(handler);
        }

        _ = handler.ContinueWith(
            completed =>
            {
                lock (_handlerLock)
                {
                    _handlers.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var contentLength = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line && line.Length > 0)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                }
            }

            if (contentLength > 0)
            {
                var requestBody = new char[contentLength];
                _ = await reader.ReadBlockAsync(requestBody, cancellationToken);
            }

            const string malformedJson = "{\"mappingSpec\":";
            var body = Encoding.UTF8.GetBytes(malformedJson);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
}
