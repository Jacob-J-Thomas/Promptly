using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Promptly.Application.Models;

namespace Promptly.Server.Security;

public sealed class HttpContextTenantAccessScopeAccessor(
    IHttpContextAccessor httpContextAccessor) : ITenantAccessScopeAccessor
{
    public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope) =>
        TenantAccessScopeFactory.TryCreate(httpContextAccessor.HttpContext?.User, out scope);
}
