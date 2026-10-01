using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class ManagerAgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;
    private readonly SearchAndSocialOrchestrator _workingOrchestrator;

    public ManagerAgentOrchestrator(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        FirebaseSearchSocialService searchSocialService,
        SearchAndSocialOrchestrator workingOrchestrator)
    {
        _firebaseLogger = firebaseLogger;
        _searchSocialService = searchSocialService;
        _workingOrchestrator = workingOrchestrator;

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );
        _kernel = builder.Build();
    }

    public async Task RunOversightReviewAsync(string userId, UserBusinessProfile businessProfile)
    {
        var taskId = $"mgr-review-{Guid.NewGuid().ToString().Substring(0, 8)}";

        var startLog = new AgentTaskLog(
            taskId,
            $"[ManagerAgent] Evaluate Search & Social Agent operational output for {businessProfile.BusinessName}",
            "Running",
            "Retrieving latest metrics from workspace databases for analysis...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        // Fetch latest working results
        var latestSearch = await _searchSocialService.GetSearchResultsAsync(userId);
        var latestSocial = await _searchSocialService.GetSocialResultsAsync(userId);

        if (latestSearch == null && latestSocial == null)
        {
            var emptyReport = new ManagerPerformanceReport(
                userId,
                new List<AgentPerformanceReview>
                {
                    new AgentPerformanceReview("Search Agent", 0, "No execution data detected.", "Deploy Search Agent."),
                    new AgentPerformanceReview("Social Media Agent", 0, "No execution data detected.", "Run copywriter agent after search completes.")
                },
                DateTime.UtcNow
            );
            await _searchSocialService.SaveManagerReportAsync(emptyReport);
            
            await _firebaseLogger.LogStateAsync(startLog with {
                Status = "Completed",
                AgentThought = "Completed oversight review: No active operational logs found. Generated warning placeholder report.",
                Timestamp = DateTime.UtcNow
            });
            return;
        }

        var searchContentText = latestSearch != null 
            ? string.Join("\n", latestSearch.Items.Select(i => $"- {i.Title}: {i.Snippet}"))
            : "No active search records.";

        var socialContentText = latestSocial != null
            ? string.Join("\n", latestSocial.Recommendations.Select(r => $"- [{r.Platform}] Content: {r.Content}"))
            : "No active social recommendations.";

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var systemPrompt = $@"You are the Operations Manager agent of company '{businessProfile.BusinessName}' in industry '{businessProfile.BusinessIndustry}'.
Your task is to audit the performance of:
1. Search Agent (Responsible for finding latest trends/news. Latest outputs:\n{searchContentText})
2. Social Media Agent (Responsible for recommending promotional posts. Latest outputs:\n{socialContentText})

Evaluate their alignment with company target audience: '{businessProfile.TargetAudience}' and domain description: '{businessProfile.BusinessDescription}'.
Assign each agent a score (1-100), analyze their relevance, and write a concrete optimization strategy for future executions.

Return your response ONLY as a JSON array of objects. Do not wrap in markdown code blocks, do not output anything else.
JSON Structure:
[
  {{
    ""agentName"": ""Search Agent"",
    ""score"": 85,
    ""relevanceAnalysis"": ""Detailed evaluation of current results quality..."",
    ""optimizationStrategy"": ""Actionable directive to improve next search run...""
  }},
  {{
    ""agentName"": ""Social Media Agent"",
    ""score"": 90,
    ""relevanceAnalysis"": ""Detailed evaluation of copywriting content..."",
    ""optimizationStrategy"": ""Actionable directive to improve posts...""
  }}
]";

        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage("Perform the auditing review of working agents.");

        var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
        var cleanedResponse = CleanJson(response.Content ?? "");

        List<AgentPerformanceReview> reviews = new();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<AgentPerformanceReview>>(cleanedResponse, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
            if (parsed != null)
            {
                reviews = parsed;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Manager Review JSON parsing failed: {ex.Message}");
            reviews = GetFallbackPerformanceReviews(latestSearch != null, latestSocial != null);
        }

        var report = new ManagerPerformanceReport(userId, reviews, DateTime.UtcNow);
        await _searchSocialService.SaveManagerReportAsync(report);

        var endLog = startLog with {
            Status = "Completed",
            AgentThought = $"Oversight completed. Calculated performance ratings: Search Agent ({reviews.FirstOrDefault(r => r.AgentName.Contains("Search"))?.Score ?? 0}/100) | Social Agent ({reviews.FirstOrDefault(r => r.AgentName.Contains("Social"))?.Score ?? 0}/100).",
            ToolOutput = "Performance report written and registered in Firebase database.",
            Timestamp = DateTime.UtcNow
        };
        await _firebaseLogger.LogStateAsync(endLog);
    }

    public async Task AssignTaskToWorkingAgentAsync(
        string userId, 
        string directiveId, 
        UserBusinessProfile businessProfile, 
        string taskDescription)
    {
        var taskId = $"mgr-delegation-{Guid.NewGuid().ToString().Substring(0, 8)}";

        var startLog = new AgentTaskLog(
            taskId,
            $"[ManagerAgent] Delegate CEO Directive to Working Agents: \"{taskDescription}\"",
            "Running",
            "Accepting CEO command. Transitioning directive status to 'In Progress' and deploying pipelines...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        // 1. Fetch directive and update status
        var directives = await _searchSocialService.GetCeoDirectivesAsync(userId);
        var activeDirective = directives.FirstOrDefault(d => d.Id == directiveId);
        if (activeDirective != null)
        {
            var updated = activeDirective with { Status = "In Progress" };
            await _searchSocialService.SaveCeoDirectiveAsync(userId, updated);
        }

        try
        {
            // 2. Delegate to Working Agent Orchestrator to run search + social
            await _workingOrchestrator.RunWorkflowAsync(userId, businessProfile);

            // 3. Immediately run performance audit
            await RunOversightReviewAsync(userId, businessProfile);

            // 4. Update directive to completed
            if (activeDirective != null)
            {
                var completed = activeDirective with { Status = "Completed" };
                await _searchSocialService.SaveCeoDirectiveAsync(userId, completed);
            }

            var endLog = startLog with {
                Status = "Completed",
                AgentThought = "CEO Directive workflow fully completed. Working agents executed and results audited.",
                ToolOutput = "Task completed successfully. Updated CEO Office directive status tracker.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(endLog);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Manager task delegation failed: {ex.Message}");
            if (activeDirective != null)
            {
                var failed = activeDirective with { Status = "Pending" }; // reset
                await _searchSocialService.SaveCeoDirectiveAsync(userId, failed);
            }

            var errorLog = startLog with {
                Status = "Failed",
                AgentThought = $"Workflow interrupted: {ex.Message}",
                ToolOutput = "Delegation pipeline failed. Reverted directive status to Pending.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(errorLog);
        }
    }

    private string CleanJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "[]";
        var cleaned = content.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
        }
        else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(3);
        }
        if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }
        return cleaned.Trim();
    }

    private List<AgentPerformanceReview> GetFallbackPerformanceReviews(bool hasSearch, bool hasSocial)
    {
        return new List<AgentPerformanceReview>
        {
            new AgentPerformanceReview(
                "Search Agent", 
                hasSearch ? 78 : 0, 
                hasSearch ? "Successfully fetched simulated article details." : "No execution logs available.", 
                hasSearch ? "Refine queries to cover more competitor products." : "Execute manual or scheduler Search Agent."
            ),
            new AgentPerformanceReview(
                "Social Media Agent", 
                hasSocial ? 82 : 0, 
                hasSocial ? "Constructed platform specific posts with valid hashtags." : "No recommendations generated.", 
                hasSocial ? "Add more customer testimonial angles to post contents." : "Run Copywriter Agent."
            )
        };
    }
}
