using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class CeoAgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;
    private readonly BusinessKnowledgeService _knowledgeService;

    public CeoAgentOrchestrator(
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
        builder.Plugins.AddFromType<AgenticSystem.Plugins.SystemAutomationPlugin>("AutomationTools");
        _kernel = builder.Build();
    }

    public async Task<CeoDirective> RunCeoDirectiveAsync(
        string userId, 
        string directiveText, 
        UserBusinessProfile businessProfile, 
        string? analystMemorySummary = null)
    {
        var taskId = $"ceo-{Guid.NewGuid().ToString().Substring(0, 8)}";
        AgentCommunicationTracker.ActiveTaskId = taskId;
        var isQuestion = IsQueryAQuestion(directiveText);

        var startLog = new AgentTaskLog(
            taskId,
            $"[CeoAgent] Processing: \"{directiveText}\"",
            "Running",
            $"Determining executive command type (IsQuestion: {isQuestion}). Consult memory context...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var memoryInjection = string.IsNullOrEmpty(analystMemorySummary) 
            ? "" 
            : $"\n\nData Analyst Vector memory insights:\n{analystMemorySummary}";

        var knowledgeContext = await _knowledgeService.GetUnifiedKnowledgeContextAsync(userId);
        var knowledgeInjection = string.IsNullOrEmpty(knowledgeContext)
            ? ""
            : $"\n\n{knowledgeContext}";

        var systemPrompt = $@"You are the CEO (Chief Executive Officer) agent of the company '{businessProfile.BusinessName}' in the '{businessProfile.BusinessIndustry}' industry.
Company details:
- Core Description: {businessProfile.BusinessDescription}
- Primary Targets: {businessProfile.TargetAudience}
{knowledgeInjection}
{memoryInjection}

Analyze the user's input: '{directiveText}'

Your task:
1. If the input is a question (asking for advice, details, or planning), write an executive, direct response answering it. Keep the answer professional and tailored to this business.
2. If the input is a directive or a command (like 'optimize our Twitter approach' or 'run competitive intelligence on scaling'), reformulate it into a clear, actionable task directive assigned to the Manager Agent.
3. If the input is a command to the Lead Agent (such as 'send a WhatsApp message' or 'email John Davis') or a question about leads (such as 'who is John Davis?' or 'what is our lead status?'), you have a tool called 'CommandLeadAgent' which you MUST use to execute the command or query lead information. Return the response from the tool.

When you use the CommandLeadAgent tool, summarize the details of what you delegated in your response, and explain the action taken.

Return your response in a clear format. If it's a directive to the Manager, start your response with 'DIRECTIVE FOR MANAGER:' followed by the task. Otherwise, just output the direct answer.";

        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage(directiveText);

        var settings = new GeminiPromptExecutionSettings 
        { 
            ToolCallBehavior = GeminiToolCallBehavior.AutoInvokeKernelFunctions 
        };
        var response = await chatService.GetChatMessageContentAsync(chat, settings, _kernel);
        var responseText = response.Content ?? "Executive decision: No response content generated.";

        CeoDirective directive;
        if (responseText.StartsWith("DIRECTIVE FOR MANAGER:", StringComparison.OrdinalIgnoreCase))
        {
            var formattedDirective = responseText.Substring("DIRECTIVE FOR MANAGER:".Length).Trim();
            directive = new CeoDirective(
                Guid.NewGuid().ToString(),
                directiveText,
                "Manager",
                "Pending",
                formattedDirective,
                DateTime.UtcNow
            );
        }
        else
        {
            // It's an answered question or tool execution
            directive = new CeoDirective(
                Guid.NewGuid().ToString(),
                directiveText,
                "User",
                "Completed",
                responseText,
                DateTime.UtcNow
            );
        }

        var communications = AgentCommunicationTracker.GetCommunications(taskId);
        if (communications.Any())
        {
            var commText = "\n\n--- Agent-to-Agent Communication Trace ---\n" + 
                string.Join("\n\n", communications.Select(c => 
                    $"[{c.Sender} ➔ {c.Receiver}]\nTask Sent: {c.TaskGiven}\nResponse Received: {c.Response}"));
            directive = directive with { AnswerText = directive.AnswerText + commText };
        }

        await _searchSocialService.SaveCeoDirectiveAsync(userId, directive);

        var endLog = startLog with {
            Status = "Completed",
            AgentThought = isQuestion 
                ? "Answered CEO question directly using corporate and memory context." 
                : "Formulated manager directive task and logged pending command.",
            ToolOutput = $"CEO decision processed successfully. Result saved in executive database.",
            Timestamp = DateTime.UtcNow
        };
        await _firebaseLogger.LogStateAsync(endLog);

        AgentCommunicationTracker.Clear(taskId);

        return directive;
    }

    private bool IsQueryAQuestion(string text)
    {
        var cleaned = text.Trim().ToLower();
        return cleaned.EndsWith("?") || 
               cleaned.StartsWith("what") || 
               cleaned.StartsWith("how") || 
               cleaned.StartsWith("why") || 
               cleaned.StartsWith("can you") || 
               cleaned.StartsWith("explain");
    }
}
