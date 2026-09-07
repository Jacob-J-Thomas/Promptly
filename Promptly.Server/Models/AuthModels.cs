using System.ComponentModel.DataAnnotations;

namespace Promptly.Server.Models;

public static class AuthenticationInputLimits
{
    public const int RequestBodyMaxBytes = 8 * 1024;
    public const int EmailMaxLength = 256;
    public const int PasswordMaxLength = 128;
    public const int NameMaxLength = 256;
}

public record RegisterRequest
{
    [Required]
    [StringLength(AuthenticationInputLimits.EmailMaxLength)]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    [StringLength(AuthenticationInputLimits.PasswordMaxLength, MinimumLength = 8)]
    public required string Password { get; init; }

    [Required]
    [StringLength(AuthenticationInputLimits.NameMaxLength)]
    public required string Name { get; init; }
}

public record LoginRequest
{
    [Required]
    [StringLength(AuthenticationInputLimits.EmailMaxLength)]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    public required string Password { get; init; }
}

public record AuthResponse
{
    public required string Token { get; init; }
    public required UserInfo User { get; init; }
}

public record UserInfo
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string Name { get; init; }
}
