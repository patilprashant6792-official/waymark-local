using MudBlazor.Services;
using Waymark.Core.Auth;
using Waymark.Core.Edge;
using Waymark.Core.LiveFeed;
using Waymark.Core.Guides;
using Waymark.Core.Modules;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Data;
using Waymark.Data.Sqlite;
using Waymark.Host.Auth;
using Waymark.Host.Components;
using Waymark.Host.Edge;
using Waymark.Host.Mcp;
using Waymark.Host.Safety;
using Waymark.Logging;
using Waymark.Host.Ui;
using Waymark.Modules.Code;
using Waymark.Modules.Commands;
using Waymark.Modules.Files;
using Waymark.Modules.NuGet;

var builder = WebApplication.CreateBuilder(args);

// Two loopback listeners: the admin UI, and the one the tunnel forwards to (OAuth and MCP only).
var edge = EdgeOptions.FromConfiguration(builder.Configuration);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(edge.AdminPort);
    kestrel.ListenLocalhost(edge.McpPort);
});

builder.Services.AddSingleton(edge);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
var dataOptions = WaymarkDataOptions.FromConfiguration(builder.Configuration);
builder.Services.AddWaymarkData(dataOptions);
builder.Services.AddWaymarkWorkspaces(builder.Configuration);
builder.Services.AddWaymarkFileAccess(builder.Configuration);

builder.Services.AddWaymarkDataProtection(Path.Combine(Path.GetDirectoryName(dataOptions.DatabasePath)!, "keys"));
builder.Services.AddWaymarkEdge();
builder.Services.AddWaymarkAuth();
builder.Services.AddWaymarkUi();

// A background service that fails is logged (the Live page shows it under Errors) and the rest of Waymark keeps running.
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Live, memory-only view of tool calls, file changes, sign-in events and commands, plus errors from anywhere. Waymark's own
// categories are allowed from Information so those events pass the filter; everything else follows the usual levels.
builder.Logging.AddLiveLog();
builder.Logging.AddFilter<LiveLogProvider>("Waymark", LogLevel.Information);

IMcpModule[] modules = [new CodeModule(), new NuGetModule(), new FilesModule(), new CommandsModule()];
builder.Services.AddWaymarkTools(
    builder.Configuration,
    [typeof(WaymarkTools).Assembly, .. modules.Select(module => module.GetType().Assembly)]);

var mcp = builder.Services.AddMcpServer(options => options.ServerInstructions = GuideRules.ServerInstructions)
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<WaymarkTools>()
    .WithWaymarkToolPipeline();

foreach (var module in modules)
{
    module.AddServices(builder.Services, builder.Configuration);
    module.AddTools(mcp);
}

var app = builder.Build();
GlobalSafetyNet.Install(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Waymark.Failures"));

// Built now rather than on the first call: a module that has tools but no guide must stop the start-up, not surprise the first agent.
_ = app.Services.GetRequiredService<IGuideCatalog>();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);

// These start-up chores only refresh or tidy up, so a failure is reported and skipped. The database is different: without it nothing
// works, and a migration that fails must stop the app (see docs/migration/smoke.md), so its errors are not caught here.
await SoftAsync("Reading the public address", () => app.Services.GetRequiredService<IPublicUrlProvider>().RefreshAsync(app.Lifetime.ApplicationStopping));

// Expired codes and credentials are only kept for a week, so the tables stay small.
await SoftAsync(
    "Removing expired codes and credentials",
    () => app.Services.GetRequiredService<IGrantStore>().PruneExpiredAsync(
        app.Services.GetRequiredService<TimeProvider>().GetUtcNow() - TimeSpan.FromDays(7),
        app.Lifetime.ApplicationStopping));

static async Task SoftAsync(string what, Func<Task> work)
{
    try
    {
        await work();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        FailureReporter.Report(ex, what);
    }
}

app.Logger.LogInformation("Admin UI: http://localhost:{Port}", edge.AdminPort);
app.Logger.LogInformation("MCP endpoint: http://localhost:{Port}/mcp (Waymark starts the ngrok tunnel itself once an address is saved on the Setup page)", edge.McpPort);

static bool IsAdmin(HttpContext context) =>
    context.RequestServices.GetRequiredService<IListenerClassifier>().Classify(context) == ListenerKind.Admin;

app.UseForwardedHeaders();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ListenerGuard>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseWhen(IsAdmin, admin => admin.UseExceptionHandler("/Error", createScopeForErrors: true));
    app.UseWhen(context => !IsAdmin(context), tunnel => tunnel.UseExceptionHandler(errors => errors.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "server_error" });
    })));
}

// Friendly not-found pages belong to the admin UI only; OAuth and MCP clients must see the raw status codes.
app.UseWhen(IsAdmin, admin => admin.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

DiscoveryEndpoints.Map(app);
RegistrationEndpoint.Map(app);
AuthorizeEndpoints.Map(app);
GrantEndpoints.Map(app);

// Unauthenticated calls get 401 plus WWW-Authenticate with the metadata address (see BearerAuthHandler).
app.MapMcp("/mcp").RequireAuthorization(AuthPolicies.Mcp);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
