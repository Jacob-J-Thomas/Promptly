using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Promptly.Application.Data;
using Promptly.Domain.Entities;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class IdentityCredentialVerifierTests
{
    private static readonly DateTimeOffset VerificationTime =
        new(2026, 8, 12, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task Missing_user_runs_dummy_password_work_and_returns_generic_failure()
    {
        await using var harness = CreateHarness();

        var user = await harness.Verifier.VerifyAsync(
            "missing@example.test",
            "WrongPassword1",
            TestContext.Current.CancellationToken);

        Assert.Null(user);
        Assert.Equal(1, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public async Task Wrong_password_records_a_durable_failure_without_dummy_work()
    {
        await using var harness = CreateHarness();
        var expected = await harness.CreateUserAsync();

        var user = await harness.Verifier.VerifyAsync(
            expected.Email!,
            "WrongPassword1",
            TestContext.Current.CancellationToken);

        Assert.Null(user);
        Assert.Equal(0, harness.InvalidVerifier.CallCount);
        Assert.Equal(1, await harness.UserManager.GetAccessFailedCountAsync(expected));
    }

    [Fact]
    public async Task Completed_wrong_password_returns_for_accounting_even_when_verification_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        await using var harness = CreateHarness(afterPasswordVerification: cancellation.Cancel);
        var expected = await harness.CreateUserAsync();

        var user = await harness.Verifier.VerifyAsync(
            expected.Email!,
            "WrongPassword1",
            cancellation.Token);

        Assert.Null(user);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, harness.InvalidVerifier.CallCount);
        Assert.Equal(1, await harness.UserManager.GetAccessFailedCountAsync(expected));
    }

    [Fact]
    public async Task Threshold_crossing_wrong_password_does_not_repeat_dummy_password_work()
    {
        await using var harness = CreateHarness(maxFailedAccessAttempts: 1);
        var expected = await harness.CreateUserAsync();

        var user = await harness.Verifier.VerifyAsync(
            expected.Email!,
            "WrongPassword1",
            TestContext.Current.CancellationToken);

        Assert.Null(user);
        Assert.True(await harness.UserManager.IsLockedOutAsync(expected));
        Assert.Equal(0, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public async Task Already_locked_user_runs_dummy_password_work_and_remains_generic()
    {
        await using var harness = CreateHarness(maxFailedAccessAttempts: 1);
        var expected = await harness.CreateUserAsync();
        Assert.Null(await harness.Verifier.VerifyAsync(
            expected.Email!,
            "WrongPassword1",
            TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.InvalidVerifier.CallCount);

        var user = await harness.Verifier.VerifyAsync(
            expected.Email!,
            IdentityHarness.Password,
            TestContext.Current.CancellationToken);

        Assert.Null(user);
        Assert.True(await harness.UserManager.IsLockedOutAsync(expected));
        Assert.Equal(1, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public async Task Not_allowed_user_runs_dummy_password_work_and_remains_generic()
    {
        await using var harness = CreateHarness(requireConfirmedEmail: true);
        var expected = await harness.CreateUserAsync();

        var user = await harness.Verifier.VerifyAsync(
            expected.Email!,
            IdentityHarness.Password,
            TestContext.Current.CancellationToken);

        Assert.Null(user);
        Assert.Equal(1, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public async Task Successful_login_resets_failures_then_records_the_injected_time()
    {
        await using var harness = CreateHarness();
        var expected = await harness.CreateUserAsync();
        Assert.Null(await harness.Verifier.VerifyAsync(
            expected.Email!,
            "WrongPassword1",
            TestContext.Current.CancellationToken));

        var actual = await harness.Verifier.VerifyAsync(
            expected.Email!,
            IdentityHarness.Password,
            TestContext.Current.CancellationToken);

        var authenticated = Assert.IsType<User>(actual);
        Assert.Same(expected, authenticated);
        Assert.Equal(0, await harness.UserManager.GetAccessFailedCountAsync(expected));
        Assert.Equal(VerificationTime.UtcDateTime, authenticated.LastLoginAt);
        var persisted = await harness.DbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == expected.Id, TestContext.Current.CancellationToken);
        Assert.Equal(VerificationTime.UtcDateTime, persisted.LastLoginAt);
        Assert.Equal(0, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public async Task Completed_successful_password_honors_cancellation_before_login_persistence()
    {
        using var cancellation = new CancellationTokenSource();
        await using var harness = CreateHarness(afterPasswordVerification: cancellation.Cancel);
        var expected = await harness.CreateUserAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Verifier.VerifyAsync(
                expected.Email!,
                IdentityHarness.Password,
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(expected.LastLoginAt);
        var persisted = await harness.DbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == expected.Id, TestContext.Current.CancellationToken);
        Assert.Null(persisted.LastLoginAt);
        Assert.Equal(0, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public async Task Invalid_arguments_and_pre_cancelled_requests_fail_before_identity_work()
    {
        await using var harness = CreateHarness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Verifier.VerifyAsync(
            " ",
            "password",
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Verifier.VerifyAsync(
            "user@example.test",
            null!,
            CancellationToken.None));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Verifier.VerifyAsync(
                "user@example.test",
                "password",
                cancellationSource.Token));

        Assert.Equal(0, harness.InvalidVerifier.CallCount);
    }

    [Fact]
    public void Invalid_credential_verifier_hashes_arbitrary_inputs_and_rejects_nulls()
    {
        var options = Options.Create(new PasswordHasherOptions
        {
            IterationCount = 1
        });
        var verifier = new InvalidCredentialPasswordVerifier(options);

        verifier.Verify("attacker-controlled-password");

        Assert.Throws<ArgumentNullException>(() => verifier.Verify(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new InvalidCredentialPasswordVerifier(null!));
    }

    private static IdentityHarness CreateHarness(
        int maxFailedAccessAttempts = 5,
        bool requireConfirmedEmail = false,
        Action? afterPasswordVerification = null) =>
        new(maxFailedAccessAttempts, requireConfirmedEmail, afterPasswordVerification);

    private sealed class IdentityHarness : IAsyncDisposable
    {
        public const string Password = "CorrectPassword1";

        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        public IdentityHarness(
            int maxFailedAccessAttempts,
            bool requireConfirmedEmail,
            Action? afterPasswordVerification)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<PromptlyDbContext>(options =>
                options.UseInMemoryDatabase($"identity-credential-{Guid.NewGuid():N}"));
            services.AddIdentity<User, IdentityRole>(options =>
                {
                    options.Lockout.AllowedForNewUsers = true;
                    options.Lockout.MaxFailedAccessAttempts = maxFailedAccessAttempts;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                    options.SignIn.RequireConfirmedEmail = requireConfirmedEmail;
                    options.Password.RequireNonAlphanumeric = false;
                })
                .AddEntityFrameworkStores<PromptlyDbContext>();
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1);
            if (afterPasswordVerification is not null)
            {
                services.AddScoped<IPasswordHasher<User>>(serviceProvider =>
                    new CallbackPasswordHasher(
                        serviceProvider.GetRequiredService<IOptions<PasswordHasherOptions>>(),
                        afterPasswordVerification));
            }
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(VerificationTime));
            InvalidVerifier = new RecordingInvalidCredentialVerifier();
            services.AddSingleton<IInvalidCredentialPasswordVerifier>(InvalidVerifier);
            services.AddScoped<IIdentityCredentialVerifier, IdentityCredentialVerifier>();

            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            _scope = _provider.CreateAsyncScope();
            UserManager = _scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Verifier = _scope.ServiceProvider.GetRequiredService<IIdentityCredentialVerifier>();
            DbContext = _scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        }

        public RecordingInvalidCredentialVerifier InvalidVerifier { get; }

        public UserManager<User> UserManager { get; }

        public IIdentityCredentialVerifier Verifier { get; }

        public PromptlyDbContext DbContext { get; }

        public async Task<User> CreateUserAsync()
        {
            var email = $"credential-{Guid.NewGuid():N}@example.test";
            var user = new User
            {
                UserName = email,
                Email = email,
                EmailConfirmed = false
            };
            var result = await UserManager.CreateAsync(user, Password);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class RecordingInvalidCredentialVerifier : IInvalidCredentialPasswordVerifier
    {
        public int CallCount { get; private set; }

        public void Verify(string password)
        {
            Assert.NotNull(password);
            CallCount++;
        }
    }

    private sealed class CallbackPasswordHasher(
        IOptions<PasswordHasherOptions> options,
        Action afterVerification) : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> _inner = new(options);

        public string HashPassword(User user, string password) =>
            _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            User user,
            string hashedPassword,
            string providedPassword)
        {
            var result = _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            afterVerification();
            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
