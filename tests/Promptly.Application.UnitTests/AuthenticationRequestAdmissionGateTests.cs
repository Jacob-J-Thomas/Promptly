using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

[Collection(AuthenticationRequestAdmissionMetricsCollection.Name)]
public sealed class AuthenticationRequestAdmissionGateTests
{
    [Fact]
    public async Task ConcurrentAttemptsAdmitExactlyConfiguredCeilingWithoutQueueing()
    {
        const int MaximumConcurrentRequests = 7;
        const int AttemptCount = 128;
        using var gate = CreateGate(MaximumConcurrentRequests);
        using var start = new ManualResetEventSlim();
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var admittedLeases = new ConcurrentAdmissionLeases();
        var attempts = Enumerable.Range(0, AttemptCount)
            .Select(index => Task.Run(() =>
            {
                start.Wait(attemptCancellation.Token);
                var operation = index % 2 == 0
                    ? AuthenticationOperation.Login
                    : AuthenticationOperation.Registration;
                var lease = gate.TryAcquire(operation);
                if (lease is not null)
                {
                    admittedLeases.Add(lease);
                }

                return lease is not null;
            }, attemptCancellation.Token))
            .ToArray();

        start.Set();
        try
        {
            var admitted = await Task.WhenAll(attempts).WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.Equal(MaximumConcurrentRequests, admitted.Count(isAdmitted => isAdmitted));
            Assert.Equal(
                AttemptCount - MaximumConcurrentRequests,
                admitted.Count(isAdmitted => !isAdmitted));
        }
        finally
        {
            attemptCancellation.Cancel();
            try
            {
                await Task.WhenAll(attempts);
            }
            catch (OperationCanceledException)
            {
                // Cancellation prevents not-yet-started attempts from acquiring.
            }
        }
    }

    [Fact]
    public async Task LoginAndRegistrationShareOneProcessWideCeiling()
    {
        using var gate = CreateGate(1);
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
        using var gate = CreateGate(1);
        using var first = gate.TryAcquire(AuthenticationOperation.Login);
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

        using var gate = CreateGate(1);
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
        using var gate = CreateGate(1);

        using (var admitted = gate.TryAcquire(AuthenticationOperation.Login))
        {
            Assert.NotNull(admitted);
            Assert.Null(gate.TryAcquire(AuthenticationOperation.Registration));
            listener.RecordObservableInstruments();
        }
        listener.RecordObservableInstruments();

        Assert.Equal(
            new[]
            {
                AuthenticationRequestAdmissionMetrics.AdmittedInstrumentName,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                AuthenticationRequestAdmissionMetrics.RejectedInstrumentName
            },
            published.Keys.Order(StringComparer.Ordinal));
        Assert.IsType<ObservableGauge<long>>(
            published[AuthenticationRequestAdmissionMetrics.InFlightInstrumentName]);
        Assert.Collection(
            measurements.Where(measurement =>
                measurement.InstrumentName !=
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.AdmittedInstrumentName,
                1,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.RejectedInstrumentName,
                1,
                AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue));
        Assert.Collection(
            measurements.Where(measurement =>
                measurement.InstrumentName ==
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                1,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                0,
                AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                0,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                0,
                AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue));
    }

    [Fact]
    public void InFlightGaugeReportsCurrentStateForLateAndRenewedSubscriptions()
    {
        using var gate = CreateGate(1);
        using var login = gate.TryAcquire(AuthenticationOperation.Login);
        Assert.NotNull(login);
        var measurements = new ConcurrentQueue<MetricMeasurement>();
        Instrument? inFlightInstrument = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AuthenticationRequestAdmissionMetrics.MeterName
                    && instrument.Name ==
                    AuthenticationRequestAdmissionMetrics.InFlightInstrumentName)
                {
                    inFlightInstrument = instrument;
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

        listener.RecordObservableInstruments();
        AssertGaugePair(measurements.ToArray(), login: 1, registration: 0);

        Assert.NotNull(inFlightInstrument);
        listener.DisableMeasurementEvents(inFlightInstrument);
        login.Dispose();
        using var registration = gate.TryAcquire(AuthenticationOperation.Registration);
        Assert.NotNull(registration);
        listener.EnableMeasurementEvents(inFlightInstrument);
        listener.RecordObservableInstruments();
        AssertGaugePair(measurements.ToArray()[^2..], login: 0, registration: 1);

        registration.Dispose();
        listener.RecordObservableInstruments();
        AssertGaugePair(measurements.ToArray()[^2..], login: 0, registration: 0);
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
        using var gate = CreateGate(1);

        using var first = gate.TryAcquire(AuthenticationOperation.Login);
        Assert.NotNull(first);
        Assert.Null(gate.TryAcquire(AuthenticationOperation.Registration));
        first.Dispose();

        using var recovered = gate.TryAcquire(AuthenticationOperation.Registration);
        Assert.NotNull(recovered);

        Assert.Equal(3, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void ThrowingInstrumentPublicationCannotPoisonAdmissionInitialization()
    {
        var publicationCalls = 0;
        using (var throwingListener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name ==
                    AuthenticationRequestAdmissionMetrics.MeterName)
                {
                    Interlocked.Increment(ref publicationCalls);
                    throw new InvalidOperationException(
                        "Simulated instrument publication failure.");
                }
            }
        })
        {
            throwingListener.Start();
            using var gate = CreateGate(1);
            using var login = gate.TryAcquire(AuthenticationOperation.Login);
            Assert.NotNull(login);
            Assert.Null(gate.TryAcquire(AuthenticationOperation.Registration));
            login.Dispose();
            using var recovered = gate.TryAcquire(AuthenticationOperation.Registration);
            Assert.NotNull(recovered);
        }

