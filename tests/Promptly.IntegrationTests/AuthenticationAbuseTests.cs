using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Promptly.Domain.Entities;
using Promptly.Server.Models;
using Promptly.Server.Security;

namespace Promptly.IntegrationTests;

public sealed class AuthenticationAbuseTests(IntegrationFixture fixture)
{
    private const string Password = "Integration1";
    private const string WrongPassword = "WrongPassword1";
    private const string AllowedOrigin = "http://localhost:3000";

    [Fact]
    public async Task Identity_lockout_is_persisted_generic_and_recovers_after_expiry()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:IdentityMaxFailedAccessAttempts"] = "2";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var email = UniqueEmail("lockout");
            using var registration = await SendRegistrationAsync(
                host.Client,
                email,
                client: "lockout-registration");
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

            using var missing = await SendLoginAsync(
                host.Client,
                UniqueEmail("missing"),
                WrongPassword,
                client: "lockout-client");
            var missingBody = await AssertGenericUnauthorizedAsync(missing);

            using var firstFailure = await SendLoginAsync(
                host.Client,
                email,
                WrongPassword,
                client: "lockout-client");
            var wrongBody = await AssertGenericUnauthorizedAsync(firstFailure);

            using var thresholdFailure = await SendLoginAsync(
                host.Client,
                email,
                WrongPassword,
                client: "lockout-client");
            var thresholdBody = await AssertGenericUnauthorizedAsync(thresholdFailure);

            using var locked = await SendLoginAsync(
                host.Client,
                email,
                Password,
                client: "lockout-client");
            var lockedBody = await AssertGenericUnauthorizedAsync(locked);

            Assert.Equal(missingBody, wrongBody);
            Assert.Equal(wrongBody, thresholdBody);
            Assert.Equal(thresholdBody, lockedBody);
            Assert.DoesNotContain(email, lockedBody, StringComparison.OrdinalIgnoreCase);

            await using (var scope = host.Factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var user = Assert.IsType<User>(await userManager.FindByEmailAsync(email));
                Assert.True(await userManager.IsLockedOutAsync(user));
                Assert.NotNull(user.LockoutEnd);
                Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);

