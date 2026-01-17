using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IJwtService
{
    string GenerateToken(User user);
}
