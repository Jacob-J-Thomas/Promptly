using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthenticationRequestAdmissionGateTests
{
    [Fact]
    public async Task ConcurrentAttemptsAdmitExactlyConfiguredCeilingWithoutQueueing()
    {
        const int MaximumConcurrentRequests = 7;
        const int AttemptCount = 128;
        var gate = CreateGate(MaximumConcurrentRequests);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, AttemptCount)
            .Select(index => Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                var operation = index % 2 == 0
                    ? AuthenticationOperation.Login
                    : AuthenticationOperation.Registration;
                return gate.TryAcquire(operation);
            }, TestContext.Current.CancellationToken))
            .ToArray();

        start.Set();
        var leases = await Task.WhenAll(attempts).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(MaximumConcurrentRequests, leases.Count(lease => lease is not null));
        Assert.Equal(
            AttemptCount - MaximumConcurrentRequests,
            leases.Count(lease => lease is null));
        foreach (var lease in leases)
        {
            lease?.Dispose();
        }
    }

    [Fact]
    public async Task LoginAndRegistrationShareOneProcessWideCeiling()
    {
        var gate = CreateGate(1);
        using var login = gate.TryAcquire(AuthenticationOperation.Login);
        Assert.NotNull(login);

        var blockedTask = Task.Run(
            () => gate.TryAcquire(AuthenticationOperation.Registration),
            TestContext.Current.CancellationToken);
        var registration = await blockedTask.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Null(registration);
        login.Dispose();
        using var recovered = gate.TryAcquire(AuthenticationOperation.Registration);
        Assert.NotNull(recovered);
    }

    [Fact]
    public void LeaseReleaseIsIdempotentAndCapacityRecoversExactlyOnce()
    {
        var gate = CreateGate(1);
        var first = gate.TryAcquire(AuthenticationOperation.Login);
        Assert.NotNull(first);

        first.Dispose();
        first.Dispose();

        using var second = gate.TryAcquire(AuthenticationOperation.Registration);
        Assert.NotNull(second);
        Assert.Null(gate.TryAcquire(AuthenticationOperation.Login));
    }

    [Fact]
    public void ConstructorAndOperationValidateInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthenticationRequestAdmissionGate(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateGate(257));

        var gate = CreateGate(1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            gate.TryAcquire((AuthenticationOperation)int.MaxValue));
    }

    [Fact]
    public void MetricsUseExactNamesAndOnlyBoundedOperationTags()
    {
        var published = new ConcurrentDictionary<string, Instrument>();
        var measurements = new ConcurrentQueue<MetricMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AuthenticationRequestAdmissionMetrics.MeterName)
                {
                    published[instrument.Name] = instrument;
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copiedTags = new KeyValuePair<string, object?>[tags.Length];
            tags.CopyTo(copiedTags);
            measurements.Enqueue(new MetricMeasurement(instrument.Name, value, copiedTags));
        });
        listener.Start();
        var gate = CreateGate(1);

        using (var admitted = gate.TryAcquire(AuthenticationOperation.Login))
        {
            Assert.NotNull(admitted);
            Assert.Null(gate.TryAcquire(AuthenticationOperation.Registration));
        }

        Assert.Equal(
            new[]
            {
                AuthenticationRequestAdmissionMetrics.AdmittedInstrumentName,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                AuthenticationRequestAdmissionMetrics.RejectedInstrumentName
            },
            published.Keys.Order(StringComparer.Ordinal));
        Assert.Collection(
            measurements,
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.AdmittedInstrumentName,
                1,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                1,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.RejectedInstrumentName,
                1,
                AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                -1,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue));
    }

    [Fact]
    public void ThrowingMetricListenerCannotLeakCapacityOrEscapeAdmissionOperations()
    {
        var callbackCount = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AuthenticationRequestAdmissionMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            throw new InvalidOperationException("Simulated metrics observer failure.");
        });
        listener.Start();
        var gate = CreateGate(1);

        var first = gate.TryAcquire(AuthenticationOperation.Login);
        Assert.NotNull(first);
        Assert.Null(gate.TryAcquire(AuthenticationOperation.Registration));
        first.Dispose();

        using var recovered = gate.TryAcquire(AuthenticationOperation.Registration);
        Assert.NotNull(recovered);

        Assert.Equal(6, Volatile.Read(ref callbackCount));
    }

    private static AuthenticationRequestAdmissionGate CreateGate(int maximumConcurrentRequests) =>
        new(Options.Create(new AuthenticationAbuseOptions
        {
            MaximumConcurrentAuthenticationRequests = maximumConcurrentRequests
        }));

    private static void AssertMeasurement(
        MetricMeasurement measurement,
        string instrumentName,
        long value,
        string operation)
    {
        Assert.Equal(instrumentName, measurement.InstrumentName);
        Assert.Equal(value, measurement.Value);
        var tag = Assert.Single(measurement.Tags);
        Assert.Equal(AuthenticationRequestAdmissionMetrics.OperationTagName, tag.Key);
        Assert.Equal(operation, tag.Value);
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
