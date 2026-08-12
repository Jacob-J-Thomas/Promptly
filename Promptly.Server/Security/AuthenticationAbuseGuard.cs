namespace Promptly.Server.Security;

public enum AuthenticationOperation
{
    Login,
    Registration
}

public readonly record struct AuthenticationThrottleDecision
{
    private AuthenticationThrottleDecision(bool isAllowed, int retryAfterSeconds)
    {
        IsAllowed = isAllowed;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public bool IsAllowed { get; }

    public int RetryAfterSeconds { get; }

    public static AuthenticationThrottleDecision Allowed { get; } = new(true, 0);

    public static AuthenticationThrottleDecision RejectAfter(int retryAfterSeconds)
    {
        if (retryAfterSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfterSeconds),
                retryAfterSeconds,
                "Retry-After must be positive.");
        }

        return new AuthenticationThrottleDecision(false, retryAfterSeconds);
    }
}

public interface IAuthenticationAbuseGuard
{
    AuthenticationThrottleDecision TryAcquireClient(
        AuthenticationOperation operation,
        HttpContext httpContext);

    ValueTask<AuthenticationAccountAttempt> BeginAccountAttemptAsync(
        AuthenticationOperation operation,
        HttpContext httpContext,
        string normalizedAccount,
        CancellationToken cancellationToken);

    AuthenticationThrottleDecision RecordLoginFailure(AuthenticationAccountAttempt attempt);
}

public sealed class AuthenticationAbuseGuard : IAuthenticationAbuseGuard, IDisposable
{
    private const int ExpiredStateScanLimit = 64;
    private const int TrackedPartitionCohortCount = 4;
    private readonly object _stateGate = new();
    private readonly IAuthenticationPartitionKeyProvider _partitionKeys;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumRetryAfterSeconds;
    private readonly Dictionary<FixedWindowPartition, FixedWindowState> _fixedWindows = [];
    private readonly Dictionary<string, PasswordSprayState> _passwordSprayByClient =
        new(StringComparer.Ordinal);
    private readonly SortedSet<TrackedPartitionExpiry> _sprayEntryExpiryIndex = [];
    private readonly TrackedCohortState[] _trackedCohorts;
    private long _nextTrackedPartitionId;
    private readonly SemaphoreSlim[] _accountStripes;
    private readonly RateLimit _loginClientLimit;
    private readonly RateLimit _registrationClientLimit;
    private readonly RateLimit _loginAccountLimit;
    private readonly RateLimit _registrationAccountLimit;
    private readonly int _passwordSprayDistinctAccountLimit;
    private readonly int _maximumTrackedSprayAccountEntries;
    private readonly long _passwordSprayWindowTicks;
    private readonly long _passwordSprayBlockTicks;
    private int _trackedSprayAccountEntries;
    private int _disposed;

