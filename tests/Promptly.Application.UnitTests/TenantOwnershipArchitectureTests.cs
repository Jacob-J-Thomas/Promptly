using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class TenantOwnershipArchitectureTests
{
    private static readonly MethodInfo ScopeAccessorMethod =
        typeof(ITenantAccessScopeAccessor).GetMethod(
            nameof(ITenantAccessScopeAccessor.TryGetScope))!;

    private static readonly HashSet<Type> ExplicitlyOwnerlessControllerContracts =
    [
        typeof(IPythonEvalClient),
        typeof(IYamlService)
    ];

    [Fact]
    public void Every_tenant_controller_service_operation_requires_scope()
    {
        var tenantServiceContracts = GetTenantControllers()
            .SelectMany(controller => controller.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsInterface
                && type.Namespace == typeof(IProjectService).Namespace
                && !ExplicitlyOwnerlessControllerContracts.Contains(type))
            .Distinct()
            .ToList();

        Assert.NotEmpty(tenantServiceContracts);
        foreach (var contract in tenantServiceContracts)
        {
            foreach (var method in contract.GetMethods())
            {
                if (IsExplicitlyOwnerlessMappingOperation(method))
                {
                    continue;
                }

                Assert.Equal(
                    typeof(TenantAccessScope),
                    method.GetParameters()[^1].ParameterType);
            }
        }
    }

    [Fact]
    public void Tenant_query_layer_covers_every_entity_exposed_by_scoped_services()
    {
        var expectedEntityTypes = GetTenantControllers()
            .SelectMany(controller => controller.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsInterface
                && type.Namespace == typeof(IProjectService).Namespace)
            .SelectMany(contract => contract.GetMethods())
            .Where(RequiresTenantScope)
            .SelectMany(method => GetDomainEntityTypes(method.ReturnType))
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
        var actualEntityTypes = typeof(TenantOwnedQueries)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(TenantOwnedQueries.ForTenant))
            .Select(method => method.GetParameters()[0].ParameterType.GetGenericArguments()[0])
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedEntityTypes);
        Assert.Equal(expectedEntityTypes, actualEntityTypes);
    }

    [Fact]
    public void Every_tenant_action_uses_scope_and_controllers_cannot_reach_storage_directly()
    {
        var controllers = GetTenantControllers();
        Assert.NotEmpty(controllers);

        foreach (var controller in controllers)
        {
            Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>(inherit: true));
            var constructor = Assert.Single(controller.GetConstructors());
            var dependencies = constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToList();

            Assert.Contains(typeof(ITenantAccessScopeAccessor), dependencies);
            Assert.DoesNotContain(typeof(PromptlyDbContext), dependencies);
            Assert.DoesNotContain(typeof(ITestRunWorkerStore), dependencies);
            Assert.DoesNotContain(
                controller.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => field.FieldType == typeof(PromptlyDbContext)
                    || field.FieldType == typeof(ITestRunWorkerStore));

            var actions = controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
                .ToList();
            Assert.NotEmpty(actions);
            Assert.All(actions, action =>
            {
                Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));
                AssertCallsScopeAccessor(action);
            });
        }
    }

    private static List<Type> GetTenantControllers() =>
        typeof(Program).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract
                && typeof(ControllerBase).IsAssignableFrom(type)
                && type.GetConstructors()
                    .SelectMany(constructor => constructor.GetParameters())
                    .Any(parameter => parameter.ParameterType == typeof(ITenantAccessScopeAccessor)
                        || parameter.ParameterType == typeof(PromptlyDbContext)
                        || parameter.ParameterType == typeof(ITestRunWorkerStore)
                        || IsTenantServiceContract(parameter.ParameterType)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    private static bool IsTenantServiceContract(Type type) =>
        type.IsInterface
        && type.Namespace == typeof(IProjectService).Namespace
        && !ExplicitlyOwnerlessControllerContracts.Contains(type)
        && type.GetMethods()
            .SelectMany(method => GetDomainEntityTypes(method.ReturnType))
            .Any();

    private static bool IsExplicitlyOwnerlessMappingOperation(MethodInfo method) =>
        method.DeclaringType == typeof(IMappingService)
        && method.Name is nameof(IMappingService.ApplyMappingAsync)
            or nameof(IMappingService.ValidateMappingAsync);

    private static bool RequiresTenantScope(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length > 0 && parameters[^1].ParameterType == typeof(TenantAccessScope);
    }

    private static IEnumerable<Type> GetDomainEntityTypes(Type type)
    {
        if (type.Namespace == typeof(Project).Namespace)
        {
            yield return type;
            yield break;
        }

        if (type.IsArray)
        {
            foreach (var entityType in GetDomainEntityTypes(type.GetElementType()!))
            {
                yield return entityType;
            }
        }

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var entityType in GetDomainEntityTypes(argument))
            {
                yield return entityType;
            }
        }
    }

    private static void AssertCallsScopeAccessor(MethodInfo action)
    {
        var stateMachineType = action.GetCustomAttribute<AsyncStateMachineAttribute>()
            ?.StateMachineType;
        var implementation = stateMachineType?.GetMethod(
            nameof(IAsyncStateMachine.MoveNext),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? action;
        var il = implementation.GetMethodBody()?.GetILAsByteArray() ?? [];
        var tokenBytes = BitConverter.GetBytes(ScopeAccessorMethod.MetadataToken);
        var callsAccessor = Enumerable
            .Range(0, Math.Max(0, il.Length - tokenBytes.Length))
            .Any(index => (il[index] == 0x28 || il[index] == 0x6f)
                && il.AsSpan(index + 1, tokenBytes.Length).SequenceEqual(tokenBytes));

        Assert.True(
            callsAccessor,
            $"Tenant action {action.DeclaringType?.FullName}.{action.Name} does not call " +
            $"{nameof(ITenantAccessScopeAccessor.TryGetScope)}.");
    }
}