                var recovery = await userManager.SetLockoutEndDateAsync(
                    user,
                    DateTimeOffset.UtcNow.AddMinutes(-1));
                Assert.True(recovery.Succeeded, FormatIdentityErrors(recovery));
            }

            using var recovered = await SendLoginAsync(
                host.Client,
                email,
                Password,
                client: "lockout-client");
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

            await using var verificationScope = host.Factory.Services.CreateAsyncScope();
            var verificationManager = verificationScope.ServiceProvider
                .GetRequiredService<UserManager<User>>();
            var recoveredUser = Assert.IsType<User>(
                await verificationManager.FindByEmailAsync(email));
            Assert.False(await verificationManager.IsLockedOutAsync(recoveredUser));
            Assert.Equal(0, await verificationManager.GetAccessFailedCountAsync(recoveredUser));
        });
    }

    [Fact]
    public async Task Login_client_limit_returns_stable_private_problem_and_preserves_cors()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:LoginIpPermitLimit"] = "2";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            const string limitedClient = "login-client-limited";
            using var first = await SendLoginAsync(
                host.Client,
                UniqueEmail("client-first"),
                WrongPassword,
                limitedClient);
            await AssertGenericUnauthorizedAsync(first);

            using var second = await SendLoginAsync(
                host.Client,
                UniqueEmail("client-second"),
                WrongPassword,
                limitedClient);
            await AssertGenericUnauthorizedAsync(second);

            var sensitiveEmail = UniqueEmail("must-not-leak");
            using var limited = await SendLoginAsync(
                host.Client,
                sensitiveEmail,
                WrongPassword,
                limitedClient,
                origin: AllowedOrigin);
            await AssertThrottleProblemAsync(limited, 60, sensitiveEmail, limitedClient);
            Assert.Equal(
                AllowedOrigin,
                Assert.Single(limited.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal(
                "true",
                Assert.Single(limited.Headers.GetValues("Access-Control-Allow-Credentials")));
            AssertRetryAfterIsCorsExposed(limited);

            using var isolated = await SendLoginAsync(
                host.Client,
                UniqueEmail("client-isolated"),
                WrongPassword,
                client: "login-client-isolated");
            await AssertGenericUnauthorizedAsync(isolated);
        });
    }

    [Fact]
    public async Task Registration_client_limit_does_not_starve_another_client()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:RegistrationIpPermitLimit"] = "1";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            const string limitedClient = "registration-client-limited";
            using var first = await SendRegistrationAsync(
                host.Client,
                UniqueEmail("registration-first"),
                limitedClient);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var sensitiveEmail = UniqueEmail("registration-limited");
            using var limited = await SendRegistrationAsync(
                host.Client,
                sensitiveEmail,
                limitedClient);
            await AssertThrottleProblemAsync(limited, 60, sensitiveEmail, limitedClient);

            using var isolated = await SendRegistrationAsync(
                host.Client,
                UniqueEmail("registration-isolated"),
                client: "registration-client-isolated");
            Assert.Equal(HttpStatusCode.OK, isolated.StatusCode);
        });
    }

    [Fact]
    public async Task Concurrent_client_admission_is_exact_and_malformed_json_consumes_permits()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:LoginIpPermitLimit"] = "4";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            const string concurrentClient = "concurrent-login-client";
            var distinctStripeEmails = CreateEmailsOnDistinctStripes(
                "concurrent-client",
                count: 12,
                stripeCount: 64);
            var requests = distinctStripeEmails
                .Select(email => SendLoginAsync(
                    host.Client,
                    email,
                    WrongPassword,
                    concurrentClient))
                .ToArray();
            using var responses = await HttpResponseBatch.WhenAllAsync(requests);
            Assert.Equal(
                4,
                responses.Messages.Count(
                    response => response.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(
                8,
                responses.Messages.Count(
                    response => response.StatusCode == HttpStatusCode.TooManyRequests));
            Assert.All(
                responses.Messages,
                response => Assert.Contains(
                    response.StatusCode,
                    new[]
                    {
                        HttpStatusCode.Unauthorized,
                        HttpStatusCode.TooManyRequests
                    }));

            const string malformedClient = "malformed-login-client";
            for (var index = 0; index < 4; index++)
            {
                using var malformed = await SendMalformedLoginAsync(host.Client, malformedClient);
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }

            using var limited = await SendLoginAsync(
                host.Client,
                UniqueEmail("after-malformed"),
                WrongPassword,
                malformedClient);
            await AssertThrottleProblemAsync(limited, 60, malformedClient);
        });
    }

    [Fact]
    public async Task Aggregate_authentication_saturation_is_zero_queue_and_recovers_after_cancellation()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:MaximumConcurrentAuthenticationRequests"] = "2";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var accounts = CreateEmailsOnDistinctStripes(
                "aggregate-holder",
                count: 2,
                stripeCount: 64);
            var credentialGate = host.Factory.Services
                .GetRequiredService<AuthenticationCredentialGate>();
            var partitionKeys = host.Factory.Services
                .GetRequiredService<TestAuthenticationPartitionKeyProvider>();
            var bodyProbe = host.Factory.Services
                .GetRequiredService<AuthenticationBodyReadProbe>();
            credentialGate.Hold(accounts);

            using var firstCancellation = new CancellationTokenSource();
            var firstHeld = SendLoginAsync(
                host.Client,
                accounts[0],
                WrongPassword,
                client: "aggregate-holder-first",
                cancellationToken: firstCancellation.Token);
            var secondHeld = SendLoginAsync(
                host.Client,
                accounts[1],
                WrongPassword,
                client: "aggregate-holder-second");
            var secondResponseDisposed = false;
            try
            {
                using var heldTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await credentialGate.WaitUntilHeldAsync(heldTimeout.Token);

                var clientCallsBeforeSaturation = partitionKeys.ClientKeyCallCount;
                var accountCallsBeforeSaturation = partitionKeys.AccountKeyCallCount;
                var verificationCallsBeforeSaturation = credentialGate.VerificationCallCount;
                var usersBeforeSaturation = await CountUsersAsync(host);
                var bodyReadsBeforeSaturation = bodyProbe.ReadCount;

                using var saturatedLogin = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("aggregate-saturated-login"),
                    WrongPassword,
                    client: "aggregate-saturated-login-client",
                    observeBodyReads: true);
                await AssertThrottleProblemAsync(saturatedLogin, 60);

                using var saturatedRegistration = await SendRegistrationAsync(
                    host.Client,
                    UniqueEmail("aggregate-saturated-registration"),
                    client: "aggregate-saturated-registration-client",
                    observeBodyReads: true);
                await AssertThrottleProblemAsync(saturatedRegistration, 60);

                Assert.Equal(
                    clientCallsBeforeSaturation + 2,
                    partitionKeys.ClientKeyCallCount);
                Assert.Equal(accountCallsBeforeSaturation, partitionKeys.AccountKeyCallCount);
                Assert.Equal(
                    verificationCallsBeforeSaturation,
                    credentialGate.VerificationCallCount);
                Assert.Equal(usersBeforeSaturation, await CountUsersAsync(host));
                Assert.Equal(bodyReadsBeforeSaturation, bodyProbe.ReadCount);

                firstCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstHeld);

                // Reuse the canceled holder's account so recovery does not depend on
                // a randomly selected account stripe being distinct from the survivor.
                var recoveredEmail = accounts[0];
                using var recovered = await SendRegistrationAsync(
                    host.Client,
                    recoveredEmail,
                    client: "aggregate-recovered-registration-client");
                Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
                Assert.Equal(usersBeforeSaturation + 1, await CountUsersAsync(host));

                credentialGate.Release();
                using (var secondResponse = await secondHeld)
                {
                    secondResponseDisposed = true;
                    await AssertGenericUnauthorizedAsync(secondResponse);
                }
            }
            finally
            {
                credentialGate.Release();
                firstCancellation.Cancel();
                await DrainAuthenticationRequestAsync(firstHeld);
                if (!secondResponseDisposed)
                {
                    await DrainAuthenticationRequestAsync(secondHeld);
                }
            }
        });
    }

    [Fact]
    public async Task Concurrent_login_account_limits_are_exact_and_partition_isolated()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:IdentityMaxFailedAccessAttempts"] = "4";
        settings["AuthenticationAbuse:LoginAccountPermitLimit"] = "4";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var accounts = CreateEmailsOnDistinctStripes(
                "concurrent-account",
                count: 2,
                stripeCount: 64);
            var gate = host.Factory.Services.GetRequiredService<AuthenticationCredentialGate>();
            gate.Hold(accounts);
            var heldRequests = accounts
                .Select((account, index) => SendLoginAsync(
                    host.Client,
                    account,
                    WrongPassword,
                    $"holder-{index}"))
                .ToArray();
            try
            {
                using var heldTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await gate.WaitUntilHeldAsync(heldTimeout.Token);

                var busyRequests = accounts
                    .SelectMany((account, accountIndex) => Enumerable.Range(0, 11)
                        .Select(index => SendLoginAsync(
                            host.Client,
                            account,
                            WrongPassword,
                            $"busy-{accountIndex}-{index}")))
                    .ToArray();
                using var busyResponses = await HttpResponseBatch.WhenAllAsync(busyRequests);
                Assert.All(busyResponses.Messages, response =>
                    Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode));
                foreach (var response in busyResponses.Messages)
                {
                    await AssertThrottleProblemAsync(
                        response,
                        maximumRetryAfterSeconds: 60,
                        accounts[0],
                        accounts[1]);
                }
            }
            finally
            {
                gate.Release();
                try
                {
                    await Task.WhenAll(heldRequests);
                    foreach (var heldRequest in heldRequests)
                    {
                        await AssertGenericUnauthorizedAsync(heldRequest.Result);
                    }
                }
                finally
                {
                    foreach (var heldRequest in heldRequests.Where(
                                 heldRequest => heldRequest.IsCompletedSuccessfully))
                    {
                        heldRequest.Result.Dispose();
                    }
                }
            }

            for (var index = 0; index < 3; index++)
            {
                using var firstRemaining = await SendLoginAsync(
                    host.Client,
                    accounts[0],
                    WrongPassword,
                    $"first-remaining-{index}");
                await AssertGenericUnauthorizedAsync(firstRemaining);
            }

            using (var firstLimited = await SendLoginAsync(
                       host.Client,
                       accounts[0],
                       WrongPassword,
                       "first-limited"))
            {
                await AssertThrottleProblemAsync(firstLimited, 60, accounts[0]);
            }

            for (var index = 0; index < 3; index++)
            {
                using var isolatedRemaining = await SendLoginAsync(
                    host.Client,
                    accounts[1],
                    WrongPassword,
                    $"isolated-remaining-{index}");
                await AssertGenericUnauthorizedAsync(isolatedRemaining);
            }

            using var isolatedLimited = await SendLoginAsync(
                host.Client,
                accounts[1],
                WrongPassword,
                "isolated-limited");
            await AssertThrottleProblemAsync(isolatedLimited, 60, accounts[1]);
        });
    }

    [Fact]
    public async Task Overlong_authentication_fields_are_rejected_before_identity_work()
    {
        var settings = CreateSettings();

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var credentialGate = host.Factory.Services
                .GetRequiredService<AuthenticationCredentialGate>();
            var partitionKeys = host.Factory.Services
                .GetRequiredService<TestAuthenticationPartitionKeyProvider>();
            var verificationCallsBefore = credentialGate.VerificationCallCount;
            var accountKeyCallsBefore = partitionKeys.AccountKeyCallCount;
            var userCountBefore = await CountUsersAsync(host);

            using var overlongLogin = await SendAuthenticationJsonAsync(
                host.Client,
                "/api/auth/login",
                JsonSerializer.Serialize(new
                {
                    email = OverlongEmail(),
                    password = WrongPassword
                }),
                client: UniqueEmail("overlong-login-client"));
            Assert.Equal(HttpStatusCode.BadRequest, overlongLogin.StatusCode);

            var registrations = new[]
            {
                new
                {
                    Email = OverlongEmail(),
                    Password,
                    Name = "Valid Name"
                },
                new
                {
                    Email = UniqueEmail("overlong-registration-password"),
                    Password = new string(
                        'a',
                        AuthenticationInputLimits.PasswordMaxLength + 1),
                    Name = "Valid Name"
                },
                new
                {
                    Email = UniqueEmail("overlong-registration-name"),
                    Password,
                    Name = new string('a', AuthenticationInputLimits.NameMaxLength + 1)
                }
            };
            foreach (var registration in registrations)
            {
                using var response = await SendAuthenticationJsonAsync(
                    host.Client,
                    "/api/auth/register",
                    JsonSerializer.Serialize(new
                    {
                        email = registration.Email,
                        password = registration.Password,
                        name = registration.Name
                    }),
                    client: UniqueEmail("overlong-registration-client"));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            Assert.Equal(verificationCallsBefore, credentialGate.VerificationCallCount);
            Assert.Equal(accountKeyCallsBefore, partitionKeys.AccountKeyCallCount);
            Assert.Equal(userCountBefore, await CountUsersAsync(host));
        });
    }

    [Fact]
    public async Task Legacy_password_above_registration_limit_remains_login_compatible()
    {
        var settings = CreateSettings();

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var email = UniqueEmail("legacy-long-password");
            var legacyPassword = string.Concat(
                "A1",
                new string('a', AuthenticationInputLimits.PasswordMaxLength - 1));
            await using (var scope = host.Factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var result = await userManager.CreateAsync(
                    new User
                    {
                        UserName = email,
                        Email = email,
                        CreatedAt = DateTime.UtcNow
                    },
                    legacyPassword);
                Assert.True(result.Succeeded, FormatIdentityErrors(result));
            }

            using var login = await SendLoginAsync(
                host.Client,
                email,
                legacyPassword,
                client: "legacy-long-password-client");

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        });
    }

    [Fact]
    public async Task Oversized_authentication_bodies_return_413_before_identity_work()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:LoginIpPermitLimit"] = "1";
        settings["AuthenticationAbuse:RegistrationIpPermitLimit"] = "1";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var credentialGate = host.Factory.Services
                .GetRequiredService<AuthenticationCredentialGate>();
            var partitionKeys = host.Factory.Services
                .GetRequiredService<TestAuthenticationPartitionKeyProvider>();
            var verificationCallsBefore = credentialGate.VerificationCallCount;
            var accountKeyCallsBefore = partitionKeys.AccountKeyCallCount;
            var userCountBefore = await CountUsersAsync(host);
            var oversizedPadding = new string(
                'a',
                AuthenticationInputLimits.RequestBodyMaxBytes);

            using var login = await SendAuthenticationJsonAsync(
                host.Client,
                "/api/auth/login",
                JsonSerializer.Serialize(new
                {
                    email = UniqueEmail("oversized-login"),
                    password = WrongPassword,
                    padding = oversizedPadding
                }),
                client: "oversized-login-client");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, login.StatusCode);

            using var loginThrottled = await SendAuthenticationJsonAsync(
                host.Client,
                "/api/auth/login",
                JsonSerializer.Serialize(new
                {
                    email = UniqueEmail("oversized-login-throttled"),
                    password = WrongPassword,
                    padding = oversizedPadding
                }),
                client: "oversized-login-client");
            Assert.Equal(HttpStatusCode.TooManyRequests, loginThrottled.StatusCode);

            using var registration = await SendAuthenticationJsonAsync(
                host.Client,
                "/api/auth/register",
                JsonSerializer.Serialize(new
                {
                    email = UniqueEmail("oversized-registration"),
                    password = Password,
                    name = "Oversized Registration",
                    padding = oversizedPadding
                }),
                client: "oversized-registration-client");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, registration.StatusCode);

            using var registrationThrottled = await SendAuthenticationJsonAsync(
                host.Client,
                "/api/auth/register",
                JsonSerializer.Serialize(new
                {
                    email = UniqueEmail("oversized-registration-throttled"),
                    password = Password,
                    name = "Oversized Registration",
                    padding = oversizedPadding
                }),
                client: "oversized-registration-client");
            Assert.Equal(HttpStatusCode.TooManyRequests, registrationThrottled.StatusCode);

            using var unknownLength = await SendAuthenticationContentAsync(
                host.Client,
                "/api/auth/login",
                new UnknownLengthJsonContent(JsonSerializer.Serialize(new
                {
                    email = UniqueEmail("oversized-unknown-length-login"),
                    password = WrongPassword,
                    padding = oversizedPadding
                })),
                client: "oversized-unknown-length-login-client");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, unknownLength.StatusCode);

            using var unknownLengthThrottled = await SendAuthenticationContentAsync(
                host.Client,
                "/api/auth/login",
                new UnknownLengthJsonContent(JsonSerializer.Serialize(new
                {
                    email = UniqueEmail("oversized-unknown-length-throttled"),
                    password = WrongPassword,
                    padding = oversizedPadding
                })),
                client: "oversized-unknown-length-login-client");
            Assert.Equal(HttpStatusCode.TooManyRequests, unknownLengthThrottled.StatusCode);

            Assert.Equal(verificationCallsBefore, credentialGate.VerificationCallCount);
            Assert.Equal(accountKeyCallsBefore, partitionKeys.AccountKeyCallCount);
            Assert.Equal(userCountBefore, await CountUsersAsync(host));
        });
    }

    [Fact]
    public async Task Registration_account_limit_uses_normalized_account_and_isolates_accounts()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:RegistrationAccountPermitLimit"] = "2";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var email = UniqueEmail("registration-account");
            using var created = await SendRegistrationAsync(
                host.Client,
                email,
                client: "registration-account-a");
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            using var duplicate = await SendRegistrationAsync(
                host.Client,
                email.ToUpperInvariant(),
                client: "registration-account-b");
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

            using var limited = await SendRegistrationAsync(
                host.Client,
                email,
                client: "registration-account-c");
            await AssertThrottleProblemAsync(limited, 60, email);

            using var isolated = await SendRegistrationAsync(
                host.Client,
                UniqueEmail("registration-account-isolated"),
                client: "registration-account-c");
            Assert.Equal(HttpStatusCode.OK, isolated.StatusCode);
        });
    }

    [Fact]
    public async Task Password_spray_blocks_login_but_not_an_isolated_client()
    {
        var settings = CreateSettings();
        settings["AuthenticationAbuse:PasswordSprayDistinctAccountLimit"] = "3";

        await RunWithPartitionedHostAsync(settings, async host =>
        {
            var victimEmail = UniqueEmail("spray-victim");
            using var registration = await SendRegistrationAsync(
                host.Client,
                victimEmail,
                client: "spray-registration");
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

            const string attacker = "spray-attacker";
            for (var index = 0; index < 2; index++)
            {
                using var failure = await SendLoginAsync(
                    host.Client,
                    UniqueEmail($"spray-target-{index}"),
                    WrongPassword,
                    attacker);
                await AssertGenericUnauthorizedAsync(failure);
            }

            var thresholdEmail = UniqueEmail("spray-threshold-target");
            using var threshold = await SendLoginAsync(
                host.Client,
                thresholdEmail,
                WrongPassword,
                attacker,
                origin: AllowedOrigin);
            await AssertThrottleProblemAsync(threshold, 60, thresholdEmail, attacker);
            AssertRetryAfterIsCorsExposed(threshold);

            using var blockedCorrectPassword = await SendLoginAsync(
                host.Client,
                victimEmail,
                Password,
                attacker);
            await AssertThrottleProblemAsync(
                blockedCorrectPassword,
                60,
                victimEmail,
                attacker);

            using var isolated = await SendLoginAsync(
                host.Client,
                victimEmail,
                Password,
                client: "spray-isolated-client");
            Assert.Equal(HttpStatusCode.OK, isolated.StatusCode);
        });
    }

    [Fact]
    public async Task Forwarded_for_is_ignored_from_untrusted_peers_and_partitioned_for_trusted_proxy()
    {
        var untrustedSettings = CreateSettings();
        untrustedSettings["AuthenticationAbuse:LoginIpPermitLimit"] = "1";
        await RunWithProductionPartitionerAsync(
            untrustedSettings,
            IPAddress.Parse("192.0.2.10"),
            async host =>
            {
                using var first = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("untrusted-first"),
                    WrongPassword,
                    forwardedFor: "198.51.100.10");
                await AssertGenericUnauthorizedAsync(first);

                using var spoofed = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("untrusted-spoofed"),
                    WrongPassword,
                    forwardedFor: "198.51.100.11");
                await AssertThrottleProblemAsync(spoofed, 60, "198.51.100.11");
            });

        var trustedSettings = CreateSettings();
        trustedSettings["AuthenticationAbuse:LoginIpPermitLimit"] = "1";
        trustedSettings["AuthenticationAbuse:TrustedProxyNetworks:0"] = "192.0.2.0/24";
        await RunWithProductionPartitionerAsync(
            trustedSettings,
            IPAddress.Parse("192.0.2.10"),
            async host =>
            {
                using var firstClient = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("trusted-first"),
                    WrongPassword,
                    forwardedFor: "198.51.100.20");
                await AssertGenericUnauthorizedAsync(firstClient);

                using var secondClient = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("trusted-second"),
                    WrongPassword,
                    forwardedFor: "198.51.100.21");
                await AssertGenericUnauthorizedAsync(secondClient);

                using var repeatedFirstClient = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("trusted-repeat"),
                    WrongPassword,
                    forwardedFor: "198.51.100.20");
                await AssertThrottleProblemAsync(
                    repeatedFirstClient,
                    60,
                    "198.51.100.20");

                using var malformedChain = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("trusted-malformed-chain"),
                    WrongPassword,
                    forwardedFor: "198.51.100.22, 198.51.100.23");
                await AssertGenericUnauthorizedAsync(malformedChain);

                using var changedMalformedChain = await SendLoginAsync(
                    host.Client,
                    UniqueEmail("trusted-changed-chain"),
                    WrongPassword,
                    forwardedFor: "198.51.100.24, 198.51.100.25");
                await AssertThrottleProblemAsync(
                    changedMalformedChain,
                    60,
                    "198.51.100.24",
                    "198.51.100.25");
            });
    }

    private Task RunWithPartitionedHostAsync(
        IReadOnlyDictionary<string, string?> settings,
        Func<IntegrationTestHost, Task> operation) =>
        fixture.RunWithHostAsync(
            fixture.DefaultWorkerBaseUrl,
            operation,
            settings,
            services =>
            {
                services.RemoveAll<IAuthenticationPartitionKeyProvider>();
                services.AddSingleton<TestAuthenticationPartitionKeyProvider>();
                services.AddSingleton<IAuthenticationPartitionKeyProvider>(serviceProvider =>
                    serviceProvider.GetRequiredService<
                        TestAuthenticationPartitionKeyProvider>());
                services.AddSingleton<AuthenticationCredentialGate>();
                services.AddSingleton<AuthenticationBodyReadProbe>();
                services.AddSingleton<
                    IStartupFilter,
                    AuthenticationBodyReadProbeStartupFilter>();
                services.RemoveAll<IIdentityCredentialVerifier>();
                services.AddScoped<IdentityCredentialVerifier>();
                services.AddScoped<
                    IIdentityCredentialVerifier,
                    GatedIdentityCredentialVerifier>();
            });

    private Task RunWithProductionPartitionerAsync(
        IReadOnlyDictionary<string, string?> settings,
        IPAddress remoteIpAddress,
        Func<IntegrationTestHost, Task> operation) =>
        fixture.RunWithHostAsync(
            fixture.DefaultWorkerBaseUrl,
            operation,
            settings,
            services => services.AddSingleton<IStartupFilter>(
                new FixedRemoteIpAddressStartupFilter(remoteIpAddress)));

    private static Dictionary<string, string?> CreateSettings() => new()
    {
        ["Startup:ApplyDatabaseMigrations"] = "false",
        ["AuthenticationAbuse:ApiReplicaCount"] = "1",
        ["AuthenticationAbuse:IdentityMaxFailedAccessAttempts"] = "2",
        ["AuthenticationAbuse:IdentityLockoutSeconds"] = "60",
        ["AuthenticationAbuse:LoginIpPermitLimit"] = "1000",
        ["AuthenticationAbuse:LoginIpWindowSeconds"] = "60",
        ["AuthenticationAbuse:RegistrationIpPermitLimit"] = "1000",
        ["AuthenticationAbuse:RegistrationIpWindowSeconds"] = "60",
        ["AuthenticationAbuse:LoginAccountPermitLimit"] = "1000",
        ["AuthenticationAbuse:LoginAccountWindowSeconds"] = "60",
        ["AuthenticationAbuse:RegistrationAccountPermitLimit"] = "1000",
        ["AuthenticationAbuse:RegistrationAccountWindowSeconds"] = "60",
        ["AuthenticationAbuse:PasswordSprayDistinctAccountLimit"] = "1000",
        ["AuthenticationAbuse:PasswordSprayWindowSeconds"] = "60",
        ["AuthenticationAbuse:PasswordSprayBlockSeconds"] = "60",
        ["AuthenticationAbuse:MaximumTrackedPartitions"] = "100000",
        ["AuthenticationAbuse:MaximumConcurrentAuthenticationRequests"] = "16",
        ["AuthenticationAbuse:AccountLockStripeCount"] = "64",
        ["AuthenticationAbuse:MaximumRetryAfterSeconds"] = "60"
    };

    private static async Task<HttpResponseMessage> SendLoginAsync(
        HttpClient httpClient,
        string email,
        string password,
        string? client = null,
        string? origin = null,
        string? forwardedFor = null,
        bool observeBodyReads = false,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        AddOptionalHeader(request, TestAuthenticationPartitionKeyProvider.ClientHeaderName, client);
        AddOptionalHeader(request, "Origin", origin);
        AddOptionalHeader(request, "X-Forwarded-For", forwardedFor);
        if (observeBodyReads)
        {
            request.Headers.TryAddWithoutValidation(AuthenticationBodyReadProbe.HeaderName, "1");
        }

        return await httpClient.SendAsync(
            request,
            cancellationToken == default
                ? TestContext.Current.CancellationToken
                : cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendRegistrationAsync(
        HttpClient httpClient,
        string email,
        string client,
        bool observeBodyReads = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email,
                password = Password,
                name = "Abuse Integration User"
            })
        };
        AddOptionalHeader(request, TestAuthenticationPartitionKeyProvider.ClientHeaderName, client);
        if (observeBodyReads)
        {
            request.Headers.TryAddWithoutValidation(AuthenticationBodyReadProbe.HeaderName, "1");
        }

        return await httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task DrainAuthenticationRequestAsync(
        Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected cleanup path for a held authentication request.
        }
    }

    private static async Task<HttpResponseMessage> SendMalformedLoginAsync(
        HttpClient httpClient,
        string client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent("{\"email\":", Encoding.UTF8, "application/json")
        };
        AddOptionalHeader(request, TestAuthenticationPartitionKeyProvider.ClientHeaderName, client);
        return await httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAuthenticationJsonAsync(
        HttpClient httpClient,
        string path,
        string json,
        string client)
        => await SendAuthenticationContentAsync(
            httpClient,
            path,
            new StringContent(json, Encoding.UTF8, "application/json"),
            client);

    private static async Task<HttpResponseMessage> SendAuthenticationContentAsync(
        HttpClient httpClient,
        string path,
        HttpContent content,
        string client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };
        AddOptionalHeader(request, TestAuthenticationPartitionKeyProvider.ClientHeaderName, client);
        return await httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AddOptionalHeader(
        HttpRequestMessage request,
        string name,
        string? value)
    {
        if (value is not null)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static async Task<string> AssertGenericUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("message", property.Name);
        Assert.Equal("Invalid email or password", property.Value.GetString());
        return body;
    }

    private static async Task AssertThrottleProblemAsync(
        HttpResponseMessage response,
        int maximumRetryAfterSeconds,
        params string[] sensitiveValues)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var retryAfter = int.Parse(
            Assert.Single(response.Headers.GetValues("Retry-After")),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, maximumRetryAfterSeconds);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            ["code", "status", "title", "type"],
            document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemType,
            document.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemTitle,
            document.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemCode,
            document.RootElement.GetProperty("code").GetString());

        foreach (var sensitiveValue in sensitiveValues)
        {
            Assert.DoesNotContain(sensitiveValue, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertRetryAfterIsCorsExposed(HttpResponseMessage response)
    {
        var exposedHeaders = response.Headers
            .GetValues("Access-Control-Expose-Headers")
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries));
        Assert.Contains("Retry-After", exposedHeaders, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] CreateEmailsOnDistinctStripes(
        string prefix,
        int count,
        int stripeCount)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stripes = new HashSet<int>();
        var emails = new List<string>();
        for (var index = 0; emails.Count < count; index++)
        {
            var email = $"{prefix}-{suffix}-{index}@example.test";
            var normalized = email.ToUpperInvariant();
            var key = string.Concat(
                TestAuthenticationPartitionKeyProvider.AccountKeyPrefix,
                normalized);
            var stripe = (int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)stripeCount);
            if (stripes.Add(stripe))
            {
                emails.Add(email);
            }
        }

        return [.. emails];
    }

    private static async Task<int> CountUsersAsync(IntegrationTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await userManager.Users.CountAsync(TestContext.Current.CancellationToken);
    }

    private static string OverlongEmail()
    {
        const string suffix = "@example.test";
        return string.Concat(
            new string(
                'a',
                AuthenticationInputLimits.EmailMaxLength - suffix.Length + 1),
            suffix);
    }

    private static string UniqueEmail(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static string FormatIdentityErrors(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));

    private sealed class HttpResponseBatch(HttpResponseMessage[] responses) : IDisposable
    {
        public IReadOnlyList<HttpResponseMessage> Messages { get; } = responses;

        public static async Task<HttpResponseBatch> WhenAllAsync(
            IEnumerable<Task<HttpResponseMessage>> responseTasks)
        {
            var tasks = responseTasks.ToArray();
            try
            {
                return new HttpResponseBatch(await Task.WhenAll(tasks));
            }
            catch
            {
                foreach (var task in tasks.Where(task => task.IsCompletedSuccessfully))
                {
                    task.Result.Dispose();
                }

                throw;
            }
        }

        public void Dispose()
        {
            foreach (var response in Messages)
            {
                response.Dispose();
            }
        }
    }

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _payload;

        public UnknownLengthJsonContent(string json)
        {
            _payload = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = Encoding.UTF8.WebName
            };
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(_payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
