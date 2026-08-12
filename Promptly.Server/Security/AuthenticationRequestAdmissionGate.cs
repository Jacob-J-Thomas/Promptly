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

    internal static string GetOperationTagValue(AuthenticationOperation operation) =>
        operation switch
        {
            AuthenticationOperation.Login => LoginOperationTagValue,
            AuthenticationOperation.Registration => RegistrationOperationTagValue,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.")
        };
}

public sealed class AuthenticationRequestAdmissionGate :
    IAuthenticationRequestAdmissionGate,
    IDisposable
{
    private const long LoginIncrement = 1;
    private const long RegistrationIncrement = 1L << 32;
    private const long OperationCountMask = uint.MaxValue;

    private readonly int _maximumConcurrentRequests;
    private readonly Meter _meter;
    private readonly Counter<long>? _admitted;
    private readonly Counter<long>? _rejected;
    private readonly ObservableGauge<long>? _inFlight;
    private long _inFlightByOperation;

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
        _meter = new Meter(AuthenticationRequestAdmissionMetrics.MeterName);
        _admitted = TryCreateInstrument(() => _meter.CreateCounter<long>(
            AuthenticationRequestAdmissionMetrics.AdmittedInstrumentName,
            unit: "{request}",
            description: "Authentication requests admitted by the process-wide concurrency gate."));
        _rejected = TryCreateInstrument(() => _meter.CreateCounter<long>(
            AuthenticationRequestAdmissionMetrics.RejectedInstrumentName,
            unit: "{request}",
            description: "Authentication requests rejected by the process-wide concurrency gate."));
        _inFlight = TryCreateInstrument(() => _meter.CreateObservableGauge(
            AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
            ObserveInFlight,
            unit: "{request}",
            description: "Authentication requests currently holding process-wide admission."));
    }

    public IAuthenticationRequestAdmissionLease? TryAcquire(AuthenticationOperation operation)
    {
        var operationTag = AuthenticationRequestAdmissionMetrics.GetOperationTagValue(operation);
        var operationIncrement = GetOperationIncrement(operation);
        while (true)
        {
            var current = Interlocked.Read(ref _inFlightByOperation);
            if (GetTotalInFlight(current) >= _maximumConcurrentRequests)
            {
                TryAdd(_rejected, operationTag);
                return null;
            }

            var updated = current + operationIncrement;
            if (Interlocked.CompareExchange(
                    ref _inFlightByOperation,
                    updated,
                    current) != current)
            {
                continue;
            }

            TryAdd(_admitted, operationTag);
            return new AuthenticationRequestAdmissionLease(
                this,
                operationIncrement);
        }
    }

    public void Dispose() => _meter.Dispose();

    private static long GetOperationIncrement(AuthenticationOperation operation) =>
        operation switch
        {
            AuthenticationOperation.Login => LoginIncrement,
            AuthenticationOperation.Registration => RegistrationIncrement,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.")
        };

    private static int GetLoginInFlight(long snapshot) =>
        checked((int)(snapshot & OperationCountMask));

    private static int GetRegistrationInFlight(long snapshot) =>
        checked((int)((snapshot >> 32) & OperationCountMask));

    private static int GetTotalInFlight(long snapshot) =>
        checked(GetLoginInFlight(snapshot) + GetRegistrationInFlight(snapshot));

    private Measurement<long>[] ObserveInFlight()
    {
        var snapshot = Interlocked.Read(ref _inFlightByOperation);
        return
        [
            new(
                GetLoginInFlight(snapshot),
                CreateOperationTag(
                    AuthenticationRequestAdmissionMetrics.LoginOperationTagValue)),
            new(
                GetRegistrationInFlight(snapshot),
                CreateOperationTag(
                    AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue))
        ];
    }

    private static TInstrument? TryCreateInstrument<TInstrument>(
        Func<TInstrument> createInstrument)
        where TInstrument : Instrument
    {
        try
        {
            return createInstrument();
        }
        catch (Exception) // lgtm[cs/catch-of-all-exceptions] Observer failures must not poison auth.
        {
            // Instrument publication invokes external listeners synchronously.
            // A broken observer must not poison authentication initialization.
            return null;
        }
    }

    private static void TryAdd(Counter<long>? instrument, string operationTag)
    {
        if (instrument is null)
        {
            return;
        }

        try
        {
            instrument.Add(1, CreateOperationTag(operationTag));
        }
        catch (Exception) // lgtm[cs/catch-of-all-exceptions] Observer failures must not break auth.
        {
            // Metrics observers are untrusted extensions. Telemetry must never
            // leak admission capacity or turn an otherwise valid request into a 500.
        }
    }

    private static KeyValuePair<string, object?> CreateOperationTag(string operationTag) =>
        new(AuthenticationRequestAdmissionMetrics.OperationTagName, operationTag);

    private void Release(long operationIncrement) =>
        Interlocked.Add(ref _inFlightByOperation, -operationIncrement);

    private sealed class AuthenticationRequestAdmissionLease(
        AuthenticationRequestAdmissionGate owner,
        long operationIncrement) : IAuthenticationRequestAdmissionLease
    {
        private AuthenticationRequestAdmissionGate? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(operationIncrement);
    }
}
