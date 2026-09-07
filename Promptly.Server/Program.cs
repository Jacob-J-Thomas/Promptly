using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Infrastructure.Clients;
using Promptly.Infrastructure.Configuration;
using Promptly.Infrastructure.Networking;
using Promptly.Application.Data;
using Promptly.Infrastructure.Security;
using Promptly.Infrastructure.Services;
using Promptly.Server.Security;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme."
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Add DbContext
builder.Services.AddDbContext<PromptlyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Configure authentication abuse controls before Identity so the persisted
// account-lockout policy and the request-level limits share one validated source.
var authenticationAbuseSection = builder.Configuration.GetSection(
    AuthenticationAbuseOptions.SectionName);
var authenticationAbuseSettings = AuthenticationAbuseOptionsValidator.GetValidatedSettings(
    authenticationAbuseSection.Get<AuthenticationAbuseOptions>()
        ?? new AuthenticationAbuseOptions());
builder.Services.AddOptions<AuthenticationAbuseOptions>()
    .Bind(authenticationAbuseSection)
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<AuthenticationAbuseOptions>,
    AuthenticationAbuseOptionsValidator>();

// Configure Identity
builder.Services.AddIdentity<User, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts =
        authenticationAbuseSettings.IdentityMaxFailedAccessAttempts;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromSeconds(
        authenticationAbuseSettings.IdentityLockoutSeconds);
})
.AddEntityFrameworkStores<PromptlyDbContext>()
.AddDefaultTokenProviders();

// Configure JWT
var jwtSection = builder.Configuration.GetSection("JWT");
var jwtSettings = jwtSection.Get<JwtSettings>() ?? new JwtSettings();
var jwtSigningKey = JwtSettingsValidator.GetValidatedSigningKeyBytes(jwtSettings);
builder.Services.AddOptions<JwtSettings>()
    .Bind(jwtSection)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JwtSettings>, JwtSettingsValidator>();
builder.Services.AddScoped<IJwtService, JwtService>();

// Configure Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = TenantAuthenticationSchemes.Policy;
    options.DefaultAuthenticateScheme = TenantAuthenticationSchemes.Policy;
    options.DefaultChallengeScheme = TenantAuthenticationSchemes.Policy;
})
.AddPolicyScheme(
    TenantAuthenticationSchemes.Policy,
    TenantAuthenticationSchemes.Policy,
    options => options.ForwardDefaultSelector = TenantAuthenticationSchemeSelector.Select)
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        AuthenticationType = TenantAuthenticationSchemes.JwtBearer,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(jwtSigningKey)
        {
            KeyId = JwtSettingsValidator.GetSigningKeyId(jwtSigningKey)
        }
    };
})
.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    TenantAuthenticationSchemes.ApiKey,
    null);

// Configure Authorization
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantAccessScopeAccessor, HttpContextTenantAccessScopeAccessor>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAuthenticationPartitionKeyProvider, AuthenticationPartitionKeyProvider>();
builder.Services.AddSingleton<IAuthenticationAbuseGuard, AuthenticationAbuseGuard>();
builder.Services.AddSingleton<
    IAuthenticationRequestAdmissionGate,
    AuthenticationRequestAdmissionGate>();
builder.Services.AddSingleton<
    IAuthenticationThrottleResponseWriter,
    AuthenticationThrottleResponseWriter>();
builder.Services.AddSingleton<IInvalidCredentialPasswordVerifier, InvalidCredentialPasswordVerifier>();
builder.Services.AddScoped<IIdentityCredentialVerifier, IdentityCredentialVerifier>();

// Configure Data Protection
var dataProtectionPath = builder.Configuration["DATA_PROTECTION_PATH"] ?? "./dataprotection-keys";
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("Promptly");

// Register application services
builder.Services.AddScoped<IEncryptionService, DataProtectionEncryptionService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IEnvironmentService, EnvironmentService>();
builder.Services.AddScoped<IEndpointService, EndpointService>();
builder.Services.AddScoped<IJsonPathService, JsonPathService>();
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<ITestSuiteService, TestSuiteService>();
builder.Services.AddScoped<ITestCaseService, TestCaseService>();
builder.Services.AddScoped<IYamlService, YamlService>();
builder.Services.AddScoped<ITestRunService, TestRunService>();
builder.Services.AddScoped<ITestRunWorkerStore, TestRunWorkerStore>();
builder.Services.AddOptions<EndpointEgressOptions>()
    .Bind(builder.Configuration.GetSection(EndpointEgressOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EndpointEgressOptions>, EndpointEgressOptionsValidator>();
builder.Services.AddSingleton<IDestinationAddressResolver, SystemDestinationAddressResolver>();
builder.Services.AddSingleton<IEndpointDestinationGuard, EndpointDestinationGuard>();
builder.Services.AddSingleton<IEndpointSocketConnector, EndpointSocketConnector>();
builder.Services.AddSingleton<EndpointDestinationConnector>();
builder.Services.AddScoped<IEndpointExecutor, EndpointExecutor>();
builder.Services.AddHttpClient(EndpointExecutor.HttpClientName, httpClient =>
    {
        // Explicitly cap endpoint traffic at HTTP/2. HTTP/3 uses QUIC and would bypass
        // the TCP ConnectCallback used by the direct transport.
        httpClient.DefaultRequestVersion = HttpVersion.Version20;
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    })
    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<EndpointEgressOptions>>().Value;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            EnableMultipleHttp2Connections = false,
            PooledConnectionLifetime = TimeSpan.FromSeconds(
                options.PooledConnectionLifetimeSeconds),
            UseCookies = false
        };

        if (!string.IsNullOrEmpty(options.ProxyUrl))
        {
            handler.Proxy = new WebProxy(new Uri(options.ProxyUrl))
            {
                BypassProxyOnLocal = false
            };
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
            handler.ConnectCallback = serviceProvider
                .GetRequiredService<EndpointDestinationConnector>()
                .ConnectAsync;
        }

        return handler;
    });
builder.Services.AddSingleton<IBoundedRegexMatcher, BoundedRegexMatcher>();
builder.Services.AddScoped<IExpectationEvaluator, ExpectationEvaluator>();
builder.Services.AddScoped<ITestRunProcessor, TestRunProcessor>();

// Register Python worker client
builder.Services.AddHttpClient<IPythonEvalClient, PythonEvalClient>();

// Register the background runner by default. Integration hosts can disable it
// explicitly so they exercise the real HTTP pipeline without racing queued work.
if (builder.Configuration.GetValue("TestRunner:Enabled", true))
{
    builder.Services.AddHostedService<Promptly.Server.Services.TestRunWorkerService>();
}

// Add CORS for local development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("Retry-After")
              .AllowCredentials();
    });
});

var app = builder.Build();

// Apply database migrations automatically at startup by default. The explicit
// switch lets specialized hosts own migration orchestration when required.
if (builder.Configuration.GetValue("Startup:ApplyDatabaseMigrations", true))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
    dbContext.Database.Migrate();
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors();
app.UseMiddleware<AuthenticationAbuseMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

public partial class Program;
