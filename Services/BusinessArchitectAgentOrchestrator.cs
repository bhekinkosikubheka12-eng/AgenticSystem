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

public class BusinessArchitectAgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;
    private readonly BusinessKnowledgeService _knowledgeService;

    public BusinessArchitectAgentOrchestrator(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        FirebaseSearchSocialService searchSocialService,
        BusinessKnowledgeService knowledgeService)
    {
        _firebaseLogger = firebaseLogger;
        _searchSocialService = searchSocialService;
        _knowledgeService = knowledgeService;

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );
        _kernel = builder.Build();
    }

    public async Task<BusinessModelVisual> RunBusinessModelGenerationAsync(string userId, UserBusinessProfile businessProfile)
    {
        var taskId = $"arch-{Guid.NewGuid().ToString().Substring(0, 8)}";

        // Start Ledger Log
        var startLog = new AgentTaskLog(
            taskId,
            $"[BusinessArchitectAgent] Synthesize and visual map Business Model Canvas for {businessProfile.BusinessName}",
            "Running",
            "Retrieving active agent execution logs and user profile configurations...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        // Fetch recent directives to extract agent movements
        var directives = await _searchSocialService.GetCeoDirectivesAsync(userId);
        var searchResults = await _searchSocialService.GetSearchResultsAsync(userId);
        var socialResults = await _searchSocialService.GetSocialResultsAsync(userId);

        var agentMovementsText = "Active movements: \n";
        if (directives.Any())
        {
            agentMovementsText += $"- {directives.Count} CEO directives issued. Directives: " + string.Join(", ", directives.Take(3).Select(d => d.DirectiveText)) + "\n";
        }
        if (searchResults != null)
        {
            agentMovementsText += $"- Search Agent performed query audits fetching competitor insights: " + string.Join(", ", searchResults.Items.Take(2).Select(i => i.Title)) + "\n";
        }
        if (socialResults != null)
        {
            agentMovementsText += $"- Social Media advisor completed platform drafts for platforms: " + string.Join(", ", socialResults.Recommendations.Select(r => r.Platform)) + "\n";
        }

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var knowledgeContext = await _knowledgeService.GetUnifiedKnowledgeContextAsync(userId);
        var knowledgeInjection = string.IsNullOrEmpty(knowledgeContext)
            ? ""
            : $"\nAdditional Corporate Knowledge:\n{knowledgeContext}";

        var systemPrompt = $@"You are a professional Business Architect Agent.
Analyze the user's business profile:
- Company Name: {businessProfile.BusinessName}
- Industry: {businessProfile.BusinessIndustry}
- Core Description: {businessProfile.BusinessDescription}
- Target Audience: {businessProfile.TargetAudience}
{knowledgeInjection}

Analyze the latest agent action traces in this business:
{agentMovementsText}

Your task is to generate exactly 8 customized Business Model Canvas cards detailing every moving part of this business and how the AI agents (CEO, Manager, Search, Social, Data Analyst) support, optimize, and streamline these operations.

You must output exactly these 8 sections:
1. Value Propositions
2. Customer Segments
3. Channels
4. Customer Relationships
5. Key Activities
6. Key Resources
7. Cost Structure
8. Revenue & Strategic Value

Return the response ONLY as a JSON array of objects. Do not wrap in markdown code blocks, do not output anything else.
Structure:
[
  {{
    ""sectionName"": ""Value Propositions"",
    ""description"": ""How the business creates value for customer segments..."",
    ""bulletPoints"": [
      ""Automated trend monitoring via Search Agent"",
      ""Optimized social templates to boost audience reach"",
      ""Instant institutional context retrieved from vector storage""
    ]
  }}
]";

        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage("Synthesize business visuals and generate the Business Model Canvas JSON.");

        var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
        var cleanedContent = CleanJson(response.Content ?? "");

        List<BusinessModelItem> sections = new();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<BusinessModelItem>>(cleanedContent, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
            if (parsed != null)
            {
                sections = parsed;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Business Architect JSON parsing failed: {ex.Message}");
            sections = GetFallbackBusinessModel(businessProfile);
        }

        // Keep section count to 8
        if (sections.Count > 8)
        {
            sections = sections.Take(8).ToList();
        }

        var visualObj = new BusinessModelVisual(userId, sections, DateTime.UtcNow);
        await _searchSocialService.SaveBusinessModelAsync(visualObj);

        var endLog = startLog with {
            Status = "Completed",
            AgentThought = $"Business Model Canvas successfully compiled. Rendered {sections.Count} core sections detailing organizational flow.",
            ToolOutput = $"Business model visually mapped and saved to Firebase cache database.",
            Timestamp = DateTime.UtcNow
        };
        await _firebaseLogger.LogStateAsync(endLog);

        return visualObj;
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

    private List<BusinessModelItem> GetFallbackBusinessModel(UserBusinessProfile profile)
    {
        return new List<BusinessModelItem>
        {
            new BusinessModelItem(
                "Value Propositions", 
                $"Deliver modern solutions within the {profile.BusinessIndustry} domain optimized dynamically via Gemini Agent reasoning.",
                new List<string> { "Real-time query intelligence", "AI Copywriting advisor templates", "Volatile cache semantic indexes" }
            ),
            new BusinessModelItem(
                "Customer Segments", 
                $"Target customer base aligned with user parameters.",
                new List<string> { profile.TargetAudience, "Tech-focused operators", "Innovation leaders" }
            ),
            new BusinessModelItem(
                "Channels", 
                "Communication routes utilized by agents to reach audience.",
                new List<string> { "LinkedIn corporate network", "Twitter/X microblog feed", "Internal Command operational suite" }
            ),
            new BusinessModelItem(
                "Customer Relationships", 
                "Engagement models to build trust and authority.",
                new List<string> { "Consistent trend-driven posts", "Immediate search validation", "Accurate data answers" }
            ),
            new BusinessModelItem(
                "Key Activities", 
                "Operating procedures ran autonomously inside workspace.",
                new List<string> { "Market Search querying", "Social Media recommendation drafting", "Vector memory catalog optimization" }
            ),
            new BusinessModelItem(
                "Key Resources", 
                "System assets driving agent capability.",
                new List<string> { "Gemini LLM model configuration", "Qdrant memory vector storage", "Firebase realtime database" }
            ),
            new BusinessModelItem(
                "Cost Structure", 
                "Resource expenses of running operations.",
                new List<string> { "LLM API usage tokens", "Scheduler background loop computing", "Realtime DB read/write triggers" }
            ),
            new BusinessModelItem(
                "Revenue & Strategic Value", 
                "Value captured from operations.",
                new List<string> { "Streamlined strategic planning", "Maximized product reach", "Automated competitive audits" }
            )
        };
    }
}
