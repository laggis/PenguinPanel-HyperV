using HyperVPanel.Components;
using HyperVPanel.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<AppDatabase>();
builder.Services.AddSingleton<PowerShellService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GuacamoleJsonAuthService>();
builder.Services.AddSingleton<PanelService>();
builder.Services.AddSingleton<WhmcsApiAuthService>();
builder.Services.AddSingleton<WhmcsApiService>();
builder.Services.AddSingleton<AbuseMonitorService>();
builder.Services.AddScoped<UserSession>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AbuseMonitorService>());

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
    context.Response.Headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
    await next();
});

await app.Services.GetRequiredService<AppDatabase>().InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/whmcs", StringComparison.OrdinalIgnoreCase))
    {
        var auth = context.RequestServices.GetRequiredService<WhmcsApiAuthService>();
        var decision = auth.Authorize(context);
        if (!decision.Success)
        {
            context.Response.StatusCode = decision.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = decision.Error });
            return;
        }
    }

    await next();
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/api/public/status", async (PanelService panel) => Results.Ok(await panel.GetPublicVmStatusesAsync()));

app.MapGet("/api/whmcs/health", () => Results.Ok(new
{
    status = "ok",
    api = "PenguinPanel WHMCS API",
    version = "2.1",
    utc = DateTime.UtcNow
}));

app.MapGet("/api/whmcs/catalog", async (WhmcsApiService api) =>
    await HandleWhmcsAsync(api.GetCatalogAsync));

app.MapPost("/api/whmcs/vps/create", async (WhmcsApiService api, WhmcsCreateVpsRequest request) =>
    await HandleWhmcsAsync(() => api.CreateAsync(request)));

app.MapGet("/api/whmcs/vps/{serviceId:long}", async Task<IResult> (WhmcsApiService api, long serviceId) =>
{
    try
    {
        var status = await api.GetStatusAsync(serviceId);
        if (status is null)
        {
            return Results.NotFound(new { error = $"WHMCS service {serviceId} was not found." });
        }

        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        return WhmcsExceptionResult(ex);
    }
});

app.MapPost("/api/whmcs/vps/{serviceId:long}/suspend", async (WhmcsApiService api, long serviceId) =>
    await HandleWhmcsAsync(() => api.SuspendAsync(serviceId)));

app.MapPost("/api/whmcs/vps/{serviceId:long}/unsuspend", async (WhmcsApiService api, long serviceId) =>
    await HandleWhmcsAsync(() => api.UnsuspendAsync(serviceId)));

app.MapPost("/api/whmcs/vps/{serviceId:long}/resize", async (WhmcsApiService api, long serviceId, WhmcsResizeVpsRequest request) =>
    await HandleWhmcsAsync(() => api.ResizeAsync(serviceId, request)));

app.MapDelete("/api/whmcs/vps/{serviceId:long}", async (WhmcsApiService api, long serviceId) =>
    await HandleWhmcsAsync(() => api.TerminateAsync(serviceId)));

app.MapPost("/api/guest/heartbeat", async Task<IResult> (HttpContext context, PanelService panel, GuestAgentHeartbeatRequest request) =>
{
    var token = ReadGuestAgentToken(context);
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Unauthorized();
    }

    try
    {
        var response = await panel.RegisterGuestAgentHeartbeatAsync(
            token,
            request.AgentVersion ?? string.Empty,
            context.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
        return Results.Ok(response);
    }
    catch
    {
        return Results.Unauthorized();
    }
});

app.MapPost("/api/guest/commands/next", async Task<IResult> (HttpContext context, PanelService panel) =>
{
    var token = ReadGuestAgentToken(context);
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Unauthorized();
    }

    try
    {
        var command = await panel.StartNextGuestAgentCommandAsync(token);
        return command is null ? Results.NoContent() : Results.Ok(command);
    }
    catch
    {
        return Results.Unauthorized();
    }
});

app.MapPost("/api/guest/commands/{id:long}/complete", async Task<IResult> (HttpContext context, PanelService panel, long id, GuestAgentCompleteRequest request) =>
{
    var token = ReadGuestAgentToken(context);
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Unauthorized();
    }

    try
    {
        await panel.CompleteGuestAgentCommandAsync(token, id, request.Success, request.Message ?? string.Empty);
        return Results.Ok(new { status = "ok" });
    }
    catch
    {
        return Results.Unauthorized();
    }
});

app.Run();

static string ReadGuestAgentToken(HttpContext context)
{
    if (context.Request.Headers.TryGetValue("X-Penguin-Agent-Token", out var directToken))
    {
        return directToken.ToString().Trim();
    }

    if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
    {
        var value = authorization.ToString().Trim();
        const string prefix = "Bearer ";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[prefix.Length..].Trim();
        }
    }

    return string.Empty;
}

static async Task<IResult> HandleWhmcsAsync<T>(Func<Task<T>> action)
{
    try
    {
        return Results.Ok(await action());
    }
    catch (Exception ex)
    {
        return WhmcsExceptionResult(ex);
    }
}

static IResult WhmcsExceptionResult(Exception ex) => ex switch
{
    KeyNotFoundException => Results.NotFound(new { error = ex.Message }),
    InvalidOperationException => Results.BadRequest(new { error = ex.Message }),
    _ => Results.Problem(title: "WHMCS API operation failed", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError)
};
