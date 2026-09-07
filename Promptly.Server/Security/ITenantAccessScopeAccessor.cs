using System.Diagnostics.CodeAnalysis;
using Promptly.Application.Models;

namespace Promptly.Server.Security;

public interface ITenantAccessScopeAccessor
{
    bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope);
}
