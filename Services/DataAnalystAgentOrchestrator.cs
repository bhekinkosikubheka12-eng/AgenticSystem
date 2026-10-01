using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class DataAnalystAgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;
    private readonly QdrantMemoryService _qdrantMemory;

    public DataAnalystAgentOrchestrator(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        FirebaseSearchSocialService searchSocialService,
        QdrantMemoryService qdrantMemory)
    {
        _firebaseLogger = firebaseLogger;
        _searchSocialService = searchSocialService;
        _qdrantMemory = qdrantMemory;

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );
        _kernel = builder.Build();
    }

    public async Task<string> RunMemoryOptimizationAsync(string userId, UserBusinessProfile businessProfile)
    {
        var taskId = $"analyst-{Guid.NewGuid().ToString().Substring(0, 8)}";

        var startLog = new AgentTaskLog(
            taskId,
            $"[DataAnalystAgent] Optimize Qdrant vector memory index for {businessProfile.BusinessName}",
            "Running",
            "Reading institutional memory catalog points from cache database...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        var catalog = await _searchSocialService.GetMemoryCatalogAsync(userId);
        if (!catalog.Any())
        {
            var fallbackSummary = "Memory base is currently empty. Initialize business context vectors to begin optimization.";
            await _firebaseLogger.LogStateAsync(startLog with {
                Status = "Completed",
                AgentThought = "Analyzed memory index: Catalog is currently empty. No optimizations required.",
                ToolOutput = fallbackSummary,
                Timestamp = DateTime.UtcNow
            });
            return fallbackSummary;
        }

        var catalogText = string.Join("\n", catalog.Select((item, idx) => 
            $"{idx+1}. Tag: {item.Tag} | Content: {item.Content}"));

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var systemPrompt = $@"You are the Data Analyst Agent of the company '{businessProfile.BusinessName}'.
Your job is to optimize and evaluate the institutional memory index.
Here is the current memory catalog containing past agent thoughts and research findings:
{catalogText}

Please perform a data analysis:
1. Synthesize the indexed memory records.
2. Outline 3 strategic organizational insights based on this data that will help other agents (the CEO and Manager) make better decisions.
3. Suggest tags or organizational optimizations.

Return your analysis in a structured, easy-to-read executive summary.";

        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage("Run memory analysis and optimize the vector store catalog.");

        var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
        var analysisResult = response.Content ?? "Data Analyst completed memory optimization with no qualitative remarks.";

        // Commit Analyst's synthesis back to memory to catalog the optimization!
        var optMemoryId = $"opt-{Guid.NewGuid().ToString().Substring(0, 8)}";
        await _qdrantMemory.SaveKnowledgeAsync(optMemoryId, $"Analyst Insight: {analysisResult.Substring(0, Math.Min(analysisResult.Length, 200))}", "AnalystSynthesis", userId);

        var endLog = startLog with {
            Status = "Completed",
            AgentThought = "Institutional vector memory optimization complete. Catalog indexed. Synthesized insights committed to memory.",
            ToolOutput = analysisResult,
            Timestamp = DateTime.UtcNow
        };
        await _firebaseLogger.LogStateAsync(endLog);

        return analysisResult;
    }

    public async Task AddMemoryItemAsync(string userId, string content, string tag)
    {
        var id = Guid.NewGuid().ToString();
        // This will trigger the QdrantMemoryService event, which automatically updates the Firebase catalog!
        await _qdrantMemory.SaveKnowledgeAsync(id, content, tag, userId);
    }

    public async Task DeleteMemoryItemAsync(string userId, string itemId)
    {
        await _searchSocialService.DeleteMemoryCatalogItemAsync(userId, itemId);
    }

    public async Task<string> SearchMemoryInsightsAsync(string query)
    {
        var matches = await _qdrantMemory.SearchMemoryAsync(query);
        if (!matches.Any()) return "No relevant memory records found matching query.";
        return string.Join("\n", matches.Select(m => $"- {m}"));
    }
}
