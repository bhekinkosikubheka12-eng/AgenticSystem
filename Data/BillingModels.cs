using System;
using System.Collections.Generic;

namespace AgenticSystem.Data;

public record BillingSubscription(
    string UserId,
    bool IsActive,
    double MonthlyBudgetLimit,
    string CardholderName,
    string CardNumberLast4,
    string CardExpiry,
    DateTime SubscribedAt,
    bool IsKillSwitchActive = false,
    int CeoLimit = 20,
    int ManagerLimit = 20,
    int SearchLimit = 20,
    int SocialLimit = 20,
    int LeadsLimit = 20,
    int DocumentsLimit = 20,
    int AnalystLimit = 20,
    int ArchitectLimit = 20,
    int VideoLimit = 20,
    int ImageLimit = 20,
    int MaxTokensPerCall = 5000
);

public record AgentTokenUsage(
    string AgentName,
    int InputTokens,
    int OutputTokens,
    double EstimatedWholesaleCost,
    double EstimatedRetailCost,
    int ExecutionCount
);

public record UsageLogItem(
    string TaskId,
    string AgentName,
    string Description,
    int InputTokens,
    int OutputTokens,
    double WholesaleCost,
    double RetailCost,
    DateTime Timestamp
);

public record BillingSummary(
    BillingSubscription Subscription,
    int TotalInputTokens,
    int TotalOutputTokens,
    int TotalTokens,
    double TotalWholesaleCost,
    double TotalRetailCost,
    List<AgentTokenUsage> AgentBreakdowns,
    List<UsageLogItem> HistoryLogs
);