        Assert.True(Volatile.Read(ref publicationCalls) > 0);

        var published = new ConcurrentDictionary<string, Instrument>();
        using var recoveredListener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == AuthenticationRequestAdmissionMetrics.MeterName)
                {
                    published[instrument.Name] = instrument;
                }
            }
        };
        recoveredListener.Start();
        using var recoveredGate = CreateGate(1);

        Assert.Equal(
            new[]
            {
                AuthenticationRequestAdmissionMetrics.AdmittedInstrumentName,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                AuthenticationRequestAdmissionMetrics.RejectedInstrumentName
            },
            published.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InFlightGaugeSnapshotNeverExceedsCeilingDuringConcurrentHandoffs()
    {
        const int CollectionCount = 10_000;
        using var gate = CreateGate(1);
        var measurements = new List<MetricMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AuthenticationRequestAdmissionMetrics.MeterName
                    && instrument.Name ==
                    AuthenticationRequestAdmissionMetrics.InFlightInstrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copiedTags = new KeyValuePair<string, object?>[tags.Length];
            tags.CopyTo(copiedTags);
            measurements.Add(new MetricMeasurement(instrument.Name, value, copiedTags));
        });
        listener.Start();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var operations = new[]
        {
            AuthenticationOperation.Login,
            AuthenticationOperation.Registration
        };
        using var ready = new CountdownEvent(operations.Length);
        using var start = new ManualResetEventSlim();
        var acquisitionCounts = new int[operations.Length];
        var samplingAcquisitionCounts = new int[operations.Length];
        var firstAcquisitions = operations
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var samplingAcquisitions = operations
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var samplingStarted = 0;
        var contenders = operations.Select((operation, operationIndex) => Task.Run(() =>
        {
            ready.Signal();
            try
            {
                start.Wait(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellation.IsCancellationRequested)
            {
                var lease = gate.TryAcquire(operation);
                if (lease is null)
                {
                    Thread.Yield();
                    continue;
                }

                using (lease)
                {
                    Interlocked.Increment(ref acquisitionCounts[operationIndex]);
                    firstAcquisitions[operationIndex].TrySetResult();
                    if (Volatile.Read(ref samplingStarted) != 0)
                    {
                        Interlocked.Increment(
                            ref samplingAcquisitionCounts[operationIndex]);
                        samplingAcquisitions[operationIndex].TrySetResult();
                    }

                    Thread.SpinWait(20);
                }

                Thread.Yield();
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            Assert.True(ready.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            start.Set();
            await Task.WhenAll(firstAcquisitions.Select(acquisition => acquisition.Task))
                .WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

            Volatile.Write(ref samplingStarted, 1);
            using var samplingTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            samplingTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var collectionIndex = 0;
            while (collectionIndex < CollectionCount
                   || samplingAcquisitions.Any(acquisition => !acquisition.Task.IsCompleted))
            {
                samplingTimeout.Token.ThrowIfCancellationRequested();
                measurements.Clear();
                listener.RecordObservableInstruments();

                Assert.Equal(2, measurements.Count);
                var login = GetGaugeValue(
                    measurements,
                    AuthenticationRequestAdmissionMetrics.LoginOperationTagValue);
                var registration = GetGaugeValue(
                    measurements,
                    AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue);
                Assert.InRange(login, 0, 1);
                Assert.InRange(registration, 0, 1);
                Assert.InRange(login + registration, 0, 1);

                collectionIndex++;
                if (collectionIndex % 100 == 0)
                {
                    await Task.Yield();
                }
            }

            Assert.All(acquisitionCounts, count => Assert.True(count > 0));
            Assert.All(samplingAcquisitionCounts, count => Assert.True(count > 0));
        }
        finally
        {
            Volatile.Write(ref samplingStarted, 0);
            cancellation.Cancel();
            start.Set();
            await Task.WhenAll(contenders);
        }
    }

    private static AuthenticationRequestAdmissionGate CreateGate(int maximumConcurrentRequests) =>
        new(Options.Create(new AuthenticationAbuseOptions
        {
            MaximumConcurrentAuthenticationRequests = maximumConcurrentRequests
        }));

    private sealed class ConcurrentAdmissionLeases : IDisposable
    {
        private readonly ConcurrentBag<IAuthenticationRequestAdmissionLease> _leases = [];

        public void Add(IAuthenticationRequestAdmissionLease lease) => _leases.Add(lease);

        public void Dispose()
        {
            while (_leases.TryTake(out var lease))
            {
                lease.Dispose();
            }
        }
    }

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

    private static void AssertGaugePair(
        IReadOnlyList<MetricMeasurement> measurements,
        long login,
        long registration)
    {
        Assert.Collection(
            measurements,
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                login,
                AuthenticationRequestAdmissionMetrics.LoginOperationTagValue),
            measurement => AssertMeasurement(
                measurement,
                AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
                registration,
                AuthenticationRequestAdmissionMetrics.RegistrationOperationTagValue));
    }

    private static long GetGaugeValue(
        IEnumerable<MetricMeasurement> measurements,
        string operation)
    {
        var measurement = Assert.Single(measurements, candidate =>
            Equals(Assert.Single(candidate.Tags).Value, operation));
        Assert.Equal(
            AuthenticationRequestAdmissionMetrics.InFlightInstrumentName,
            measurement.InstrumentName);
        return measurement.Value;
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthenticationRequestAdmissionMetricsCollection
{
    public const string Name = "Authentication request admission metrics";
}
