using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using AgenticSystem.Data;
using AgenticSystem.Plugins;

namespace AgenticSystem.Services;

public class AgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly QdrantMemoryService _qdrantMemory;
    private readonly BusinessKnowledgeService _knowledgeService;

    public AgentOrchestrator(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        QdrantMemoryService qdrantMemory,
        BusinessKnowledgeService knowledgeService)
    {
        _firebaseLogger = firebaseLogger;
        _qdrantMemory = qdrantMemory;
        _knowledgeService = knowledgeService;

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );

        builder.Plugins.AddFromType<SystemAutomationPlugin>("AutomationTools");
        _kernel = builder.Build();
    }

    public async Task RunWorkflowAsync(string taskId, string taskPrompt, UserBusinessProfile? businessProfile = null)
    {
        try
        {
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();
            
            // 1. Check long-term memory historical context 
            var pastInsights = await _qdrantMemory.SearchMemoryAsync(taskPrompt);
            string contextInjection = pastInsights.Any() ? $"\nRelevant Past Knowledge:\n{string.Join("\n", pastInsights)}" : "";

            // Inject User Business Context if available
            string businessInjection = "";
            if (businessProfile != null)
            {
                businessInjection = $"\nUser's Business Context:\n- Name: {businessProfile.BusinessName}\n- Industry: {businessProfile.BusinessIndustry}\n- Description: {businessProfile.BusinessDescription}\n- Target Audience: {businessProfile.TargetAudience}\nUse this context to tailor your tool responses and metrics analysis specifically to benefit this type of business!";
                
                var knowledgeContext = await _knowledgeService.GetUnifiedKnowledgeContextAsync(businessProfile.UserId);
                if (!string.IsNullOrEmpty(knowledgeContext))
                {
                    businessInjection += $"\n\n{knowledgeContext}";
                }
            }

            // 2. Build Agent System Directives
            var chatHistory = new ChatHistory($"You are an autonomous operations agent. Execute goals using provided tools. {businessInjection} {contextInjection}");
            chatHistory.AddUserMessage(taskPrompt);

            // 3. Initialize Initial Log State
            var executionLog = new AgentTaskLog(taskId, taskPrompt, "Running", "Analyzing constraints and executing tools...", "", DateTime.UtcNow);
            await _firebaseLogger.LogStateAsync(executionLog);

            // 4. Invoke LLM with automatic tool calling support
            var settings = new GeminiPromptExecutionSettings 
            { 
                ToolCallBehavior = GeminiToolCallBehavior.AutoInvokeKernelFunctions 
            };
            var response = await chatService.GetChatMessageContentAsync(chatHistory, settings, _kernel);

            // 5. Commit Key Insight directly to Vector Storage
            string shortTermInsight = response.Content ?? "Execution process finalized without analytical textual output.";
            await _qdrantMemory.SaveKnowledgeAsync(Guid.NewGuid().ToString(), $"Context: {taskPrompt} | Insight: {shortTermInsight}", "WorkflowResult");

            // 6. Push final complete state execution to Firebase
            var finalizedLog = executionLog with {
                Status = "Completed",
                AgentThought = shortTermInsight,
                ToolOutput = "Plugins invoked dynamically by execution plan.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(finalizedLog);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during orchestrator execution: {ex.Message}");
            var errorLog = new AgentTaskLog(
                taskId, 
                taskPrompt, 
                "Failed", 
                $"Workflow interrupted by error: {ex.Message}", 
                "No tools completed.", 
                DateTime.UtcNow
            );
            await _firebaseLogger.LogStateAsync(errorLog);
        }
    }
}
