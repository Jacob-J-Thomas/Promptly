using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthenticationAbuseGuardTests
{
    [Fact]
    public void ClientGateIsAtomicUnderConcurrency()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.LoginIpPermitLimit = 37;
            options.LoginIpWindowSeconds = 17;
        });

        var decisions = new AuthenticationThrottleDecision[400];
        Parallel.For(0, decisions.Length, index =>
            decisions[index] = harness.Guard.TryAcquireClient(
                AuthenticationOperation.Login,
                harness.Context("client-a")));

        Assert.Equal(37, decisions.Count(decision => decision.IsAllowed));
        Assert.Equal(363, decisions.Count(decision => !decision.IsAllowed));
        Assert.All(
            decisions.Where(decision => !decision.IsAllowed),
            decision => Assert.Equal(17, decision.RetryAfterSeconds));
    }

    [Fact]
    public void Ipv6SixtyFourPrefixSharesClientWindowAndDifferentPrefixIsIsolated()
    {
        var provider = new AuthenticationPartitionKeyProvider(
            Options.Create(new AuthenticationAbuseOptions()));
        using var harness = GuardHarness.Create(
            options => options.LoginIpPermitLimit = 1,
            provider);

        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:20::1")).IsAllowed);
        Assert.False(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:20:ffff::2")).IsAllowed);
        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:21::1")).IsAllowed);
    }

    [Fact]
    public void FixedWindowResetsAtExactBoundaryAndCeilsRetryAfter()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.LoginIpPermitLimit = 1;
            options.LoginIpWindowSeconds = 2;
        });
        var context = harness.Context("client-a");

        Assert.True(harness.Guard.TryAcquireClient(AuthenticationOperation.Login, context).IsAllowed);
        harness.Time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1));
        Assert.Equal(
            1,
            harness.Guard.TryAcquireClient(AuthenticationOperation.Login, context).RetryAfterSeconds);
        harness.Time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        Assert.True(harness.Guard.TryAcquireClient(AuthenticationOperation.Login, context).IsAllowed);
    }

    [Fact]
    public void RetryAfterIsClampedToConfiguredMaximum()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.LoginIpPermitLimit = 1;
            options.LoginIpWindowSeconds = 100;
            options.MaximumRetryAfterSeconds = 7;
        });
        var context = harness.Context("client-a");

        Assert.True(harness.Guard.TryAcquireClient(AuthenticationOperation.Login, context).IsAllowed);
        Assert.Equal(
            7,
            harness.Guard.TryAcquireClient(AuthenticationOperation.Login, context).RetryAfterSeconds);
    }

    [Fact]
    public async Task ClientAccountAndOperationPartitionsAreIndependent()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.LoginIpPermitLimit = 1;
            options.RegistrationIpPermitLimit = 1;
            options.LoginAccountPermitLimit = 1;
            options.RegistrationAccountPermitLimit = 1;
        });

        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-a")).IsAllowed);
        Assert.False(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-a")).IsAllowed);
        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-b")).IsAllowed);
        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Registration,
            harness.Context("client-a")).IsAllowed);

        using var loginA = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "account-a",
            TestContext.Current.CancellationToken);
        Assert.True(loginA.IsAllowed);
        loginA.Dispose();

        using var secondLoginA = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-b"),
            "account-a",
            TestContext.Current.CancellationToken);
        Assert.False(secondLoginA.IsAllowed);

        using var loginB = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "account-b",
            TestContext.Current.CancellationToken);
        Assert.True(loginB.IsAllowed);
        loginB.Dispose();

        using var registrationA = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Registration,
            harness.Context("client-a"),
            "account-a",
            TestContext.Current.CancellationToken);
        Assert.True(registrationA.IsAllowed);
    }

    [Fact]
    public async Task SameAccountContentionRejectsImmediatelyAndRecoversAfterDispose()
    {
        using var harness = GuardHarness.Create(options => options.LoginAccountPermitLimit = 2);
        var first = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "same-account",
            TestContext.Current.CancellationToken);
        using var second = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-b"),
            "same-account",
            TestContext.Current.CancellationToken);
        Assert.False(second.IsAllowed);
        Assert.Equal(1, second.Decision.RetryAfterSeconds);

        first.Dispose();
        using var recovered = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-c"),
            "same-account",
            TestContext.Current.CancellationToken);
        Assert.True(recovered.IsAllowed);
    }

    [Fact]
    public async Task CanceledImmediateAttemptThrowsWithoutReleasingHolder()
    {
        using var harness = GuardHarness.Create();
        await using var first = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "same-account",
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Guard.BeginAccountAttemptAsync(
                AuthenticationOperation.Login,
                harness.Context("client-b"),
                "same-account",
                cancellation.Token).AsTask());
        using var third = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-c"),
            "same-account",
            TestContext.Current.CancellationToken);
        Assert.False(third.IsAllowed);
        first.Dispose();
    }

    [Fact]
    public async Task SameStripeDifferentAccountsHaveZeroQueuedWaitersUnderConcurrency()
    {
        using var harness = GuardHarness.Create(
            options =>
            {
                options.AccountLockStripeCount = 1;
                options.LoginAccountPermitLimit = 1;
            },
            new TestPartitionKeyProvider());
        using var holder = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("holder"),
            "holder-account",
            TestContext.Current.CancellationToken);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 250)
            .Select(index => harness.Guard.BeginAccountAttemptAsync(
                AuthenticationOperation.Login,
                harness.Context($"client-{index}"),
                $"account-{index}",
                TestContext.Current.CancellationToken).AsTask()));

        Assert.All(attempts, attempt =>
        {
            Assert.False(attempt.IsAllowed);
            attempt.Dispose();
        });
        holder.Dispose();
        using var recovered = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("recovered"),
            "new-account",
            TestContext.Current.CancellationToken);
        Assert.True(recovered.IsAllowed);
    }

    [Fact]
    public async Task ArbitraryShortProviderKeysAreValidForStripeSelection()
    {
        using var harness = GuardHarness.Create(
            keyProvider: new TestPartitionKeyProvider(
                _ => "c",
                _ => "x"));

        using var attempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("ignored"),
            "ignored",
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsAllowed);
    }

    [Fact]
    public async Task HeldAccountStripeRejectsBarrierStartedContendersWithoutConsumingQuota()
    {
        const int PermitLimit = 4;
        const int ContenderCount = 120;
        using var harness = GuardHarness.Create(options =>
            options.LoginAccountPermitLimit = PermitLimit);
        using var holder = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("holder"),
            "same-account",
            TestContext.Current.CancellationToken);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, ContenderCount)
            .Select(index => Task.Run(async () =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                return await harness.Guard.BeginAccountAttemptAsync(
                    AuthenticationOperation.Login,
                    harness.Context($"client-{index}"),
                    "same-account",
                    TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken))
            .ToArray();
        start.Set();

        var attempts = await Task.WhenAll(tasks);
        Assert.All(attempts, attempt =>
        {
            Assert.False(attempt.IsAllowed);
            Assert.Equal(1, attempt.Decision.RetryAfterSeconds);
            attempt.Dispose();
        });
        holder.Dispose();

        for (var permit = 1; permit < PermitLimit; permit++)
        {
            using var admitted = await harness.Guard.BeginAccountAttemptAsync(
                AuthenticationOperation.Login,
                harness.Context($"sequential-{permit}"),
                "same-account",
                TestContext.Current.CancellationToken);
            Assert.True(admitted.IsAllowed);
        }

        using var exhausted = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("exhausted"),
            "same-account",
            TestContext.Current.CancellationToken);
        Assert.False(exhausted.IsAllowed);
    }

    [Fact]
    public async Task PasswordSprayCountsDistinctAccountsAndThresholdRequestIsRejected()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.PasswordSprayDistinctAccountLimit = 3;
            options.PasswordSprayBlockSeconds = 19;
        });

        Assert.True(await RecordFailure(harness, "client-a", "ACCOUNT-A"));
        Assert.True(await RecordFailure(harness, "client-a", "ACCOUNT-A"));
        Assert.True(await RecordFailure(harness, "client-a", "account-a"));
        Assert.True(await RecordFailure(harness, "client-a", "account-b"));
        var threshold = await RecordFailureDecision(harness, "client-a", "account-c");

        Assert.False(threshold.IsAllowed);
        Assert.Equal(19, threshold.RetryAfterSeconds);
    }

    [Fact]
    public async Task InFlightFailureObservesActiveSprayBlockAndExpiredBlockStartsFreshWindow()
    {
        const int StripeCount = 64;
        const string InFlightAccount = "in-flight";
        var thresholdAccount = FindAccountOnDifferentStripe(InFlightAccount, StripeCount);
        using var harness = GuardHarness.Create(options =>
        {
            options.PasswordSprayDistinctAccountLimit = 2;
            options.PasswordSprayBlockSeconds = 3;
            options.AccountLockStripeCount = StripeCount;
        });

        Assert.True(await RecordFailure(harness, "client-a", "seed"));
        using var duringBlock = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            InFlightAccount,
            TestContext.Current.CancellationToken);
        Assert.True(duringBlock.IsAllowed);
        Assert.False(await RecordFailure(harness, "client-a", thresholdAccount));

        var inFlightDecision = harness.Guard.RecordLoginFailure(duringBlock);
        Assert.False(inFlightDecision.IsAllowed);
        Assert.Equal(3, inFlightDecision.RetryAfterSeconds);
        duringBlock.Dispose();

        using var afterBlock = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "after-block",
            TestContext.Current.CancellationToken);
        Assert.False(afterBlock.IsAllowed);

        harness.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await RecordFailure(harness, "client-a", "third"));
    }

    [Fact]
    public async Task SprayTrackingCapacityFailureRejectsThresholdMutation()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.MaximumTrackedPartitions = 4;
            options.LoginAccountWindowSeconds = 7;
        });
        using var attempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "account-a",
            TestContext.Current.CancellationToken);

        var decision = harness.Guard.RecordLoginFailure(attempt);

        Assert.False(decision.IsAllowed);
        Assert.Equal(7, decision.RetryAfterSeconds);
    }

    [Fact]
    public async Task GlobalSprayEntryCapIsExactWithoutEmptyStateLeakAndRecoversOnExpiry()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.MaximumTrackedPartitions = 512;
            options.MaximumTrackedSprayAccountEntries = 64;
            options.PasswordSprayDistinctAccountLimit = 64;
            options.PasswordSprayWindowSeconds = 4;
            options.LoginAccountWindowSeconds = 4;
        });
        for (var index = 0; index < 64; index++)
        {
            Assert.True(await RecordFailure(harness, $"client-{index}", "same-account"));
        }
        Assert.False(await RecordFailure(harness, "overflow-client-c", "same-account"));
        Assert.False(await RecordFailure(harness, "overflow-client-d", "same-account"));

        using var independentAccount = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-0"),
            "same-account",
            TestContext.Current.CancellationToken);
        Assert.True(independentAccount.IsAllowed);
        independentAccount.Dispose();

        // The failed spray-state creations above must not consume tracked-partition
        // capacity. The cohort has room for exactly these 63 additional account
        // windows after its 64 spray states and the shared account window.
        for (var index = 0; index < 63; index++)
        {
            using var accountAttempt = await harness.Guard.BeginAccountAttemptAsync(
                AuthenticationOperation.Login,
                harness.Context("client-0"),
                $"capacity-account-{index}",
                TestContext.Current.CancellationToken);
            Assert.True(accountAttempt.IsAllowed);
        }

        using var capacityProbe = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-0"),
            "capacity-probe",
            TestContext.Current.CancellationToken);
        Assert.False(capacityProbe.IsAllowed);

        harness.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.True(await RecordFailure(harness, "overflow-client-c", "same-account"));
    }

    [Fact]
    public async Task SprayThresholdClearsGlobalEntryBudgetAndOtherCohortsRemainAvailable()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.MaximumTrackedPartitions = 16;
            options.MaximumTrackedSprayAccountEntries = 64;
            options.PasswordSprayDistinctAccountLimit = 2;
            options.PasswordSprayBlockSeconds = 1;
        });

        Assert.True(await RecordFailure(harness, "client-a", "account-a"));
        Assert.False(await RecordFailure(harness, "client-a", "account-b"));
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await RecordFailure(harness, "client-b", "account-c"));

        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("login-client")).IsAllowed);
        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Registration,
            harness.Context("registration-client")).IsAllowed);
        using var registrationAccount = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Registration,
            harness.Context("registration-client"),
            "registration-account",
            TestContext.Current.CancellationToken);
        Assert.True(registrationAccount.IsAllowed);
    }

    [Fact]
    public async Task ExhaustingEachCapacityCohortDoesNotDenyOtherCohorts()
    {
        using var harness = GuardHarness.Create(options => options.MaximumTrackedPartitions = 4);

        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("login-a")).IsAllowed);
        Assert.False(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("login-b")).IsAllowed);

        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Registration,
            harness.Context("registration-a")).IsAllowed);
        using var loginAccount = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("account-client"),
            "login-account",
            TestContext.Current.CancellationToken);
        Assert.True(loginAccount.IsAllowed);
        loginAccount.Dispose();
        using var registrationAccount = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Registration,
            harness.Context("account-client"),
            "registration-account",
            TestContext.Current.CancellationToken);
        Assert.True(registrationAccount.IsAllowed);
    }

    [Fact]
    public async Task CapacityRetryUsesEarliestFixedOrSprayExpiry()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.MaximumTrackedPartitions = 12;
            options.LoginAccountWindowSeconds = 30;
            options.PasswordSprayWindowSeconds = 10;
        });
        using (var attempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("spray-client"),
            "account-a",
            TestContext.Current.CancellationToken))
        {
            Assert.True(harness.Guard.RecordLoginFailure(attempt).IsAllowed);
        }

        using var secondAccount = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("second-client"),
            "account-b",
            TestContext.Current.CancellationToken);
        Assert.True(secondAccount.IsAllowed);
        secondAccount.Dispose();

        using var capacityAttempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("new-client"),
            "account-c",
            TestContext.Current.CancellationToken);

        Assert.False(capacityAttempt.IsAllowed);
        Assert.Equal(10, capacityAttempt.Decision.RetryAfterSeconds);
    }

    [Fact]
    public async Task ActiveSprayBlockRejectsBeforeConsumingAccountPermitThenRecovers()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.PasswordSprayDistinctAccountLimit = 2;
            options.PasswordSprayBlockSeconds = 5;
            options.LoginAccountPermitLimit = 1;
        });

        Assert.True(await RecordFailure(harness, "attacker", "account-a"));
        Assert.False(await RecordFailure(harness, "attacker", "account-b"));

        using var blocked = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("attacker"),
            "target-account",
            TestContext.Current.CancellationToken);
        Assert.False(blocked.IsAllowed);
        Assert.Equal(5, blocked.Decision.RetryAfterSeconds);

        using var otherClient = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("innocent"),
            "target-account",
            TestContext.Current.CancellationToken);
        Assert.True(otherClient.IsAllowed);
        otherClient.Dispose();

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        using var recovered = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("attacker"),
            "fresh-account",
            TestContext.Current.CancellationToken);
        Assert.True(recovered.IsAllowed);
    }

    [Fact]
    public async Task Ipv6SixtyFourPrefixSharesSprayBlockAndDifferentPrefixIsIsolated()
    {
        var provider = new AuthenticationPartitionKeyProvider(
            Options.Create(new AuthenticationAbuseOptions()));
        using var harness = GuardHarness.Create(
            options => options.PasswordSprayDistinctAccountLimit = 2,
            provider);

        using (var first = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:20::1"),
            "ACCOUNT-A",
            TestContext.Current.CancellationToken))
        {
            Assert.True(harness.Guard.RecordLoginFailure(first).IsAllowed);
        }

        using (var threshold = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:20:ffff::2"),
            "ACCOUNT-B",
            TestContext.Current.CancellationToken))
        {
            Assert.False(harness.Guard.RecordLoginFailure(threshold).IsAllowed);
        }

        using var blocked = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:20:abcd::3"),
            "ACCOUNT-C",
            TestContext.Current.CancellationToken);
        Assert.False(blocked.IsAllowed);

        using var isolated = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            IpContext("2001:db8:10:21::1"),
            "ACCOUNT-C",
            TestContext.Current.CancellationToken);
        Assert.True(isolated.IsAllowed);
    }

    [Fact]
    public async Task SprayWindowExpiryResetsDistinctAccountTracking()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.PasswordSprayDistinctAccountLimit = 2;
            options.PasswordSprayWindowSeconds = 3;
        });

        Assert.True(await RecordFailure(harness, "client-a", "account-a"));
        harness.Time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await RecordFailure(harness, "client-a", "account-b"));
    }

    [Fact]
    public async Task RegistrationAttemptsDoNotConsultLoginSprayBlock()
    {
        using var harness = GuardHarness.Create(options =>
            options.PasswordSprayDistinctAccountLimit = 2);
        Assert.True(await RecordFailure(harness, "client-a", "account-a"));
        Assert.False(await RecordFailure(harness, "client-a", "account-b"));

        using var registration = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Registration,
            harness.Context("client-a"),
            "new-account",
            TestContext.Current.CancellationToken);

        Assert.True(registration.IsAllowed);
    }

    [Fact]
    public void CapacityFailsClosedAndExpiredStateIsEvicted()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.MaximumTrackedPartitions = 8;
            options.LoginIpWindowSeconds = 4;
        });

        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-a")).IsAllowed);
        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-b")).IsAllowed);
        var capacity = harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-c"));
        Assert.False(capacity.IsAllowed);
        Assert.Equal(4, capacity.RetryAfterSeconds);

        harness.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.True(harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-c")).IsAllowed);
    }

    [Fact]
    public async Task BoundedEvictionCursorReclaimsExpiredStateBehindSixtyFourLiveStatesOnNextProbe()
    {
        using var harness = GuardHarness.Create(options =>
        {
            options.MaximumTrackedPartitions = 260;
            options.LoginAccountWindowSeconds = 100;
            options.PasswordSprayWindowSeconds = 2;
        });
        for (var index = 0; index < 64; index++)
        {
            using var fixedAttempt = await harness.Guard.BeginAccountAttemptAsync(
                AuthenticationOperation.Login,
                harness.Context($"fixed-client-{index}"),
                $"fixed-account-{index}",
                TestContext.Current.CancellationToken);
            Assert.True(fixedAttempt.IsAllowed);
        }

        using (var sprayAttempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("fixed-client-0"),
            "fixed-account-0",
            TestContext.Current.CancellationToken))
        {
            Assert.True(harness.Guard.RecordLoginFailure(sprayAttempt).IsAllowed);
        }

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        using var firstProbe = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("probe-client-1"),
            "probe-account-1",
            TestContext.Current.CancellationToken);
        Assert.False(firstProbe.IsAllowed);
        Assert.Equal(1, firstProbe.Decision.RetryAfterSeconds);
        using var secondProbe = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("probe-client-2"),
            "probe-account-2",
            TestContext.Current.CancellationToken);
        Assert.True(secondProbe.IsAllowed);
    }

    [Fact]
    public async Task GuardValidatesConstructorOperationsAndAttemptOwnership()
    {
        var options = Options.Create(new AuthenticationAbuseOptions());
        var keys = new TestPartitionKeyProvider();
        var time = new TestTimeProvider();
        Assert.Throws<ArgumentNullException>(() => new AuthenticationAbuseGuard(null!, options, time));
        Assert.Throws<ArgumentNullException>(() => new AuthenticationAbuseGuard(keys, null!, time));
        Assert.Throws<ArgumentNullException>(() => new AuthenticationAbuseGuard(keys, options, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthenticationAbuseGuard(
            keys,
            Options.Create(new AuthenticationAbuseOptions { MaximumTrackedPartitions = 0 }),
            time));

        using var first = GuardHarness.Create();
        using var second = GuardHarness.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Guard.TryAcquireClient(
            (AuthenticationOperation)99,
            first.Context("client-a")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            first.Guard.BeginAccountAttemptAsync(
                (AuthenticationOperation)99,
                first.Context("client-a"),
                "account-a",
                TestContext.Current.CancellationToken).AsTask());

        using var attempt = await first.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            first.Context("client-a"),
            "account-a",
            TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => second.Guard.RecordLoginFailure(attempt));
    }

    [Fact]
    public async Task AttemptRejectsMisuseAndGuardDisposeIsRobust()
    {
        using var harness = GuardHarness.Create(options => options.LoginAccountPermitLimit = 1);
        var first = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-a"),
            "account-a",
            TestContext.Current.CancellationToken);
        Assert.True(harness.Guard.RecordLoginFailure(first).IsAllowed);
        Assert.Throws<InvalidOperationException>(() => harness.Guard.RecordLoginFailure(first));
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => harness.Guard.RecordLoginFailure(first));

        using var rejected = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context("client-b"),
            "account-a",
            TestContext.Current.CancellationToken);
        Assert.False(rejected.IsAllowed);
        Assert.Throws<InvalidOperationException>(() => harness.Guard.RecordLoginFailure(rejected));

        using var registration = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Registration,
            harness.Context("client-a"),
            "account-b",
            TestContext.Current.CancellationToken);
        Assert.True(registration.IsAllowed);
        Assert.Throws<InvalidOperationException>(() => harness.Guard.RecordLoginFailure(registration));

        harness.Guard.Dispose();
        harness.Guard.Dispose();
        Assert.Throws<ObjectDisposedException>(() => harness.Guard.TryAcquireClient(
            AuthenticationOperation.Login,
            harness.Context("client-z")));
    }

    [Fact]
    public void DecisionRejectAfterValidatesPositiveRetry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuthenticationThrottleDecision.RejectAfter(0));
        var decision = AuthenticationThrottleDecision.RejectAfter(9);
        Assert.False(decision.IsAllowed);
        Assert.Equal(9, decision.RetryAfterSeconds);
    }

    private static async Task<bool> RecordFailure(
        GuardHarness harness,
        string client,
        string account) =>
        (await RecordFailureDecision(harness, client, account)).IsAllowed;

    private static async Task<AuthenticationThrottleDecision> RecordFailureDecision(
        GuardHarness harness,
        string client,
        string account)
    {
        using var attempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Context(client),
            account.ToUpperInvariant(),
            TestContext.Current.CancellationToken);
        Assert.True(attempt.IsAllowed);
        return harness.Guard.RecordLoginFailure(attempt);
    }

    private static string FindAccountOnDifferentStripe(string account, int stripeCount)
    {
        var accountStripe = GetStripeIndex(account.ToUpperInvariant(), stripeCount);
        for (var index = 0; ; index++)
        {
            var candidate = $"threshold-{index}";
            if (GetStripeIndex(candidate.ToUpperInvariant(), stripeCount) != accountStripe)
            {
                return candidate;
            }
        }
    }

    private static int GetStripeIndex(string accountKey, int stripeCount) =>
        (int)((uint)StringComparer.Ordinal.GetHashCode(accountKey) % (uint)stripeCount);

    private static DefaultHttpContext IpContext(string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    private sealed class GuardHarness : IDisposable
    {
        private readonly IAuthenticationPartitionKeyProvider _keyProvider;

        private GuardHarness(
            AuthenticationAbuseGuard guard,
            TestTimeProvider time,
            IAuthenticationPartitionKeyProvider keyProvider)
        {
            Guard = guard;
            Time = time;
            _keyProvider = keyProvider;
        }

        public AuthenticationAbuseGuard Guard { get; }

        public TestTimeProvider Time { get; }

        public static GuardHarness Create(
            Action<AuthenticationAbuseOptions>? configure = null,
            IAuthenticationPartitionKeyProvider? keyProvider = null)
        {
            var options = new AuthenticationAbuseOptions
            {
                LoginIpPermitLimit = 1000,
                LoginIpWindowSeconds = 60,
                RegistrationIpPermitLimit = 1000,
                RegistrationIpWindowSeconds = 60,
                LoginAccountPermitLimit = 1000,
                LoginAccountWindowSeconds = 60,
                RegistrationAccountPermitLimit = 1000,
                RegistrationAccountWindowSeconds = 60,
                PasswordSprayDistinctAccountLimit = 1000,
                PasswordSprayWindowSeconds = 60,
                PasswordSprayBlockSeconds = 60,
                MaximumTrackedPartitions = 10000,
                MaximumTrackedSprayAccountEntries = 50000,
                AccountLockStripeCount = 64,
                MaximumRetryAfterSeconds = 1000
            };
            configure?.Invoke(options);
            var actualKeyProvider = keyProvider ?? new TestPartitionKeyProvider();
            var time = new TestTimeProvider();
            return new GuardHarness(
                new AuthenticationAbuseGuard(actualKeyProvider, Options.Create(options), time),
                time,
                actualKeyProvider);
        }

        public DefaultHttpContext Context(string clientKey)
        {
            var context = new DefaultHttpContext();
            context.Items[TestPartitionKeyProvider.ClientKeyItem] = clientKey;
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            return context;
        }

        public void Dispose()
        {
            Guard.Dispose();
            (_keyProvider as IDisposable)?.Dispose();
        }
    }

    private sealed class TestPartitionKeyProvider(
        Func<HttpContext, string>? getClientKey = null,
        Func<string, string>? getAccountKey = null) : IAuthenticationPartitionKeyProvider
    {
        public const string ClientKeyItem = "authentication-test-client-key";

        public string GetClientKey(HttpContext httpContext) =>
            getClientKey?.Invoke(httpContext)
            ?? (string)httpContext.Items[ClientKeyItem]!;

        public string GetAccountKey(string normalizedAccount) =>
            getAccountKey?.Invoke(normalizedAccount)
            ?? normalizedAccount.ToUpperInvariant();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan amount) =>
            Interlocked.Add(ref _timestamp, amount.Ticks);
    }
}
