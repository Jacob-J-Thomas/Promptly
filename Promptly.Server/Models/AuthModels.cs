using System.ComponentModel.DataAnnotations;

namespace Promptly.Server.Models;

public record RegisterRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    [MinLength(8)]
    public required string Password { get; init; }

    [Required]
    public required string Name { get; init; }
}

public record LoginRequest
{
    [Required]
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
