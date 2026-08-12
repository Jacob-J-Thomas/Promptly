using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Server.Security;
using Promptly.Server.Services;

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

    [Fact]
    public void Worker_claim_and_process_load_share_all_execution_invariants()
    {
        var invariant = typeof(ExecutableRunQueries).GetMethod(
            nameof(ExecutableRunQueries.WhereExecutionGraphIsValid))!;
        var targetPolicy = typeof(EndpointTargetPolicy).GetMethod(
            nameof(EndpointTargetPolicy.TryResolve))!;
        var claim = typeof(TestRunWorkerStore).GetMethod(
            nameof(TestRunWorkerStore.ClaimNextQueuedRunAsync))!;
        var processLoad = typeof(TestRunWorkerStore).GetMethod(
            nameof(TestRunWorkerStore.LoadRunForProcessingAsync))!;
        var sharedValidation = typeof(TestRunWorkerStore).GetMethod(
            "ValidateRunForExecutionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        AssertCallsMethod(claim, sharedValidation);
        AssertCallsMethod(processLoad, sharedValidation);
        AssertCallsMethod(sharedValidation, invariant);
        AssertCallsMethod(sharedValidation, targetPolicy);
    }

    [Fact]
    public void Executable_run_query_rejects_a_null_source()
    {
        IQueryable<TestRun> source = null!;

        Assert.Throws<ArgumentNullException>(() =>
            ExecutableRunQueries.WhereExecutionGraphIsValid(source));
    }

    [Fact]
    public void Worker_disposes_claim_scope_before_opening_processing_scope()
    {
        var processNextRun = typeof(TestRunWorkerService).GetMethod(
            "ProcessNextRunAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var createScope = typeof(ServiceProviderServiceExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(ServiceProviderServiceExtensions.CreateScope)
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(IServiceProvider));

        Assert.Equal(2, CountCalls(processNextRun, createScope));
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
        var callsAccessor = CountCalls(action, ScopeAccessorMethod) > 0;

        Assert.True(
            callsAccessor,
            $"Tenant action {action.DeclaringType?.FullName}.{action.Name} does not call " +
            $"{nameof(ITenantAccessScopeAccessor.TryGetScope)}.");
    }

    private static void AssertCallsMethod(MethodInfo caller, MethodInfo callee)
    {
        Assert.True(
            CountCalls(caller, callee) > 0,
            $"{caller.DeclaringType?.FullName}.{caller.Name} does not call " +
            $"{callee.DeclaringType?.FullName}.{callee.Name}.");
    }

    private static int CountCalls(MethodInfo caller, MethodInfo callee)
    {
        var stateMachineType = caller.GetCustomAttribute<AsyncStateMachineAttribute>()
            ?.StateMachineType;
        var implementation = stateMachineType?.GetMethod(
            nameof(IAsyncStateMachine.MoveNext),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? caller;
        var il = implementation.GetMethodBody()?.GetILAsByteArray() ?? [];
        return Enumerable
            .Range(0, Math.Max(0, il.Length - sizeof(int)))
            .Count(index => IsCallTo(implementation, il, index, callee));
    }

    private static bool IsCallTo(
        MethodInfo implementation,
        byte[] il,
        int index,
        MethodInfo expected)
    {
        if (il[index] is not (0x28 or 0x6f))
        {
            return false;
        }

        try
        {
            var token = BitConverter.ToInt32(il, index + 1);
            var called = implementation.Module.ResolveMethod(token);
            return called is not null
                && called.DeclaringType == expected.DeclaringType
                && called.Name == expected.Name;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
