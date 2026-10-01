using System;
using System.Text.Json.Serialization;

namespace AgenticSystem.Data;

public record AgentTaskLog(
    string Id,
    string TaskDescription,
    string Status,
    string AgentThought,
    string ToolOutput,
    DateTime Timestamp
);

public record MemorySchema(
    string TextId,
    string Content,
    string Tag
);

public record FirebaseLogEvent(
    string Key,
    AgentTaskLog Object
);

public record UserBusinessProfile(
    string UserId,
    string BusinessName,
    string BusinessIndustry,
    string BusinessDescription,
    string TargetAudience,
    DateTime CreatedAt,
    string SelectedPlatforms = "LinkedIn;Twitter"
);

public record UserSession(string LocalId, string Email, string IdToken);

public record AuthResult(bool IsSuccess, string Message);

public class AuthSuccessResponse
{
    [JsonPropertyName("localId")] public string LocalId { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("idToken")] public string IdToken { get; set; } = "";
}

public class AuthErrorResponse
{
    [JsonPropertyName("error")] public AuthErrorDetails? Error { get; set; }
}

public class AuthErrorDetails
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public record SearchResultItem(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("publishedDate")] string PublishedDate
);

public record SearchAgentResult(
    string UserId,
    List<SearchResultItem> Items,
    DateTime Timestamp
);

public record SocialPostRecommendation(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("hashtags")] List<string> Hashtags,
    [property: JsonPropertyName("targetAudience")] string TargetAudience,
    [property: JsonPropertyName("rationale")] string Rationale
);

public record SocialMediaAgentResult(
    string UserId,
    List<SocialPostRecommendation> Recommendations,
    DateTime Timestamp
);

public record AgentSchedule(
    string UserId,
    int IntervalHours,
    DateTime LastRunTime,
    DateTime NextRunTime,
    bool IsEnabled
);

public record CeoDirective(
    string Id,
    string DirectiveText,
    string AssignedTo,
    string Status, // "Pending", "Approved", "In Progress", "Completed"
    string AnswerText, // CEO response text if it's a question
    DateTime Timestamp
);

public record AgentPerformanceReview(
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("relevanceAnalysis")] string RelevanceAnalysis,
    [property: JsonPropertyName("optimizationStrategy")] string OptimizationStrategy
);

public record ManagerPerformanceReport(
    string UserId,
    List<AgentPerformanceReview> Reviews,
    DateTime Timestamp
);

public record MemoryCatalogItem(
    string Id,
    string Content,
    string Tag,
    DateTime CreatedTime
);

public record BusinessModelItem(
    [property: JsonPropertyName("sectionName")] string SectionName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("bulletPoints")] List<string> BulletPoints
);

public record BusinessModelVisual(
    string UserId,
    List<BusinessModelItem> Sections,
    DateTime Timestamp
);

public record VideoAdScene(
    [property: JsonPropertyName("sceneNumber")] int SceneNumber,
    [property: JsonPropertyName("visualDescription")] string VisualDescription,
    [property: JsonPropertyName("audioVoiceover")] string AudioVoiceover,
    [property: JsonPropertyName("musicInstructions")] string MusicInstructions
);

public record VideoAdDetail(
    string Id,
    string Title,
    string Platform,
    string CoreMessage,
    string EstimatedDuration,
    List<VideoAdScene> Scenes,
    DateTime CreatedAt
);

public record ImagePosterDetail(
    string Id,
    string Title,
    string Platform,
    string VisualTheme,
    string PrimaryHeadline,
    string BodyCopy,
    string ImagePrompt,
    string CallToAction,
    DateTime CreatedAt,
    string ImageUrl = ""
);

public record BusinessKnowledgeItem(
    string Id,
    string UserId,
    string Title,
    string Content,
    string FileUrl,
    string FileName,
    string FileType,
    long FileSize,
    DateTime CreatedAt
);

public class ChatSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string AgentName { get; set; } = ""; // "CoreCommand", "Ceo", "Documents", "Leads"
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatMessage
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Sender { get; set; } = ""; // "User", "Agent"
    public string Text { get; set; } = "";
    public string Thought { get; set; } = ""; // Optional
    public string ToolOutput { get; set; } = ""; // Optional
    public string AssignedTo { get; set; } = "User"; // CEO assignment target
    public string Status { get; set; } = "Completed"; // Status of execution
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<AgentCommunication>? Communications { get; set; } // Optional trace of direct communications
}

public class AgentCommunication
{
    public string Sender { get; set; } = ""; // e.g., "CeoAgent"
    public string Receiver { get; set; } = ""; // e.g., "LeadsAgent"
    public string TaskGiven { get; set; } = "";
    public string Response { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
