using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Server.Controllers;
using Promptly.Server.Models;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthControllerTests
{
    private static readonly DateTimeOffset RequestTime =
        new(2026, 8, 12, 18, 30, 45, TimeSpan.Zero);

    [Fact]
    public async Task Register_rejects_invalid_model_state_before_account_or_identity_work()
    {
        await using var harness = CreateHarness();
        harness.Controller.ModelState.AddModelError(nameof(RegisterRequest.Email), "invalid");

        var result = await harness.Controller.Register(
            RegistrationRequest(),
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(harness.PartitionKeys.AccountInputs);
        Assert.Equal(0, harness.Credentials.CallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
        Assert.Empty(await harness.DbContext.Users.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_rejects_invalid_model_state_before_account_or_credential_work()
    {
        await using var harness = CreateHarness();
        harness.Controller.ModelState.AddModelError(
            nameof(Promptly.Server.Models.LoginRequest.Password),
            "required");

        var result = await harness.Controller.Login(
            LoginRequest(),
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(harness.PartitionKeys.AccountInputs);
        Assert.Equal(0, harness.Credentials.CallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
    }

    [Theory]
    [InlineData(nameof(RegisterRequest.Email))]
    [InlineData(nameof(RegisterRequest.Password))]
    [InlineData(nameof(RegisterRequest.Name))]
    public async Task Register_rejects_overlong_fields_before_account_or_identity_work(
        string memberName)
    {
        var request = memberName switch
        {
            nameof(RegisterRequest.Email) => RegistrationRequest(email: OverlongEmail()),
            nameof(RegisterRequest.Password) => RegistrationRequest(
                password: new string('a', AuthenticationInputLimits.PasswordMaxLength + 1)),
            nameof(RegisterRequest.Name) => RegistrationRequest(
                name: new string('a', AuthenticationInputLimits.NameMaxLength + 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(memberName))
        };
        var validationErrors = Validate(request);
        Assert.Contains(
            validationErrors,
            error => error.MemberNames.Contains(memberName, StringComparer.Ordinal));
        await using var harness = CreateHarness();
        AddValidationErrors(harness.Controller, validationErrors);

        var result = await harness.Controller.Register(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(harness.PartitionKeys.AccountInputs);
        Assert.Equal(0, harness.Credentials.CallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
        Assert.Empty(await harness.DbContext.Users.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_rejects_overlong_email_before_account_or_credential_work()
    {
        var request = LoginRequest(email: OverlongEmail());
        var validationErrors = Validate(request);
        Assert.Contains(
            validationErrors,
            error => error.MemberNames.Contains(
                nameof(Promptly.Server.Models.LoginRequest.Email),
                StringComparer.Ordinal));
        await using var harness = CreateHarness();
        AddValidationErrors(harness.Controller, validationErrors);

        var result = await harness.Controller.Login(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(harness.PartitionKeys.AccountInputs);
        Assert.Equal(0, harness.Credentials.CallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
    }

    [Fact]
    public void Login_accepts_legacy_password_above_the_registration_limit()
    {
        var legacyPassword = new string(
            'a',
            AuthenticationInputLimits.PasswordMaxLength + 1);

        Assert.Empty(Validate(LoginRequest(password: legacyPassword)));
    }

    [Fact]
    public void Authentication_models_accept_values_at_the_documented_limits()
    {
        const string emailSuffix = "@example.test";
        var email = string.Concat(
            new string('a', AuthenticationInputLimits.EmailMaxLength - emailSuffix.Length),
            emailSuffix);
        var password = string.Concat(
            "A1",
            new string('a', AuthenticationInputLimits.PasswordMaxLength - 2));
        var registration = RegistrationRequest(
            email,
            new string('a', AuthenticationInputLimits.NameMaxLength),
            password);

        Assert.Empty(Validate(registration));
        Assert.Empty(Validate(LoginRequest(email, password)));
    }

    [Theory]
    [InlineData(nameof(AuthController.Register))]
    [InlineData(nameof(AuthController.Login))]
    public void Authentication_actions_publish_the_request_body_limit(string actionName)
    {
        var action = typeof(AuthController).GetMethod(actionName);
        Assert.NotNull(action);
        var requestLimit = Assert.Single(
            action.GetCustomAttributes<RequestSizeLimitAttribute>());

        Assert.Equal(
            AuthenticationInputLimits.RequestBodyMaxBytes,
            ((IRequestSizeLimitMetadata)requestLimit).MaxRequestBodySize);
        Assert.Contains(
            action.GetCustomAttributes<ProducesResponseTypeAttribute>(),
            response => response.StatusCode == StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Register_returns_the_stable_throttle_result_before_a_duplicate_create()
    {
        await using var harness = CreateHarness(options =>
            options.RegistrationAccountPermitLimit = 1);
        var request = RegistrationRequest();

        Assert.IsType<OkObjectResult>(await harness.Controller.Register(
            request,
            TestContext.Current.CancellationToken));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await harness.Controller.Register(request, timeout.Token);

        var throttled = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        var decision = Assert.Single(harness.ThrottleWriter.Decisions);
        Assert.False(decision.IsAllowed);
        Assert.True(decision.RetryAfterSeconds > 0);
        Assert.Equal(1, await harness.DbContext.Users.CountAsync(timeout.Token));
        Assert.Equal(1, harness.Jwt.CallCount);
    }

    [Fact]
    public async Task Login_returns_the_stable_throttle_result_before_rechecking_credentials()
    {
        var authenticated = AuthenticatedUser();
        await using var harness = CreateHarness(options =>
        {
            options.IdentityMaxFailedAccessAttempts = 2;
            options.LoginAccountPermitLimit = 2;
        });
        harness.Credentials.Result = authenticated;
        var request = LoginRequest();

        Assert.IsType<OkObjectResult>(await harness.Controller.Login(
            request,
            TestContext.Current.CancellationToken));
        Assert.IsType<OkObjectResult>(await harness.Controller.Login(
            request,
            TestContext.Current.CancellationToken));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await harness.Controller.Login(request, timeout.Token);

        var throttled = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        Assert.Single(harness.ThrottleWriter.Decisions);
        Assert.Equal(2, harness.Credentials.CallCount);
        Assert.Equal(2, harness.Jwt.CallCount);
    }

    [Fact]
    public async Task Register_aggregates_all_identity_errors_and_releases_the_account_stripe()
    {
        var firstError = new IdentityError { Code = "first", Description = "first failure" };
        var secondError = new IdentityError { Code = "second", Description = "second failure" };
        await using var harness = CreateHarness(
            userValidator: new FailingUserValidator(firstError, secondError));
        var request = RegistrationRequest();

        var result = await harness.Controller.Register(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        var errors = harness.Controller.ModelState[string.Empty]!.Errors;
        Assert.Equal([firstError.Description, secondError.Description],
            errors.Select(error => error.ErrorMessage));
        Assert.Equal(0, harness.Jwt.CallCount);
        Assert.Empty(await harness.DbContext.Users.ToListAsync(TestContext.Current.CancellationToken));
        await AssertStripeCanBeReacquiredAsync(
            harness,
            request.Email,
            AuthenticationOperation.Login);
    }

    [Fact]
    public async Task Register_persists_the_injected_time_and_returns_the_generated_token()
    {
        await using var harness = CreateHarness();
        var request = RegistrationRequest(email: "New.User@example.test", name: "New User");

        var result = await harness.Controller.Register(
            request,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal(RecordingJwtService.Token, response.Token);
        Assert.Equal(request.Email, response.User.Email);
        Assert.Equal(request.Name, response.User.Name);
        Assert.False(string.IsNullOrWhiteSpace(response.User.Id));
        var persisted = await harness.DbContext.Users.AsNoTracking().SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(RequestTime.UtcDateTime, persisted.CreatedAt);
        Assert.Equal(response.User.Id, persisted.Id);
        var tokenUser = Assert.Single(harness.Jwt.Users);
        Assert.Equal(response.User.Id, tokenUser.Id);
        Assert.Equal(RequestTime.UtcDateTime, tokenUser.CreatedAt);
        Assert.Equal("NEW.USER@EXAMPLE.TEST", Assert.Single(harness.PartitionKeys.AccountInputs));
        await AssertStripeCanBeReacquiredAsync(
            harness,
            request.Email,
            AuthenticationOperation.Login);
    }

    [Fact]
    public async Task Missing_and_wrong_credentials_share_one_generic_unauthorized_contract()
    {
        await using var harness = CreateHarness();
        var requests = new[]
        {
            LoginRequest("missing@example.test", "DoesNotExist1"),
            LoginRequest("known@example.test", "WrongPassword1")
        };

        foreach (var request in requests)
        {
            var result = await harness.Controller.Login(
                request,
                TestContext.Current.CancellationToken);

            var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(
                "{\"message\":\"Invalid email or password\"}",
                JsonSerializer.Serialize(unauthorized.Value));
        }

        Assert.Equal(2, harness.Credentials.CallCount);
        Assert.Equal(requests.Select(request => request.Email), harness.Credentials.Emails);
        Assert.Equal(requests.Select(request => request.Password), harness.Credentials.Passwords);
        Assert.Equal(2, harness.AbuseGuard.RecordFailureCallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
        Assert.Empty(harness.ThrottleWriter.Decisions);
    }

    [Fact]
    public async Task Spray_threshold_returns_the_same_stable_throttle_contract()
    {
        await using var harness = CreateHarness(options =>
            options.PasswordSprayDistinctAccountLimit = 2);

        var firstResult = await harness.Controller.Login(
            LoginRequest("first-spray-target@example.test"),
            TestContext.Current.CancellationToken);

        var result = await harness.Controller.Login(
            LoginRequest("second-spray-target@example.test"),
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedObjectResult>(firstResult);
        var throttled = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        var decision = Assert.Single(harness.ThrottleWriter.Decisions);
        Assert.False(decision.IsAllowed);
        Assert.True(decision.RetryAfterSeconds > 0);
        Assert.Equal(2, harness.Credentials.CallCount);
        Assert.Equal(2, harness.AbuseGuard.RecordFailureCallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
        await AssertStripeCanBeReacquiredAsync(
            harness,
            "second-spray-target@example.test",
            AuthenticationOperation.Registration);
    }

    [Fact]
    public async Task Login_success_returns_identity_data_and_generated_token()
    {
        var user = AuthenticatedUser();
        await using var harness = CreateHarness();
        harness.Credentials.Result = user;
        var request = LoginRequest("member@example.test", "CorrectPassword1");

        var result = await harness.Controller.Login(
            request,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal(RecordingJwtService.Token, response.Token);
        Assert.Equal(user.Id, response.User.Id);
        Assert.Equal(user.Email, response.User.Email);
        Assert.Equal(user.UserName, response.User.Name);
        Assert.Same(user, harness.Jwt.LastUser);
        Assert.Equal(request.Email, Assert.Single(harness.Credentials.Emails));
        Assert.Equal(request.Password, Assert.Single(harness.Credentials.Passwords));
        Assert.Equal(0, harness.AbuseGuard.RecordFailureCallCount);
        Assert.Empty(harness.ThrottleWriter.Decisions);
    }

    [Fact]
    public async Task Admitted_login_success_completes_when_spray_block_activates_during_verification()
    {
        await using var harness = CreateHarness(options =>
        {
            options.PasswordSprayDistinctAccountLimit = 2;
            options.AccountLockStripeCount = 16;
        });
        var request = LoginRequest("race-success@example.test", "CorrectPassword1");
        var requestAccount = harness.UserManager.NormalizeEmail(request.Email)!;
        var triggerAccount = FindAccountOnDifferentStripe(harness, requestAccount, 16);
        await using (var seedAttempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Controller.HttpContext,
            harness.UserManager.NormalizeEmail("race-seed@example.test")!,
            TestContext.Current.CancellationToken))
        {
            Assert.True(harness.Guard.RecordLoginFailure(seedAttempt).IsAllowed);
        }

        harness.Credentials.Behavior = async (_, _, cancellationToken) =>
        {
            await using var triggerAttempt = await harness.Guard.BeginAccountAttemptAsync(
                AuthenticationOperation.Login,
                harness.Controller.HttpContext,
                triggerAccount,
                cancellationToken);
            Assert.False(harness.Guard.RecordLoginFailure(triggerAttempt).IsAllowed);
            return AuthenticatedUser();
        };
        var admittedResult = await harness.Controller.Login(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(admittedResult);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subsequentResult = await harness.Controller.Login(request, timeout.Token);

        var throttled = Assert.IsType<StatusCodeResult>(subsequentResult);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        var decision = Assert.Single(harness.ThrottleWriter.Decisions);
        Assert.False(decision.IsAllowed);
        Assert.True(decision.RetryAfterSeconds > 0);
        Assert.Equal(1, harness.Credentials.CallCount);
        Assert.Equal(1, harness.Jwt.CallCount);
        await AssertStripeCanBeReacquiredAsync(
            harness,
            request.Email,
            AuthenticationOperation.Registration);
    }

    [Fact]
    public async Task Null_identity_normalization_falls_back_to_invariant_uppercase()
    {
        await using var harness = CreateHarness(useNullNormalizer: true);
        var request = LoginRequest("Fallback.User@example.test");

        var result = await harness.Controller.Login(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(
            "FALLBACK.USER@EXAMPLE.TEST",
            Assert.Single(harness.PartitionKeys.AccountInputs));
    }

    [Fact]
    public async Task Login_propagates_cancellation_to_credentials_and_releases_the_stripe()
    {
        await using var harness = CreateHarness();
        var credentialEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Credentials.Behavior = async (_, _, cancellationToken) =>
        {
            credentialEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };
        var request = LoginRequest("cancelled@example.test");
        using var cancellation = new CancellationTokenSource();

        var login = harness.Controller.Login(request, cancellation.Token);
        await credentialEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Equal(cancellation.Token, harness.Credentials.CancellationTokens.Single());
        await AssertStripeCanBeReacquiredAsync(
            harness,
            request.Email,
            AuthenticationOperation.Registration);
    }

    [Fact]
    public async Task Login_records_a_completed_wrong_password_before_honoring_cancellation()
    {
        await using var harness = CreateHarness(options =>
            options.PasswordSprayDistinctAccountLimit = 2);
        var primer = await harness.Controller.Login(
            LoginRequest("cancellation-primer@example.test"),
            TestContext.Current.CancellationToken);
        Assert.IsType<UnauthorizedObjectResult>(primer);

        using var cancellation = new CancellationTokenSource();
        harness.Credentials.Behavior = (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<User?>(null);
        };
        var request = LoginRequest("cancelled-after-verification@example.test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Controller.Login(request, cancellation.Token));

        Assert.Equal(2, harness.AbuseGuard.RecordFailureCallCount);
        Assert.Equal(2, harness.Credentials.CallCount);
        Assert.Equal(0, harness.Jwt.CallCount);

        harness.Credentials.Behavior = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var subsequent = await harness.Controller.Login(request, timeout.Token);

        var throttled = Assert.IsType<StatusCodeResult>(subsequent);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        Assert.Equal(2, harness.Credentials.CallCount);
    }

    [Fact]
    public async Task Precancelled_login_stops_while_waiting_for_the_account_stripe()
    {
        await using var harness = CreateHarness();
        var request = LoginRequest("contended@example.test");
        await using var heldAttempt = await harness.Guard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            harness.Controller.HttpContext,
            harness.UserManager.NormalizeEmail(request.Email)!,
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Controller.Login(request, cancellation.Token));

        Assert.Equal(0, harness.Credentials.CallCount);
        Assert.Equal(0, harness.Jwt.CallCount);
    }

    private static AuthHarness CreateHarness(
        Action<AuthenticationAbuseOptions>? configureOptions = null,
        IUserValidator<User>? userValidator = null,
        bool useNullNormalizer = false) =>
        new(configureOptions, userValidator, useNullNormalizer);

    private static RegisterRequest RegistrationRequest(
        string email = "new@example.test",
        string name = "New Member",
        string password = "ValidPassword1") =>
        new()
        {
            Email = email,
            Password = password,
            Name = name
        };

    private static LoginRequest LoginRequest(
        string email = "member@example.test",
        string password = "WrongPassword1") =>
        new()
        {
            Email = email,
            Password = password
        };

    private static User AuthenticatedUser() =>
        new()
        {
            Id = "authenticated-user",
            Email = "member@example.test",
            UserName = "Member Name"
        };

    private static string OverlongEmail()
    {
        const string suffix = "@example.test";
        return string.Concat(
            new string(
                'a',
                AuthenticationInputLimits.EmailMaxLength - suffix.Length + 1),
            suffix);
    }

    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(
            model,
            new ValidationContext(model),
            results,
            validateAllProperties: true);
        return results;
    }

    private static void AddValidationErrors(
        ControllerBase controller,
        IEnumerable<ValidationResult> validationErrors)
    {
        foreach (var validationError in validationErrors)
        {
            foreach (var memberName in validationError.MemberNames.DefaultIfEmpty(string.Empty))
            {
                controller.ModelState.AddModelError(
                    memberName,
                    validationError.ErrorMessage ?? "Invalid authentication input.");
            }
        }
    }

    private static async Task AssertStripeCanBeReacquiredAsync(
        AuthHarness harness,
        string email,
        AuthenticationOperation operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var attempt = await harness.Guard.BeginAccountAttemptAsync(
            operation,
            harness.Controller.HttpContext,
            harness.UserManager.NormalizeEmail(email) ?? email.ToUpperInvariant(),
            timeout.Token);
        Assert.True(attempt.IsAllowed);
    }

    private static string FindAccountOnDifferentStripe(
        AuthHarness harness,
        string account,
        int stripeCount)
    {
        var heldStripe = GetStripeIndex(harness.PartitionKeys.PeekAccountKey(account), stripeCount);
        for (var index = 0; ; index++)
        {
            var candidate = $"RACE-TRIGGER-{index}@EXAMPLE.TEST";
            if (GetStripeIndex(harness.PartitionKeys.PeekAccountKey(candidate), stripeCount)
                != heldStripe)
            {
                return candidate;
            }
        }
    }

    private static int GetStripeIndex(string accountKey, int stripeCount) =>
        (int)((uint)StringComparer.Ordinal.GetHashCode(accountKey) % (uint)stripeCount);

    private sealed class AuthHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        public AuthHarness(
            Action<AuthenticationAbuseOptions>? configureOptions,
            IUserValidator<User>? userValidator,
            bool useNullNormalizer)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<PromptlyDbContext>(options =>
                options.UseInMemoryDatabase($"auth-controller-{Guid.NewGuid():N}"));
            services.AddIdentity<User, IdentityRole>(options =>
                {
                    options.Password.RequireNonAlphanumeric = false;
                    options.User.RequireUniqueEmail = true;
                })
                .AddEntityFrameworkStores<PromptlyDbContext>();
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1);
            if (userValidator is not null)
            {
                services.AddSingleton(userValidator);
            }
            if (useNullNormalizer)
            {
                services.AddSingleton<ILookupNormalizer, NullLookupNormalizer>();
            }

            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            _scope = _provider.CreateAsyncScope();
            UserManager = _scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            DbContext = _scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();

            var options = new AuthenticationAbuseOptions
            {
                LoginIpPermitLimit = 100,
                LoginIpWindowSeconds = 60,
                RegistrationIpPermitLimit = 100,
                RegistrationIpWindowSeconds = 60,
                LoginAccountPermitLimit = 100,
                LoginAccountWindowSeconds = 60,
                RegistrationAccountPermitLimit = 100,
                RegistrationAccountWindowSeconds = 60,
                PasswordSprayDistinctAccountLimit = 100,
                PasswordSprayWindowSeconds = 60,
                PasswordSprayBlockSeconds = 60,
                MaximumTrackedPartitions = 1_000,
                AccountLockStripeCount = 16,
                MaximumRetryAfterSeconds = 60
            };
            configureOptions?.Invoke(options);
            AuthenticationAbuseOptionsValidator.GetValidatedSettings(options);

            PartitionKeys = new RecordingPartitionKeyProvider();
            Guard = new AuthenticationAbuseGuard(
                PartitionKeys,
                Options.Create(options),
                new FixedTimeProvider(RequestTime));
            AbuseGuard = new RecordingAuthenticationAbuseGuard(Guard);
            Credentials = new RecordingCredentialVerifier();
            ThrottleWriter = new RecordingThrottleResponseWriter();
            Jwt = new RecordingJwtService();
            Controller = new AuthController(
                UserManager,
                Credentials,
                AbuseGuard,
                ThrottleWriter,
                Jwt,
                new FixedTimeProvider(RequestTime))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        public UserManager<User> UserManager { get; }

        public PromptlyDbContext DbContext { get; }

        public RecordingPartitionKeyProvider PartitionKeys { get; }

        public AuthenticationAbuseGuard Guard { get; }

        public RecordingAuthenticationAbuseGuard AbuseGuard { get; }

        public RecordingCredentialVerifier Credentials { get; }

        public RecordingThrottleResponseWriter ThrottleWriter { get; }

        public RecordingJwtService Jwt { get; }

        public AuthController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            Guard.Dispose();
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class RecordingPartitionKeyProvider : IAuthenticationPartitionKeyProvider
    {
        public List<string> AccountInputs { get; } = [];

        public string GetClientKey(HttpContext httpContext)
        {
            Assert.NotNull(httpContext);
            return new string('a', 64);
        }

        public string GetAccountKey(string normalizedAccount)
        {
            AccountInputs.Add(normalizedAccount);
            return PeekAccountKey(normalizedAccount);
        }

        public string PeekAccountKey(string normalizedAccount) =>
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedAccount)));
    }

    private sealed class RecordingCredentialVerifier : IIdentityCredentialVerifier
    {
        public User? Result { get; set; }

        public Func<string, string, CancellationToken, Task<User?>>? Behavior { get; set; }

        public int CallCount => Emails.Count;

        public List<string> Emails { get; } = [];

        public List<string> Passwords { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<User?> VerifyAsync(
            string email,
            string password,
            CancellationToken cancellationToken)
        {
            Emails.Add(email);
            Passwords.Add(password);
            CancellationTokens.Add(cancellationToken);
            return Behavior?.Invoke(email, password, cancellationToken)
                ?? Task.FromResult(Result);
        }
    }

    private sealed class RecordingAuthenticationAbuseGuard(IAuthenticationAbuseGuard inner)
        : IAuthenticationAbuseGuard
    {
        public int RecordFailureCallCount { get; private set; }

        public AuthenticationThrottleDecision TryAcquireClient(
            AuthenticationOperation operation,
            HttpContext httpContext) =>
            inner.TryAcquireClient(operation, httpContext);

        public ValueTask<AuthenticationAccountAttempt> BeginAccountAttemptAsync(
            AuthenticationOperation operation,
            HttpContext httpContext,
            string normalizedAccount,
            CancellationToken cancellationToken) =>
            inner.BeginAccountAttemptAsync(
                operation,
                httpContext,
                normalizedAccount,
                cancellationToken);

        public AuthenticationThrottleDecision RecordLoginFailure(
            AuthenticationAccountAttempt attempt)
        {
            RecordFailureCallCount++;
            return inner.RecordLoginFailure(attempt);
        }

    }

    private sealed class RecordingThrottleResponseWriter : IAuthenticationThrottleResponseWriter
    {
        public List<AuthenticationThrottleDecision> Decisions { get; } = [];

        public Task WriteAsync(
            HttpContext httpContext,
            AuthenticationThrottleDecision decision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IActionResult CreateActionResult(AuthenticationThrottleDecision decision)
        {
            Decisions.Add(decision);
            return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
        }
    }

    private sealed class RecordingJwtService : IJwtService
    {
        public const string Token = "generated-token";

        public int CallCount => Users.Count;

        public List<User> Users { get; } = [];

        public User? LastUser => Users.LastOrDefault();

        public Action? BeforeGenerate { get; set; }

        public string GenerateToken(User user)
        {
            BeforeGenerate?.Invoke();
            Users.Add(user);
            return Token;
        }
    }

    private sealed class FailingUserValidator(params IdentityError[] errors)
        : IUserValidator<User>
    {
        public Task<IdentityResult> ValidateAsync(
            UserManager<User> manager,
            User user) =>
            Task.FromResult(IdentityResult.Failed(errors));
    }

    private sealed class NullLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeEmail(string? email) => null;

        public string? NormalizeName(string? name) => name?.ToUpperInvariant();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