    public AuthenticationAbuseGuard(
        IAuthenticationPartitionKeyProvider partitionKeys,
        Microsoft.Extensions.Options.IOptions<AuthenticationAbuseOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(partitionKeys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var value = options.Value;
        _partitionKeys = partitionKeys;
        _timeProvider = timeProvider;
        var maximumTrackedPartitions = RequireAtLeast(
            value.MaximumTrackedPartitions,
            TrackedPartitionCohortCount,
            nameof(value.MaximumTrackedPartitions));
        _trackedCohorts = CreateTrackedCohorts(maximumTrackedPartitions);
        _maximumRetryAfterSeconds = RequirePositive(
            value.MaximumRetryAfterSeconds,
            nameof(value.MaximumRetryAfterSeconds));
        _loginClientLimit = new RateLimit(
            RequirePositive(value.LoginIpPermitLimit, nameof(value.LoginIpPermitLimit)),
            SecondsToTimestampTicks(
                value.LoginIpWindowSeconds,
                nameof(value.LoginIpWindowSeconds)));
        _registrationClientLimit = new RateLimit(
            RequirePositive(
                value.RegistrationIpPermitLimit,
                nameof(value.RegistrationIpPermitLimit)),
            SecondsToTimestampTicks(
                value.RegistrationIpWindowSeconds,
                nameof(value.RegistrationIpWindowSeconds)));
        _loginAccountLimit = new RateLimit(
            RequirePositive(
                value.LoginAccountPermitLimit,
                nameof(value.LoginAccountPermitLimit)),
            SecondsToTimestampTicks(
                value.LoginAccountWindowSeconds,
                nameof(value.LoginAccountWindowSeconds)));
        _registrationAccountLimit = new RateLimit(
            RequirePositive(
                value.RegistrationAccountPermitLimit,
                nameof(value.RegistrationAccountPermitLimit)),
            SecondsToTimestampTicks(
                value.RegistrationAccountWindowSeconds,
                nameof(value.RegistrationAccountWindowSeconds)));
        _passwordSprayDistinctAccountLimit = RequirePositive(
            value.PasswordSprayDistinctAccountLimit,
            nameof(value.PasswordSprayDistinctAccountLimit));
        _maximumTrackedSprayAccountEntries = RequirePositive(
            value.MaximumTrackedSprayAccountEntries,
            nameof(value.MaximumTrackedSprayAccountEntries));
        _passwordSprayWindowTicks = SecondsToTimestampTicks(
            value.PasswordSprayWindowSeconds,
            nameof(value.PasswordSprayWindowSeconds));
        _passwordSprayBlockTicks = SecondsToTimestampTicks(
            value.PasswordSprayBlockSeconds,
            nameof(value.PasswordSprayBlockSeconds));

        var stripeCount = RequirePositive(
            value.AccountLockStripeCount,
            nameof(value.AccountLockStripeCount));
        _accountStripes = Enumerable.Range(0, stripeCount)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public AuthenticationThrottleDecision TryAcquireClient(
        AuthenticationOperation operation,
        HttpContext httpContext)
    {
        ThrowIfDisposed();
        ValidateOperation(operation);
        ArgumentNullException.ThrowIfNull(httpContext);

        var clientKey = _partitionKeys.GetClientKey(httpContext);
        var limit = operation == AuthenticationOperation.Login
            ? _loginClientLimit
            : _registrationClientLimit;

        lock (_stateGate)
        {
            ThrowIfDisposed();
            return TryAcquireFixedWindowLocked(
                new FixedWindowPartition(PartitionKind.Client, operation, clientKey),
                limit,
                GetNowTicks());
        }
    }

    public ValueTask<AuthenticationAccountAttempt> BeginAccountAttemptAsync(
        AuthenticationOperation operation,
        HttpContext httpContext,
        string normalizedAccount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateOperation(operation);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(normalizedAccount);

        var clientKey = _partitionKeys.GetClientKey(httpContext);
        var accountKey = _partitionKeys.GetAccountKey(normalizedAccount);
        var stripe = _accountStripes[GetStripeIndex(accountKey, _accountStripes.Length)];
        var limit = operation == AuthenticationOperation.Login
            ? _loginAccountLimit
            : _registrationAccountLimit;
        if (operation == AuthenticationOperation.Login)
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                var sprayDecision = GetActivePasswordSprayBlockLocked(
                    clientKey,
                    GetNowTicks());
                if (!sprayDecision.IsAllowed)
                {
                    return ValueTask.FromResult(
                        AuthenticationAccountAttempt.Rejected(this, operation, sprayDecision));
                }
            }
        }

        if (!stripe.Wait(0, cancellationToken))
        {
            return ValueTask.FromResult(AuthenticationAccountAttempt.Rejected(
                this,
                operation,
                AuthenticationThrottleDecision.RejectAfter(1)));
        }

        try
        {
            ThrowIfDisposed();
            AuthenticationThrottleDecision decision;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                var nowTicks = GetNowTicks();
                decision = operation == AuthenticationOperation.Login
                    ? GetActivePasswordSprayBlockLocked(clientKey, nowTicks)
                    : AuthenticationThrottleDecision.Allowed;
                if (decision.IsAllowed)
                {
                    decision = TryAcquireFixedWindowLocked(
                        new FixedWindowPartition(PartitionKind.Account, operation, accountKey),
                        limit,
                        nowTicks);
                }
            }

            if (!decision.IsAllowed)
            {
                stripe.Release();
                return ValueTask.FromResult(
                    AuthenticationAccountAttempt.Rejected(this, operation, decision));
            }

            return ValueTask.FromResult(AuthenticationAccountAttempt.Acquired(
                this,
                operation,
                clientKey,
                accountKey,
                stripe));
        }
        catch
        {
            stripe.Release();
            throw;
        }
    }

    public AuthenticationThrottleDecision RecordLoginFailure(AuthenticationAccountAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ThrowIfDisposed();
        return attempt.RecordLoginFailure(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_stateGate)
        {
            _fixedWindows.Clear();
            _passwordSprayByClient.Clear();
            _sprayEntryExpiryIndex.Clear();
            _trackedSprayAccountEntries = 0;
            foreach (var cohort in _trackedCohorts)
            {
                cohort.EvictionOrder.Clear();
                cohort.ExpiryIndex.Clear();
                cohort.EvictionCursor = null;
            }
        }

        // SemaphoreSlim has no unmanaged state until its WaitHandle is requested,
        // which this implementation never does. Leaving the fixed stripe array
        // undisposed also lets an already-issued attempt release its stripe safely
        // while singleton teardown races request completion.
    }

    internal AuthenticationThrottleDecision RecordLoginFailureCore(
        string clientKey,
        string accountKey)
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            ThrowIfDisposed();
            var nowTicks = GetNowTicks();
            if (_passwordSprayByClient.TryGetValue(clientKey, out var state))
            {
                if (state.BlockedUntilTicks is { } blockedUntilTicks)
                {
                    if (nowTicks < blockedUntilTicks)
                    {
                        return RejectUntil(nowTicks, blockedUntilTicks);
                    }

                    RemovePasswordSprayStateLocked(clientKey, state);
                    state = null;
                }
                else if (nowTicks >= state.WindowExpiresTicks)
                {
                    RemovePasswordSprayStateLocked(clientKey, state);
                    state = null;
                }
            }

            if (state is null)
            {
                if (!TryReserveSprayAccountEntryLocked(nowTicks, out var entryCapacityDecision))
                {
                    return entryCapacityDecision;
                }

                if (!TryReserveTrackedPartitionLocked(
                        TrackedPartitionCohort.LoginAccountAndSpray,
                        nowTicks,
                        out var capacityDecision))
                {
                    return capacityDecision;
                }

                var expiresTicks = checked(nowTicks + _passwordSprayWindowTicks);
                state = new PasswordSprayState(
                    expiresTicks,
                    RegisterTrackedPartitionLocked(
                        TrackedPartitionCohort.LoginAccountAndSpray,
                        TrackedPartitionReference.ForPasswordSpray(clientKey),
                        expiresTicks));
                _passwordSprayByClient.Add(clientKey, state);
            }

            if (state.AccountKeys.Contains(accountKey))
            {
                return AuthenticationThrottleDecision.Allowed;
            }

            if (!TryReserveSprayAccountEntryLocked(nowTicks, out var sprayCapacityDecision))
            {
                return sprayCapacityDecision;
            }

            var wasEmpty = state.AccountKeys.Count == 0;
            state.AccountKeys.Add(accountKey);
            _trackedSprayAccountEntries++;
            if (wasEmpty)
            {
                _sprayEntryExpiryIndex.Add(state.Registration.Expiry);
            }

            if (state.AccountKeys.Count < _passwordSprayDistinctAccountLimit)
            {
                return AuthenticationThrottleDecision.Allowed;
            }

            ClearSprayAccountKeysLocked(state);
            state.BlockedUntilTicks = checked(nowTicks + _passwordSprayBlockTicks);
            UpdateTrackedPartitionExpiryLocked(state.Registration, state.BlockedUntilTicks.Value);
            return RejectUntil(nowTicks, state.BlockedUntilTicks.Value);
        }
    }

    private AuthenticationThrottleDecision TryAcquireFixedWindowLocked(
        FixedWindowPartition partition,
        RateLimit limit,
        long nowTicks)
    {
        if (_fixedWindows.TryGetValue(partition, out var state)
            && nowTicks >= state.WindowExpiresTicks)
        {
            RemoveFixedWindowStateLocked(partition, state);
            state = null;
        }

        if (state is null)
        {
            var cohort = GetTrackedPartitionCohort(partition);
            if (!TryReserveTrackedPartitionLocked(cohort, nowTicks, out var capacityDecision))
            {
                return capacityDecision;
            }

            var expiresTicks = checked(nowTicks + limit.WindowTicks);
            _fixedWindows.Add(
                partition,
                new FixedWindowState(
                    1,
                    expiresTicks,
                    RegisterTrackedPartitionLocked(
                        cohort,
                        TrackedPartitionReference.ForFixedWindow(partition),
                        expiresTicks)));
            return AuthenticationThrottleDecision.Allowed;
        }

        if (state.PermitCount >= limit.PermitLimit)
        {
            return RejectUntil(nowTicks, state.WindowExpiresTicks);
        }

        state.PermitCount++;
        return AuthenticationThrottleDecision.Allowed;
    }

    private AuthenticationThrottleDecision GetActivePasswordSprayBlockLocked(
        string clientKey,
        long nowTicks)
    {
        if (!_passwordSprayByClient.TryGetValue(clientKey, out var state)
            || state.BlockedUntilTicks is not { } blockedUntilTicks)
        {
            return AuthenticationThrottleDecision.Allowed;
        }

        if (nowTicks < blockedUntilTicks)
        {
            return RejectUntil(nowTicks, blockedUntilTicks);
        }

        RemovePasswordSprayStateLocked(clientKey, state);
        return AuthenticationThrottleDecision.Allowed;
    }

    private bool TryReserveTrackedPartitionLocked(
        TrackedPartitionCohort cohort,
        long nowTicks,
        out AuthenticationThrottleDecision rejection)
    {
        var cohortState = _trackedCohorts[(int)cohort];
        if (cohortState.EvictionOrder.Count < cohortState.Capacity)
        {
            rejection = default;
            return true;
        }

        EvictExpiredStateLocked(cohortState, nowTicks);
        if (cohortState.EvictionOrder.Count < cohortState.Capacity)
        {
            rejection = default;
            return true;
        }

        rejection = RejectUntil(nowTicks, cohortState.ExpiryIndex.Min.ExpiresTicks);
        return false;
    }

    private bool TryReserveSprayAccountEntryLocked(
        long nowTicks,
        out AuthenticationThrottleDecision rejection)
    {
        if (_trackedSprayAccountEntries < _maximumTrackedSprayAccountEntries)
        {
            rejection = default;
            return true;
        }

        EvictExpiredStateLocked(
            _trackedCohorts[(int)TrackedPartitionCohort.LoginAccountAndSpray],
            nowTicks);
        if (_trackedSprayAccountEntries < _maximumTrackedSprayAccountEntries)
        {
            rejection = default;
            return true;
        }

        rejection = RejectUntil(nowTicks, _sprayEntryExpiryIndex.Min.ExpiresTicks);
        return false;
    }

    private void EvictExpiredStateLocked(TrackedCohortState cohort, long nowTicks)
    {
        var scanCount = Math.Min(ExpiredStateScanLimit, cohort.EvictionOrder.Count);
        for (var index = 0; index < scanCount; index++)
        {
            var node = cohort.EvictionCursor ?? cohort.EvictionOrder.First!;
            var reference = node.Value;
            if (reference.Kind == TrackedPartitionKind.FixedWindow)
            {
                var partition = reference.FixedWindow;
                if (_fixedWindows.TryGetValue(partition, out var state)
                    && nowTicks >= state.WindowExpiresTicks)
                {
                    RemoveFixedWindowStateLocked(partition, state);
                    continue;
                }
            }
            else if (_passwordSprayByClient.TryGetValue(
                         reference.PasswordSprayClientKey!,
                         out var state)
                     && nowTicks >= state.ExpiresTicks)
            {
                RemovePasswordSprayStateLocked(reference.PasswordSprayClientKey!, state);
                continue;
            }

            cohort.EvictionCursor = node.Next ?? cohort.EvictionOrder.First;
        }
    }

    private TrackedPartitionRegistration RegisterTrackedPartitionLocked(
        TrackedPartitionCohort cohort,
        TrackedPartitionReference reference,
        long expiresTicks)
    {
        var cohortState = _trackedCohorts[(int)cohort];
        var node = cohortState.EvictionOrder.AddLast(reference);
        cohortState.EvictionCursor ??= node;
        var expiry = new TrackedPartitionExpiry(
            expiresTicks,
            checked(++_nextTrackedPartitionId));
        cohortState.ExpiryIndex.Add(expiry);
        return new TrackedPartitionRegistration(cohort, node, expiry);
    }

    private void UpdateTrackedPartitionExpiryLocked(
        TrackedPartitionRegistration registration,
        long expiresTicks)
    {
        var cohort = _trackedCohorts[(int)registration.Cohort];
        cohort.ExpiryIndex.Remove(registration.Expiry);
        registration.Expiry = new TrackedPartitionExpiry(
            expiresTicks,
            registration.Expiry.Id);
        cohort.ExpiryIndex.Add(registration.Expiry);
    }

    private void RemoveFixedWindowStateLocked(
        FixedWindowPartition partition,
        FixedWindowState state)
    {
        _fixedWindows.Remove(partition);
        UnregisterTrackedPartitionLocked(state.Registration);
    }

    private void RemovePasswordSprayStateLocked(string clientKey, PasswordSprayState state)
    {
        _passwordSprayByClient.Remove(clientKey);
        ClearSprayAccountKeysLocked(state);
        UnregisterTrackedPartitionLocked(state.Registration);
    }

    private void ClearSprayAccountKeysLocked(PasswordSprayState state)
    {
        if (state.AccountKeys.Count == 0)
        {
            return;
        }

        _sprayEntryExpiryIndex.Remove(state.Registration.Expiry);
        _trackedSprayAccountEntries -= state.AccountKeys.Count;
        state.AccountKeys.Clear();
    }

    private void UnregisterTrackedPartitionLocked(TrackedPartitionRegistration registration)
    {
        var cohort = _trackedCohorts[(int)registration.Cohort];
        var node = registration.EvictionNode;
        if (ReferenceEquals(cohort.EvictionCursor, node))
        {
            cohort.EvictionCursor = node.Next ?? cohort.EvictionOrder.First;
            if (ReferenceEquals(cohort.EvictionCursor, node))
            {
                cohort.EvictionCursor = null;
            }
        }

        cohort.EvictionOrder.Remove(node);
        cohort.ExpiryIndex.Remove(registration.Expiry);
    }

    private AuthenticationThrottleDecision RejectUntil(long nowTicks, long retryAtTicks)
    {
        var remainingTicks = Math.Max(1, retryAtTicks - nowTicks);
        var frequency = _timeProvider.TimestampFrequency;
        var seconds = (remainingTicks + frequency - 1) / frequency;
        return AuthenticationThrottleDecision.RejectAfter(
            (int)Math.Min(seconds, _maximumRetryAfterSeconds));
    }

    private long GetNowTicks() => _timeProvider.GetTimestamp();

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static int GetStripeIndex(string accountKey, int stripeCount)
    {
        var hash = StringComparer.Ordinal.GetHashCode(accountKey);
        return (int)((uint)hash % (uint)stripeCount);
    }

    private static int RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive.");
        }

        return value;
    }

    private static int RequireAtLeast(int value, int minimum, string name)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"The value must be at least {minimum}.");
        }

        return value;
    }

    private long SecondsToTimestampTicks(int seconds, string name) =>
        checked((long)RequirePositive(seconds, name) * _timeProvider.TimestampFrequency);

    private static void ValidateOperation(AuthenticationOperation operation)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.");
        }
    }

    private static TrackedCohortState[] CreateTrackedCohorts(int maximumTrackedPartitions)
    {
        var baseCapacity = maximumTrackedPartitions / TrackedPartitionCohortCount;
        var remainder = maximumTrackedPartitions % TrackedPartitionCohortCount;
        return Enumerable.Range(0, TrackedPartitionCohortCount)
            .Select(index => new TrackedCohortState(baseCapacity + (index < remainder ? 1 : 0)))
            .ToArray();
    }

    private static TrackedPartitionCohort GetTrackedPartitionCohort(
        FixedWindowPartition partition) =>
        (partition.Kind, partition.Operation) switch
        {
            (PartitionKind.Client, AuthenticationOperation.Login) =>
                TrackedPartitionCohort.LoginClient,
            (PartitionKind.Client, AuthenticationOperation.Registration) =>
                TrackedPartitionCohort.RegistrationClient,
            (PartitionKind.Account, AuthenticationOperation.Login) =>
                TrackedPartitionCohort.LoginAccountAndSpray,
            (PartitionKind.Account, AuthenticationOperation.Registration) =>
                TrackedPartitionCohort.RegistrationAccount,
            _ => throw new ArgumentOutOfRangeException(nameof(partition))
        };

    private enum PartitionKind
    {
        Client,
        Account
    }

    private readonly record struct FixedWindowPartition(
        PartitionKind Kind,
        AuthenticationOperation Operation,
        string HashedKey);

    private sealed class FixedWindowState(
        int permitCount,
        long windowExpiresTicks,
        TrackedPartitionRegistration registration)
    {
        public int PermitCount { get; set; } = permitCount;

        public long WindowExpiresTicks { get; } = windowExpiresTicks;

        public TrackedPartitionRegistration Registration { get; } = registration;
    }

    private sealed class PasswordSprayState(
        long windowExpiresTicks,
        TrackedPartitionRegistration registration)
    {
        public HashSet<string> AccountKeys { get; } = new(StringComparer.Ordinal);

        public long WindowExpiresTicks { get; } = windowExpiresTicks;

        public long? BlockedUntilTicks { get; set; }

        public long ExpiresTicks => BlockedUntilTicks ?? WindowExpiresTicks;

        public TrackedPartitionRegistration Registration { get; } = registration;
    }

    private enum TrackedPartitionKind
    {
        FixedWindow,
        PasswordSpray
    }

    private enum TrackedPartitionCohort
    {
        LoginClient,
        RegistrationClient,
        LoginAccountAndSpray,
        RegistrationAccount
    }

    private readonly record struct TrackedPartitionReference(
        TrackedPartitionKind Kind,
        FixedWindowPartition FixedWindow,
        string? PasswordSprayClientKey)
    {
        public static TrackedPartitionReference ForFixedWindow(FixedWindowPartition partition) =>
            new(TrackedPartitionKind.FixedWindow, partition, null);

        public static TrackedPartitionReference ForPasswordSpray(string clientKey) =>
            new(TrackedPartitionKind.PasswordSpray, default, clientKey);
    }

    private sealed class TrackedPartitionRegistration(
        TrackedPartitionCohort cohort,
        LinkedListNode<TrackedPartitionReference> evictionNode,
        TrackedPartitionExpiry expiry)
    {
        public TrackedPartitionCohort Cohort { get; } = cohort;

        public LinkedListNode<TrackedPartitionReference> EvictionNode { get; } = evictionNode;

        public TrackedPartitionExpiry Expiry { get; set; } = expiry;
    }

    private sealed class TrackedCohortState(int capacity)
    {
        public int Capacity { get; } = capacity;

        public LinkedList<TrackedPartitionReference> EvictionOrder { get; } = [];

        public SortedSet<TrackedPartitionExpiry> ExpiryIndex { get; } = [];

        public LinkedListNode<TrackedPartitionReference>? EvictionCursor { get; set; }
    }

    private readonly record struct TrackedPartitionExpiry(long ExpiresTicks, long Id) :
        IComparable<TrackedPartitionExpiry>
    {
        public int CompareTo(TrackedPartitionExpiry other)
        {
            var expiryComparison = ExpiresTicks.CompareTo(other.ExpiresTicks);
            return expiryComparison != 0 ? expiryComparison : Id.CompareTo(other.Id);
        }
    }

    private readonly record struct RateLimit(int PermitLimit, long WindowTicks);
}

