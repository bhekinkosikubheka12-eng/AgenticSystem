using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Firebase.Database;
using Firebase.Database.Query;
using AgenticSystem.Data;
using AgenticSystem.Components.Pages.DocumentsAgent;

namespace AgenticSystem.Services;

public class BillingService
{
    public static BillingService? Instance { get; private set; }

    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;
    private readonly FirebaseLoggerService _loggerService;
    private readonly IServiceProvider _serviceProvider;

    public bool UseLocalFallback => _useLocalFallback;

    // Local static fallback stores
    private static readonly ConcurrentDictionary<string, BillingSubscription> _localSubscriptions = new();
    private static readonly List<AgentTaskLog> _localExecutionLogs = new();
    private static bool _isSeeded = false;

    // Retail rates (Charged to the customer under the Pay-As-You-Go subscription)
    public const double RetailInputRatePerM = 1.50; // $1.50 per 1M tokens
    public const double RetailOutputRatePerM = 5.00; // $5.00 per 1M tokens

    // Wholesale rates (Google's actual cost to developers for Gemini 2.5 Flash)
    public const double WholesaleInputRatePerM = 0.075; // $0.075 per 1M tokens
    public const double WholesaleOutputRatePerM = 0.30; // $0.30 per 1M tokens

    public BillingService(IConfiguration config, FirebaseLoggerService loggerService, IServiceProvider serviceProvider)
    {
        Instance = this;
        _loggerService = loggerService;
        _serviceProvider = serviceProvider;

        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. BillingService running in local fallback mode.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize Billing FirebaseClient: {ex.Message}. Running in local fallback mode.");
                _useLocalFallback = true;
            }
        }

        // Subscribe to live log stream to capture in-memory fallback logs in real-time
        _loggerService.SubscribeToLogs().Subscribe(logEvent =>
        {
            if (logEvent?.Object != null)
            {
                lock (_localExecutionLogs)
                {
                    var existingIdx = _localExecutionLogs.FindIndex(x => x.Id == logEvent.Object.Id);
                    if (existingIdx >= 0)
                    {
                        _localExecutionLogs[existingIdx] = logEvent.Object;
                    }
                    else
                    {
                        _localExecutionLogs.Add(logEvent.Object);
                    }
                }
            }
        });

        if (!_isSeeded)
        {
            SeedFallbackData();
            _isSeeded = true;
        }
    }

    private void SeedFallbackData()
    {
        // Pre-seed some mock history for local development if database is empty/mock
        var now = DateTime.UtcNow;
        lock (_localExecutionLogs)
        {
            _localExecutionLogs.AddRange(new List<AgentTaskLog>
            {
                new AgentTaskLog("ceo-88a21bc9", "[CeoAgent] Processing: \"analyze competitor pricing structures and recommend social strategy\"", "Completed", "I have determined that we need a search trend overview for SaaS competitor pricing. Prompting Search Agent...", "DIRECTIVE FOR MANAGER: Optimize competitor pricing audits and Twitter copy recommendations.", now.AddHours(-5)),
                new AgentTaskLog("search-920f38b2", "[SearchAgent] Analyze industry trends & competitors for NextGen AI", "Completed", "Found 5 competitor articles discussing pricing models. Documenting snippet content...", "[{\"title\":\"SaaS Billing Trends 2026\",\"snippet\":\"Competitors are adopting dynamic pay-as-you-go pricing.\",\"url\":\"https://example.com/pricing\"}]", now.AddHours(-4.8)),
                new AgentTaskLog("social-b830ac71", "[SocialAgent] Generate Twitter copy drafts aligned to target audience", "Completed", "Created 3 Twitter variants outlining cost savings compared to traditional frameworks.", "[{\"platform\":\"Twitter\",\"content\":\"Stop overpaying for fixed subscription buckets. Transition to agentic pay-as-you-go today!\"}]", now.AddHours(-4.7)),
                new AgentTaskLog("mgr-review-5f8802d1", "[ManagerAgent] Evaluate Search & Social Agent operational output", "Completed", "Oversight completed. Search agent performance: 92/100, Social media agent: 88/100. Recommendations saved.", "Manager audit finalized and archived in vector memory database.", now.AddHours(-4.5)),
                new AgentTaskLog("analyst-ad20f01a", "[DataAnalyst] Cataloging vector search queries and pricing schemas", "Completed", "Retrieved and unified semantic memory nodes matching 'SaaS billing model' with 94% relevance.", "Memory catalog item generated with tag: pricing-analysis.", now.AddHours(-2)),
                new AgentTaskLog("videoad-cc88910d", "[VideoAdAgent] Drafting scene storyboard for local business promotional video", "Completed", "Generated 4 storyboard scenes highlighting cost savings and time reduction variance.", "[{\"sceneNumber\":1,\"visualDescription\":\"Pulsing dark mode interface showing savings dashboard.\"}]", now.AddHours(-1))
            });
        }
    }

    public async Task<BillingSubscription> GetSubscriptionAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) userId = "default_user";

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                var sub = await _firebaseClient
                    .Child("BillingSubscriptions")
                    .Child(userId)
                    .OnceSingleAsync<BillingSubscription>();
                
                if (sub != null) return sub;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch subscription from Firebase: {ex.Message}");
            }
        }

        // Return local state or default mock
        if (_localSubscriptions.TryGetValue(userId, out var localSub))
        {
            return localSub;
        }

        var defaultSub = new BillingSubscription(
            UserId: userId,
            IsActive: false,
            MonthlyBudgetLimit: 50.0,
            CardholderName: "Alex Mercer",
            CardNumberLast4: "4242",
            CardExpiry: "12/28",
            SubscribedAt: DateTime.UtcNow
        );
        _localSubscriptions[userId] = defaultSub;
        return defaultSub;
    }

    public async Task SaveSubscriptionAsync(string userId, BillingSubscription sub)
    {
        if (string.IsNullOrEmpty(userId)) userId = "default_user";

        _localSubscriptions[userId] = sub;

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                await _firebaseClient
                    .Child("BillingSubscriptions")
                    .Child(userId)
                    .PutAsync(sub);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save subscription to Firebase: {ex.Message}");
            }
        }
    }

    public async Task<BillingSummary> GetBillingSummaryAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) userId = "default_user";

        var subscription = await GetSubscriptionAsync(userId);
        
        // 1. Fetch all execution logs (either from Firebase or local memory fallback)
        List<AgentTaskLog> logs = new();
        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                var firebaseLogs = await _firebaseClient
                    .Child("ExecutionLogs")
                    .OnceAsync<AgentTaskLog>();
                
                logs = firebaseLogs.Select(x => x.Object).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch historical logs from Firebase: {ex.Message}. Using local logs memory.");
                lock (_localExecutionLogs)
                {
                    logs = _localExecutionLogs.ToList();
                }
            }
        }
        else
        {
            lock (_localExecutionLogs)
            {
                logs = _localExecutionLogs.ToList();
            }
        }

        // 2. Fetch all generated documents (either from Firebase or reflection from DocumentsAgentService local dictionary)
        List<GeneratedDocument> documents = new();
        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                var firebaseDocs = await _firebaseClient
                    .Child("GeneratedDocuments")
                    .Child(userId)
                    .OnceAsync<GeneratedDocument>();
                
                documents = firebaseDocs.Select(x => x.Object).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch historical documents from Firebase: {ex.Message}");
                documents = GetReflectionDocuments(userId);
            }
        }
        else
        {
            documents = GetReflectionDocuments(userId);
        }

        // 3. Process records and estimate tokens & costs
        var historyLogs = new List<UsageLogItem>();
        var agentBreakdowns = new Dictionary<string, (int inT, int outT, double wholesale, double retail, int count)>();

        // Pre-initialize standard agents to make sure they show up on the chart even if zero
        var agentNames = new[] { "CEO Agent", "Manager Agent", "Search Agent", "Social Media Agent", "Leads Agent", "Documents Agent", "Data Analyst Agent", "Business Architect Agent", "Video Ad Agent", "Image Poster Agent" };
        foreach (var name in agentNames)
        {
            agentBreakdowns[name] = (0, 0, 0.0, 0.0, 0);
        }

        // Process execution logs
        foreach (var log in logs)
        {
            var details = AnalyzeAgentTask(log);
            var (inTokens, outTokens) = EstimateTokens(log.TaskDescription, log.AgentThought + log.ToolOutput, details.Overhead);
            var wholesale = CalculateCost(inTokens, outTokens, isWholesale: true);
            var retail = CalculateCost(inTokens, outTokens, isWholesale: false);

            historyLogs.Add(new UsageLogItem(
                TaskId: log.Id,
                AgentName: details.AgentName,
                Description: log.TaskDescription,
                InputTokens: inTokens,
                OutputTokens: outTokens,
                WholesaleCost: wholesale,
                RetailCost: retail,
                Timestamp: log.Timestamp
            ));

            if (agentBreakdowns.TryGetValue(details.AgentName, out var stats))
            {
                agentBreakdowns[details.AgentName] = (
                    stats.inT + inTokens,
                    stats.outT + outTokens,
                    stats.wholesale + wholesale,
                    stats.retail + retail,
                    stats.count + 1
                );
            }
            else
            {
                agentBreakdowns[details.AgentName] = (inTokens, outTokens, wholesale, retail, 1);
            }
        }

        // Process generated documents
        foreach (var doc in documents)
        {
            var docName = "Documents Agent";
            var docDesc = $"Generate document template {doc.TemplateId}: {doc.Title} ({doc.Type})";
            var (inTokens, outTokens) = EstimateTokens(docDesc, doc.RawContent + doc.StructuredDataJson, overhead: 500);
            var wholesale = CalculateCost(inTokens, outTokens, isWholesale: true);
            var retail = CalculateCost(inTokens, outTokens, isWholesale: false);

            historyLogs.Add(new UsageLogItem(
                TaskId: doc.Id,
                AgentName: docName,
                Description: docDesc,
                InputTokens: inTokens,
                OutputTokens: outTokens,
                WholesaleCost: wholesale,
                RetailCost: retail,
                Timestamp: doc.CreatedAt
            ));

            if (agentBreakdowns.TryGetValue(docName, out var stats))
            {
                agentBreakdowns[docName] = (
                    stats.inT + inTokens,
                    stats.outT + outTokens,
                    stats.wholesale + wholesale,
                    stats.retail + retail,
                    stats.count + 1
                );
            }
            else
            {
                agentBreakdowns[docName] = (inTokens, outTokens, wholesale, retail, 1);
            }
        }

        // Sort logs descending by timestamp
        historyLogs = historyLogs.OrderByDescending(x => x.Timestamp).ToList();

        // Build breakdowns list
        var breakdownsList = agentBreakdowns.Select(kv => new AgentTokenUsage(
            AgentName: kv.Key,
            InputTokens: kv.Value.inT,
            OutputTokens: kv.Value.outT,
            EstimatedWholesaleCost: kv.Value.wholesale,
            EstimatedRetailCost: kv.Value.retail,
            ExecutionCount: kv.Value.count
        )).ToList();

        int totalIn = historyLogs.Sum(x => x.InputTokens);
        int totalOut = historyLogs.Sum(x => x.OutputTokens);
        double totalWholesale = historyLogs.Sum(x => x.WholesaleCost);
        double totalRetail = historyLogs.Sum(x => x.RetailCost);

        return new BillingSummary(
            Subscription: subscription,
            TotalInputTokens: totalIn,
            TotalOutputTokens: totalOut,
            TotalTokens: totalIn + totalOut,
            TotalWholesaleCost: totalWholesale,
            TotalRetailCost: totalRetail,
            AgentBreakdowns: breakdownsList,
            HistoryLogs: historyLogs
        );
    }

    private List<GeneratedDocument> GetReflectionDocuments(string userId)
    {
        // Use reflection to inspect the static private `_localDocuments` dictionary in `DocumentsAgentService`
        try
        {
            var field = typeof(DocumentsAgentService).GetField("_localDocuments", BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null)
            {
                var dict = field.GetValue(null) as ConcurrentDictionary<string, List<GeneratedDocument>>;
                if (dict != null && dict.TryGetValue(userId, out var list))
                {
                    return list.ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Reflection retrieval failed: {ex.Message}");
        }
        return new List<GeneratedDocument>();
    }

    private (string AgentName, int Overhead) AnalyzeAgentTask(AgentTaskLog log)
    {
        var id = log.Id.ToLower();
        var desc = log.TaskDescription.ToLower();

        if (id.StartsWith("ceo-"))
            return ("CEO Agent", 500);
        
        if (id.StartsWith("mgr-review-") || id.StartsWith("mgr-delegation-"))
            return ("Manager Agent", 600);
        
        if (id.StartsWith("search-"))
            return ("Search Agent", 400);
        
        if (id.StartsWith("social-"))
            return ("Social Media Agent", 500);
        
        if (id.StartsWith("lead-"))
            return ("Leads Agent", 400);
        
        if (id.StartsWith("analyst-"))
            return ("Data Analyst Agent", 450);
        
        if (id.StartsWith("arch-"))
            return ("Business Architect Agent", 500);
        
        if (id.StartsWith("videoad-"))
            return ("Video Ad Agent", 350);
        
        if (id.StartsWith("poster-"))
            return ("Image Poster Agent", 350);

        // Try parsing from Task Description if ID prefix doesn't match
        if (desc.Contains("[ceoagent]") || desc.Contains("ceo directive"))
            return ("CEO Agent", 500);
        if (desc.Contains("[manageragent]") || desc.Contains("oversight review"))
            return ("Manager Agent", 600);
        if (desc.Contains("[searchagent]") || desc.Contains("industry trends"))
            return ("Search Agent", 400);
        if (desc.Contains("[socialagent]") || desc.Contains("social media"))
            return ("Social Media Agent", 500);
        if (desc.Contains("[dataanalyst]") || desc.Contains("memory insights"))
            return ("Data Analyst Agent", 450);

        return ("Operations Agent", 300);
    }

    public (int InputTokens, int OutputTokens) EstimateTokens(string prompt, string completion, int overhead)
    {
        // English average heuristic: 3.8 characters per token
        int promptCharCount = string.IsNullOrEmpty(prompt) ? 0 : prompt.Length;
        int inputTokens = Math.Max(20, (int)(promptCharCount / 3.8)) + overhead;

        int completionCharCount = string.IsNullOrEmpty(completion) ? 0 : completion.Length;
        int outputTokens = Math.Max(10, (int)(completionCharCount / 3.8));

        return (inputTokens, outputTokens);
    }

    public double CalculateCost(int inputTokens, int outputTokens, bool isWholesale)
    {
        if (isWholesale)
        {
            double inputCost = (inputTokens / 1_000_000.0) * WholesaleInputRatePerM;
            double outputCost = (outputTokens / 1_000_000.0) * WholesaleOutputRatePerM;
            return inputCost + outputCost;
        }
        else
        {
            double inputCost = (inputTokens / 1_000_000.0) * RetailInputRatePerM;
            double outputCost = (outputTokens / 1_000_000.0) * RetailOutputRatePerM;
            return inputCost + outputCost;
        }
    }

    public string GetCurrentUserId()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var auth = scope.ServiceProvider.GetService<FirebaseAuthService>();
            return auth?.CurrentUser?.LocalId ?? "default_user";
        }
        catch
        {
            return "default_user";
        }
    }

    public async Task EnforceLimitsAsync(string taskId, string taskDescription)
    {
        var userId = GetCurrentUserId();
        var sub = await GetSubscriptionAsync(userId);

        // 1. Check Global Kill Switch
        if (sub.IsKillSwitchActive)
        {
            throw new InvalidOperationException("Agent execution blocked: Global Kill Switch is active.");
        }

        // 2. Check Subscription active state
        if (!sub.IsActive)
        {
            throw new InvalidOperationException("Agent execution blocked: No active Pay-As-You-Go subscription.");
        }

        // 3. Estimate prompt size and check MaxTokensPerCall limit
        var agentDetails = AnalyzeAgentTask(new AgentTaskLog(taskId, taskDescription, "Running", "", "", DateTime.UtcNow));
        var (inTokens, _) = EstimateTokens(taskDescription, "", agentDetails.Overhead);
        if (inTokens > sub.MaxTokensPerCall)
        {
            throw new InvalidOperationException($"Agent execution blocked: Prompt size of {inTokens} tokens exceeds the call limit of {sub.MaxTokensPerCall} tokens.");
        }

        // 4. Calculate total spent and check budget cap
        var summary = await GetBillingSummaryAsync(userId);
        if (summary.TotalRetailCost >= sub.MonthlyBudgetLimit)
        {
            throw new InvalidOperationException($"Agent execution blocked: Monthly spending cap of ${sub.MonthlyBudgetLimit:F2} has been reached.");
        }

        // 5. Check individual agent execution limit
        var agentStats = summary.AgentBreakdowns.FirstOrDefault(x => x.AgentName.Equals(agentDetails.AgentName, StringComparison.OrdinalIgnoreCase));
        int currentCount = agentStats?.ExecutionCount ?? 0;
        int maxLimit = GetAgentLimit(sub, agentDetails.AgentName);

        if (currentCount >= maxLimit)
        {
            throw new InvalidOperationException($"Agent execution blocked: Call count limit ({maxLimit}) for '{agentDetails.AgentName}' has been reached.");
        }
    }

    private int GetAgentLimit(BillingSubscription sub, string agentName)
    {
        return agentName switch
        {
            "CEO Agent" => sub.CeoLimit,
            "Manager Agent" => sub.ManagerLimit,
            "Search Agent" => sub.SearchLimit,
            "Social Media Agent" => sub.SocialLimit,
            "Leads Agent" => sub.LeadsLimit,
            "Documents Agent" => sub.DocumentsLimit,
            "Data Analyst Agent" => sub.AnalystLimit,
            "Business Architect Agent" => sub.ArchitectLimit,
            "Video Ad Agent" => sub.VideoLimit,
            "Image Poster Agent" => sub.ImageLimit,
            _ => 20
        };
    }
}
