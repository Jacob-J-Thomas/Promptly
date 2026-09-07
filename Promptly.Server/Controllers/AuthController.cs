using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Server.Models;
using Promptly.Server.Security;

namespace Promptly.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<User> _userManager;
    private readonly IIdentityCredentialVerifier _credentialVerifier;
    private readonly IAuthenticationAbuseGuard _abuseGuard;
    private readonly IAuthenticationThrottleResponseWriter _throttleResponseWriter;
    private readonly IJwtService _jwtService;
    private readonly TimeProvider _timeProvider;

    public AuthController(
        UserManager<User> userManager,
        IIdentityCredentialVerifier credentialVerifier,
        IAuthenticationAbuseGuard abuseGuard,
        IAuthenticationThrottleResponseWriter throttleResponseWriter,
        IJwtService jwtService,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _credentialVerifier = credentialVerifier;
        _abuseGuard = abuseGuard;
        _throttleResponseWriter = throttleResponseWriter;
        _jwtService = jwtService;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Register a new user
    /// </summary>
    [HttpPost("register")]
    [RequestSizeLimit(AuthenticationInputLimits.RequestBodyMaxBytes)]
    [AuthenticationAbuseOperation(AuthenticationOperation.Registration)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await using var accountAttempt = await _abuseGuard.BeginAccountAttemptAsync(
            AuthenticationOperation.Registration,
            HttpContext,
            NormalizeAccount(request.Email),
            cancellationToken);
        if (!accountAttempt.IsAllowed)
        {
            return _throttleResponseWriter.CreateActionResult(accountAttempt.Decision);
        }

        var user = new User
        {
            UserName = request.Email,
            Email = request.Email,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
            return BadRequest(ModelState);
        }

        var token = _jwtService.GenerateToken(user);

        return Ok(new AuthResponse
        {
            Token = token,
            User = new UserInfo
            {
                Id = user.Id,
                Email = user.Email!,
                Name = request.Name
            }
        });
    }

    /// <summary>
    /// Login with email and password
    /// </summary>
    [HttpPost("login")]
    [RequestSizeLimit(AuthenticationInputLimits.RequestBodyMaxBytes)]
    [AuthenticationAbuseOperation(AuthenticationOperation.Login)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await using var accountAttempt = await _abuseGuard.BeginAccountAttemptAsync(
            AuthenticationOperation.Login,
            HttpContext,
            NormalizeAccount(request.Email),
            cancellationToken);
        if (!accountAttempt.IsAllowed)
        {
            return _throttleResponseWriter.CreateActionResult(accountAttempt.Decision);
        }

        var user = await _credentialVerifier.VerifyAsync(
            request.Email,
            request.Password,
            cancellationToken);
        if (user is null)
        {
            var sprayDecision = _abuseGuard.RecordLoginFailure(accountAttempt);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sprayDecision.IsAllowed)
            {
                return _throttleResponseWriter.CreateActionResult(sprayDecision);
            }

            return Unauthorized(new { message = "Invalid email or password" });
        }

        var token = _jwtService.GenerateToken(user);

        return Ok(new AuthResponse
        {
            Token = token,
            User = new UserInfo
            {
                Id = user.Id,
                Email = user.Email!,
                Name = user.UserName!
            }
        });
    }

    private string NormalizeAccount(string email) =>
        _userManager.NormalizeEmail(email) ?? email.ToUpperInvariant();
}
