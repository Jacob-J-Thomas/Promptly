using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Promptly.Server.Security;

public interface IAuthenticationRequestAdmissionGate
{
    IAuthenticationRequestAdmissionLease? TryAcquire(AuthenticationOperation operation);
}

public interface IAuthenticationRequestAdmissionLease : IDisposable;

public static class AuthenticationRequestAdmissionMetrics
{
    public const string MeterName = "Promptly.Server.Authentication";
    public const string AdmittedInstrumentName = "promptly.authentication.requests.admitted";
    public const string RejectedInstrumentName = "promptly.authentication.requests.rejected";
    public const string InFlightInstrumentName = "promptly.authentication.requests.in_flight";
    public const string OperationTagName = "operation";
    public const string LoginOperationTagValue = "login";
    public const string RegistrationOperationTagValue = "registration";

    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Counter<long> Admitted = Meter.CreateCounter<long>(
        AdmittedInstrumentName,
        unit: "{request}",
        description: "Authentication requests admitted by the process-wide concurrency gate.");

    internal static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        RejectedInstrumentName,
        unit: "{request}",
        description: "Authentication requests rejected by the process-wide concurrency gate.");

    internal static readonly UpDownCounter<long> InFlight = Meter.CreateUpDownCounter<long>(
        InFlightInstrumentName,
        unit: "{request}",
        description: "Authentication requests currently holding process-wide admission.");

    internal static void RecordAdmitted(string operationTag)
    {
        TryAdd(Admitted, 1, operationTag);
        TryAdd(InFlight, 1, operationTag);
    }

    internal static void RecordRejected(string operationTag) =>
        TryAdd(Rejected, 1, operationTag);

    internal static void RecordReleased(string operationTag) =>
        TryAdd(InFlight, -1, operationTag);

    internal static string GetOperationTagValue(AuthenticationOperation operation) =>
        operation switch
        {
            AuthenticationOperation.Login => LoginOperationTagValue,
            AuthenticationOperation.Registration => RegistrationOperationTagValue,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.")
        };

    private static void TryAdd(Counter<long> instrument, long value, string operationTag)
    {
        try
        {
            instrument.Add(value, CreateOperationTag(operationTag));
        }
        // codeql[cs/catch-of-all-exceptions]
        catch (Exception)
        {
            // Metrics observers are untrusted extensions. Telemetry must never
            // leak admission capacity or turn an otherwise valid request into a 500.
        }
    }

    private static void TryAdd(UpDownCounter<long> instrument, long value, string operationTag)
    {
        try
        {
            instrument.Add(value, CreateOperationTag(operationTag));
        }
        // codeql[cs/catch-of-all-exceptions]
        catch (Exception)
        {
            // Metrics observers are untrusted extensions. Telemetry must never
            // leak admission capacity or turn an otherwise valid request into a 500.
        }
    }

    private static KeyValuePair<string, object?> CreateOperationTag(string operationTag) =>
        new(OperationTagName, operationTag);
}

public sealed class AuthenticationRequestAdmissionGate : IAuthenticationRequestAdmissionGate
{
    private readonly int _maximumConcurrentRequests;
    private int _inFlight;

    public AuthenticationRequestAdmissionGate(IOptions<AuthenticationAbuseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var maximumConcurrentRequests = options.Value.MaximumConcurrentAuthenticationRequests;
        if (maximumConcurrentRequests is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                maximumConcurrentRequests,
                "Maximum concurrent authentication requests must be between 1 and 256.");
        }

        _maximumConcurrentRequests = maximumConcurrentRequests;
    }

    public IAuthenticationRequestAdmissionLease? TryAcquire(AuthenticationOperation operation)
    {
        var operationTag = AuthenticationRequestAdmissionMetrics.GetOperationTagValue(operation);
        while (true)
        {
            var current = Volatile.Read(ref _inFlight);
            if (current >= _maximumConcurrentRequests)
            {
                AuthenticationRequestAdmissionMetrics.RecordRejected(operationTag);
                return null;
            }

            if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) != current)
            {
                continue;
            }

            AuthenticationRequestAdmissionMetrics.RecordAdmitted(operationTag);
            return new AuthenticationRequestAdmissionLease(this, operationTag);
        }
    }

    private void Release(string operationTag)
    {
        Interlocked.Decrement(ref _inFlight);
        AuthenticationRequestAdmissionMetrics.RecordReleased(operationTag);
    }

    private sealed class AuthenticationRequestAdmissionLease(
        AuthenticationRequestAdmissionGate owner,
        string operationTag) : IAuthenticationRequestAdmissionLease
    {
        private AuthenticationRequestAdmissionGate? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(operationTag);
    }
}
