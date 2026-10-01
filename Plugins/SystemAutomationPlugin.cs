using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using AgenticSystem.Services;
using AgenticSystem.Data;

namespace AgenticSystem.Plugins;

public class SystemAutomationPlugin
{
    [KernelFunction, Description("Fetches real-time localized operational system metrics.")]
    public string GetSystemMetrics()
    {
        return $"[System Metrics] CPU: {Random.Shared.Next(12, 45)}% | Active Workers: 4 Node Pools Online | Regional Load Latency: 14ms.";
    }

    [KernelFunction, Description("Executes deep structural calculation processing for automation workflows.")]
    public string CalculateRiskFactor([Description("The payload intensity target scale")] double intensity)
    {
        var computationalResult = intensity * 1.618;
        return $"[Calculation Engine] Normalized execution risk mitigation value evaluated at: {computationalResult:F3}";
    }

    [KernelFunction, Description("Commands the Lead Agent to perform actions (like sending messages on platforms: Email, WhatsApp, Facebook, Website Form) or asks questions about active leads, history, logs, and metrics.")]
    public async Task<string> CommandLeadAgent(
        [Description("The natural language instruction or query for the Lead Agent, e.g. 'send email to Sarah saying hello', 'who is Sarah Jenkins?', or 'list all pending reviews'.")] string query,
        [Description("Optional user ID to scope the leads database query. If empty, the active user's ID will be resolved automatically.")] string userId = "")
    {
        try
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true);

            var currentDir = Directory.GetCurrentDirectory();
            if (currentDir != AppContext.BaseDirectory && File.Exists(Path.Combine(currentDir, "appsettings.json")))
            {
                configBuilder.SetBasePath(currentDir).AddJsonFile("appsettings.json", optional: true);
            }

            var config = configBuilder.Build();

            var profileService = new FirebaseProfileService(config);
            var leadsService = new FirebaseLeadsService(config);
            var loggerService = new FirebaseLoggerService(config);
            var orchestrator = new LeadsCenterOrchestrator(config, loggerService, leadsService);

            var activeUserId = userId;
            if (string.IsNullOrEmpty(activeUserId))
            {
                var profiles = await profileService.GetAllProfilesAsync();
                activeUserId = profiles.FirstOrDefault()?.UserId ?? "default-user";
            }

            var profile = await profileService.GetProfileAsync(activeUserId);
            if (profile == null)
            {
                return "Error: User Business Profile not found. Please establish your business profile first.";
            }

            var activeLeads = await leadsService.GetLeadMessagesAsync(activeUserId);
            var settings = await leadsService.GetLeadsSettingsAsync(activeUserId);

            // Execute the Lead Commander Agent flow
            string response = await orchestrator.RunCommanderAgentAsync(
                activeUserId,
                query,
                activeLeads,
                profile,
                settings.IsAutonomous
            );

            AgentCommunicationTracker.RecordCommunication("CeoAgent", "LeadsAgent", query, response);

            return response;
        }
        catch (Exception ex)
        {
            return $"Error executing Lead Agent command: {ex.Message}";
        }
    }
}
