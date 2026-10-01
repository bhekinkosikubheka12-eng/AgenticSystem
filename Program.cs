using AgenticSystem.Components;
using AgenticSystem.Services;
using AgenticSystem.Data;

var builder = WebApplication.CreateBuilder(args);

// Add standard HttpClient support for REST Auth Service
builder.Services.AddHttpClient();

// Register Custom AI Stack Infrastructures
builder.Services.AddSingleton<FirebaseLoggerService>();
builder.Services.AddSingleton<BillingService>();
builder.Services.AddSingleton<QdrantMemoryService>();
builder.Services.AddSingleton<FirebaseStorageService>();
builder.Services.AddSingleton<BusinessKnowledgeService>();
builder.Services.AddTransient<AgentOrchestrator>();
builder.Services.AddScoped<FirebaseAuthService>();
builder.Services.AddScoped<FirebaseProfileService>();
builder.Services.AddSingleton<FirebaseSearchSocialService>();
builder.Services.AddSingleton<ChatSessionService>();
builder.Services.AddTransient<SearchAndSocialOrchestrator>();
builder.Services.AddTransient<CeoAgentOrchestrator>();
builder.Services.AddTransient<ManagerAgentOrchestrator>();
builder.Services.AddTransient<DataAnalystAgentOrchestrator>();
builder.Services.AddTransient<BusinessArchitectAgentOrchestrator>();
builder.Services.AddTransient<VideoAdGeneratorService>();
builder.Services.AddTransient<ImagePosterGeneratorService>();
builder.Services.AddHostedService<AgentSchedulerService>();
builder.Services.AddSingleton<FirebaseLeadsService>();
builder.Services.AddTransient<LeadsCenterOrchestrator>();
builder.Services.AddSingleton<WhatsappService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost("/api/leads/website-form/{userId}", async (string userId, WebsiteFormPayload payload, LeadsCenterOrchestrator orchestrator, FirebaseProfileService profileService) =>
{
    if (string.IsNullOrWhiteSpace(payload.Name) || string.IsNullOrWhiteSpace(payload.Message))
    {
        return Results.BadRequest(new { success = false, message = "Name and Message are required." });
    }

    var profile = await profileService.GetProfileAsync(userId);
    if (profile == null)
    {
        return Results.NotFound(new { success = false, message = "User business profile not found." });
    }

    await orchestrator.ProcessIncomingLeadAsync(
        userId,
        "Website Form",
        payload.Name,
        payload.Email ?? "No Email Provided",
        payload.Message,
        profile
    );

    return Results.Ok(new { success = true, message = "Lead submitted and processed." });
});

// WhatsApp Webhook Verification (GET)
app.MapGet("/api/leads/whatsapp/{userId}", async (string userId, HttpContext context, WhatsappService whatsappService) =>
{
    var query = context.Request.Query;
    string mode = query["hub.mode"]!;
    string verifyToken = query["hub.verify_token"]!;
    string challenge = query["hub.challenge"]!;

    var (success, challengeResult) = await whatsappService.VerifyWebhookAsync(userId, mode, verifyToken, challenge);
    if (success)
    {
        return Results.Content(challengeResult, "text/plain");
    }
    return Results.BadRequest("Verification failed");
});

// WhatsApp Webhook Event (POST)
app.MapPost("/api/leads/whatsapp/{userId}", async (string userId, HttpContext context, WhatsappService whatsappService, LeadsCenterOrchestrator orchestrator, FirebaseProfileService profileService) =>
{
    using var reader = new StreamReader(context.Request.Body);
    string payload = await reader.ReadToEndAsync();
    
    var success = await whatsappService.ProcessWebhookPayloadAsync(userId, payload, orchestrator, profileService);
    if (success)
    {
        return Results.Ok();
    }
    return Results.BadRequest("Failed to process event");
});

app.Run();

public class WebsiteFormPayload
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