public sealed class AuthenticationAccountAttempt : IDisposable, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly AuthenticationAbuseGuard _owner;
    private readonly SemaphoreSlim? _stripe;
    private readonly string? _clientKey;
    private readonly string? _accountKey;
    private bool _disposed;
    private bool _failureRecorded;

    private AuthenticationAccountAttempt(
        AuthenticationAbuseGuard owner,
        AuthenticationOperation operation,
        AuthenticationThrottleDecision decision,
        string? clientKey,
        string? accountKey,
        SemaphoreSlim? stripe)
    {
        _owner = owner;
        Operation = operation;
        Decision = decision;
        _clientKey = clientKey;
        _accountKey = accountKey;
        _stripe = stripe;
    }

    public AuthenticationOperation Operation { get; }

    public AuthenticationThrottleDecision Decision { get; }

    public bool IsAllowed => Decision.IsAllowed;

    public void Dispose()
    {
        SemaphoreSlim? stripe;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stripe = _stripe;
        }

        stripe?.Release();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static AuthenticationAccountAttempt Acquired(
        AuthenticationAbuseGuard owner,
        AuthenticationOperation operation,
        string clientKey,
        string accountKey,
        SemaphoreSlim stripe) =>
        new(
            owner,
            operation,
            AuthenticationThrottleDecision.Allowed,
            clientKey,
            accountKey,
            stripe);

    internal static AuthenticationAccountAttempt Rejected(
        AuthenticationAbuseGuard owner,
        AuthenticationOperation operation,
        AuthenticationThrottleDecision decision) =>
        new(owner, operation, decision, null, null, null);

    internal AuthenticationThrottleDecision RecordLoginFailure(AuthenticationAbuseGuard caller)
    {
        lock (_lifecycleGate)
        {
            ValidateLoginFailure(caller);
            _failureRecorded = true;
            return caller.RecordLoginFailureCore(_clientKey!, _accountKey!);
        }
    }

    private void ValidateLoginFailure(AuthenticationAbuseGuard caller)
    {
        if (!ReferenceEquals(_owner, caller))
        {
            throw new InvalidOperationException("The attempt belongs to a different guard.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAllowed)
        {
            throw new InvalidOperationException("A rejected attempt cannot record a failure.");
        }

        if (Operation != AuthenticationOperation.Login)
        {
            throw new InvalidOperationException("Only login attempts can record login failures.");
        }

        if (_failureRecorded)
        {
            throw new InvalidOperationException("The login failure was already recorded.");
        }
    }
}
