using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Promptly.Domain.Entities;

namespace Promptly.Server.Security;

public interface IIdentityCredentialVerifier
{
    Task<User?> VerifyAsync(
        string email,
        string password,
        CancellationToken cancellationToken);
}

public interface IInvalidCredentialPasswordVerifier
{
    void Verify(string password);
}

public sealed class IdentityCredentialVerifier : IIdentityCredentialVerifier
{
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IInvalidCredentialPasswordVerifier _invalidCredentialVerifier;
    private readonly TimeProvider _timeProvider;

    public IdentityCredentialVerifier(
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IInvalidCredentialPasswordVerifier invalidCredentialVerifier,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _invalidCredentialVerifier = invalidCredentialVerifier;
        _timeProvider = timeProvider;
    }

    public async Task<User?> VerifyAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);
        cancellationToken.ThrowIfCancellationRequested();

        var user = await _userManager.FindByEmailAsync(email);
        cancellationToken.ThrowIfCancellationRequested();
        if (user is null)
        {
            _invalidCredentialVerifier.Verify(password);
            return null;
        }

        var isLockedOut = await _userManager.IsLockedOutAsync(user);
        var canSignIn = await _signInManager.CanSignInAsync(user);
        cancellationToken.ThrowIfCancellationRequested();
        if (isLockedOut || !canSignIn)
        {
            // Precluded accounts still perform one password-hash-shaped unit of
            // work so missing, locked, and not-allowed failures share one generic
            // response and comparable credential-verification cost.
            _invalidCredentialVerifier.Verify(password);
            return null;
        }

        var result = await _signInManager.CheckPasswordSignInAsync(
            user,
            password,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            // The account can cross its lockout threshold after the real hash.
            // Do not repeat dummy work for that newly locked result.
            return null;
        }

        // A completed negative result must reach the controller so it can update
        // request-level spray accounting before cancellation is observed. A
        // successful result has no failure state to preserve and remains
        // cancellable before any successful-login persistence occurs.
        cancellationToken.ThrowIfCancellationRequested();

        user.LastLoginAt = _timeProvider.GetUtcNow().UtcDateTime;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException("Failed to persist the successful login");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return user;
    }
}

public sealed class InvalidCredentialPasswordVerifier : IInvalidCredentialPasswordVerifier
{
    private readonly User _dummyUser = new()
    {
        Id = "invalid-credential",
        UserName = "invalid-credential"
    };
    private readonly PasswordHasher<User> _passwordHasher;
    private readonly string _dummyPasswordHash;

    public InvalidCredentialPasswordVerifier(IOptions<PasswordHasherOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _passwordHasher = new PasswordHasher<User>(options);
        _dummyPasswordHash = _passwordHasher.HashPassword(
            _dummyUser,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Verify(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _ = _passwordHasher.VerifyHashedPassword(_dummyUser, _dummyPasswordHash, password);
    }
}
