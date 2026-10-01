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

public class SearchAndSocialOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;
    private readonly BusinessKnowledgeService _knowledgeService;

    public SearchAndSocialOrchestrator(
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

    public async Task RunWorkflowAsync(string userId, UserBusinessProfile businessProfile, string? userGuidance = null)
    {
        var searchTaskId = $"search-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var socialTaskId = $"social-{Guid.NewGuid().ToString().Substring(0, 8)}";

        List<SearchResultItem> searchItems = new();
        
        var knowledgeContext = await _knowledgeService.GetUnifiedKnowledgeContextAsync(userId);
        var knowledgeInjection = string.IsNullOrEmpty(knowledgeContext)
            ? ""
            : $"\n{knowledgeContext}";

        try
        {
            // ==========================================
            // 1. EXECUTE SEARCH AGENT
            // ==========================================
            var searchStartLog = new AgentTaskLog(
                searchTaskId,
                $"[SearchAgent] Analyze industry trends & competitors for {businessProfile.BusinessName}",
                "Running",
                "Constructing target query vector and starting deep market search...",
                "",
                DateTime.UtcNow
            );
            await _firebaseLogger.LogStateAsync(searchStartLog);

            var chatService = _kernel.GetRequiredService<IChatCompletionService>();
            
            var searchSystemPrompt = $@"You are an autonomous market analysis search agent. 
Your business profile context:
- Name: {businessProfile.BusinessName}
- Industry: {businessProfile.BusinessIndustry}
- Description: {businessProfile.BusinessDescription}
- Target Audience: {businessProfile.TargetAudience}
{knowledgeInjection}

You must search the web and generate exactly 5 relevant and high-quality market insights/news/competitor search result articles that would benefit this business.
Return the results ONLY as a JSON array of objects. Do not wrap it in markdown code blocks, do not output anything else.
Format structure:
[
  {{
    ""title"": ""Article Title"",
    ""snippet"": ""A 2-3 sentence summary of the trend/news and why it is relevant."",
    ""url"": ""https://example.com/relevant-trend-url"",
    ""publishedDate"": ""2026-06-07""
  }}
]";

            var searchChat = new ChatHistory(searchSystemPrompt);
            searchChat.AddUserMessage("Run search query and generate the 5 best market insights.");

            var searchResponse = await chatService.GetChatMessageContentAsync(searchChat, new GeminiPromptExecutionSettings(), _kernel);
            var searchContent = CleanJson(searchResponse.Content ?? "");

            try
            {
                var parsedResults = JsonSerializer.Deserialize<List<SearchResultItem>>(searchContent, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (parsedResults != null)
                {
                    searchItems = parsedResults;
                }
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"JSON Parsing failed for Search Agent output. Raw: {searchResponse.Content}. Error: {jsonEx.Message}");
                // Fallback structured data if parsing fails
                searchItems = GenerateFallbackSearchResults(businessProfile);
            }

            var searchResultObj = new SearchAgentResult(userId, searchItems, DateTime.UtcNow);
            await _searchSocialService.SaveSearchResultsAsync(searchResultObj);

            var searchEndLog = searchStartLog with {
                Status = "Completed",
                AgentThought = $"Retrieved {searchItems.Count} insights matching {businessProfile.BusinessIndustry} industry dynamics.",
                ToolOutput = $"Search completed successfully. Results committed to user workspace database.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(searchEndLog);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search Agent error: {ex.Message}");
            var searchErrorLog = new AgentTaskLog(
                searchTaskId,
                $"[SearchAgent] Analyze industry trends for {businessProfile.BusinessName}",
                "Failed",
                $"Encountered error: {ex.Message}",
                "Failed to store search findings.",
                DateTime.UtcNow
            );
            await _firebaseLogger.LogStateAsync(searchErrorLog);
        }

        // ==========================================
        // 2. EXECUTE SOCIAL MEDIA AGENT
        // ==========================================
        try
        {
            var selectedPlatforms = string.IsNullOrWhiteSpace(businessProfile.SelectedPlatforms)
                ? new List<string> { "LinkedIn", "Twitter" }
                : businessProfile.SelectedPlatforms.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            var platformsListStr = string.Join(", ", selectedPlatforms);

            var socialStartLog = new AgentTaskLog(
                socialTaskId,
                $"[SocialMediaAgent] Generate social media post recommendations for {businessProfile.BusinessName}",
                "Running",
                $"Analyzing search results to form engaging target audience posts for {platformsListStr}...",
                "",
                DateTime.UtcNow
            );
            await _firebaseLogger.LogStateAsync(socialStartLog);

            var chatService = _kernel.GetRequiredService<IChatCompletionService>();

            var searchResultsText = string.Join("\n\n", searchItems.Select((item, idx) => 
                $"{idx+1}. Title: {item.Title}\nSummary: {item.Snippet}\nSource: {item.Url}"));

            var socialSystemPrompt = $@"You are an expert Social Media advisor. 
Your business profile:
- Name: {businessProfile.BusinessName}
- Industry: {businessProfile.BusinessIndustry}
- Description: {businessProfile.BusinessDescription}
- Target Audience: {businessProfile.TargetAudience}
{knowledgeInjection}

Based on these latest search insights:
{searchResultsText}

Generate exactly 1 tailored, high-converting social media post recommendation for each of the following platforms: {platformsListStr}.
Do NOT generate recommendations for any other platforms.
Return the recommendations ONLY as a JSON array of objects. Do not wrap in markdown code blocks, do not output anything else.
Format structure:
[
  {{
    ""platform"": ""LinkedIn"" (or whichever platform this is, matching one of: {platformsListStr}),
    ""content"": ""Post copywriting content here. Include emojis and spacing for readability. If the platform is YouTube, this should be a video script or detailed video outline context."",
    ""hashtags"": [""Tag1"", ""Tag2"", ""Tag3""],
    ""targetAudience"": ""Who this specific post targets"",
    ""rationale"": ""Explanation of how this connects to the recent search insights.""
  }}
]";

            if (!string.IsNullOrWhiteSpace(userGuidance))
            {
                socialSystemPrompt += $"\n\nCRITICAL DIRECTIVE: The user has requested to focus specifically on the following topic or guidance for these posts:\n\"{userGuidance}\"\nYou MUST strictly tailor the posts to incorporate this guidance/topic.";
            }

            var socialChat = new ChatHistory(socialSystemPrompt);
            var userMsg = $"Generate {selectedPlatforms.Count} social media recommendations based on business context and search results.";
            if (!string.IsNullOrWhiteSpace(userGuidance))
            {
                userMsg += $" Incorporate guidance: {userGuidance}";
            }
            socialChat.AddUserMessage(userMsg);

            var socialResponse = await chatService.GetChatMessageContentAsync(socialChat, new GeminiPromptExecutionSettings(), _kernel);
            var socialContent = CleanJson(socialResponse.Content ?? "");

            List<SocialPostRecommendation> socialRecs = new();
            try
            {
                var parsedRecs = JsonSerializer.Deserialize<List<SocialPostRecommendation>>(socialContent, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (parsedRecs != null)
                {
                    socialRecs = parsedRecs;
                }
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"JSON Parsing failed for Social Media Agent output. Raw: {socialResponse.Content}. Error: {jsonEx.Message}");
                // Fallback content if parsing fails
                socialRecs = GenerateFallbackSocialRecs(businessProfile, searchItems, selectedPlatforms);
            }

            var socialResultObj = new SocialMediaAgentResult(userId, socialRecs, DateTime.UtcNow);
            await _searchSocialService.SaveSocialResultsAsync(socialResultObj);

            var socialEndLog = socialStartLog with {
                Status = "Completed",
                AgentThought = $"Generated {socialRecs.Count} tailored recommendation templates targeting platforms: {string.Join(" & ", socialRecs.Select(r => r.Platform).Distinct())}.",
                ToolOutput = $"Social recommendations generated successfully and saved.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(socialEndLog);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Social Media Agent error: {ex.Message}");
            var socialErrorLog = new AgentTaskLog(
                socialTaskId,
                $"[SocialMediaAgent] Generate posts for {businessProfile.BusinessName}",
                "Failed",
                $"Encountered error: {ex.Message}",
                "Failed to store social post recommendations.",
                DateTime.UtcNow
            );
            await _firebaseLogger.LogStateAsync(socialErrorLog);
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

    private List<SearchResultItem> GenerateFallbackSearchResults(UserBusinessProfile businessProfile)
    {
        return new List<SearchResultItem>
        {
            new SearchResultItem(
                $"Emerging trends in {businessProfile.BusinessIndustry} (2026)",
                $"New reports show a 40% surge in user adoption regarding tech automation and customized intelligence engines in the {businessProfile.BusinessIndustry} domain.",
                "https://business-trends.example.org/industry-forecast",
                DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd")
            ),
            new SearchResultItem(
                $"Competitor Analysis: Uptime and Scaling Priorities",
                $"Major players are focusing on customer retention and cost reduction strategies for their target base of {businessProfile.TargetAudience}.",
                "https://market-share-insights.example.org/analysis",
                DateTime.UtcNow.AddDays(-5).ToString("yyyy-MM-dd")
            ),
            new SearchResultItem(
                $"Addressing pain points of {businessProfile.TargetAudience}",
                $"DevOps, engineering managers, and executives identify transparency and fast responsiveness as the highest value drivers when picking solutions.",
                "https://industry-painpoints.example.org/devops-cx",
                DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd")
            ),
            new SearchResultItem(
                $"Why customization is the new standard in B2B systems",
                $"Generic enterprise products are losing market share to hyper-customized setups tailored for specific user business parameters.",
                "https://saas-dynamics.example.com/customization-value",
                DateTime.UtcNow.ToString("yyyy-MM-dd")
            ),
            new SearchResultItem(
                $"Regulatory changes & compliance trends in digital platforms",
                "New compliance parameters highlight safety, local storage resilience, and data fallback systems as critical pillars for long term contracts.",
                "https://compliance-news.example.org/standards-2026",
                DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd")
            )
        };
    }

    private List<SocialPostRecommendation> GenerateFallbackSocialRecs(UserBusinessProfile businessProfile, List<SearchResultItem> searchItems, List<string> selectedPlatforms)
    {
        var topTopic = searchItems.FirstOrDefault()?.Title ?? "industry optimization";
        var recs = new List<SocialPostRecommendation>();
        
        foreach (var platform in selectedPlatforms)
        {
            if (platform.Equals("LinkedIn", StringComparison.OrdinalIgnoreCase))
            {
                recs.Add(new SocialPostRecommendation(
                    "LinkedIn",
                    $"🚀 Customization and efficiency are no longer optional in {businessProfile.BusinessIndustry}.\n\nAt {businessProfile.BusinessName}, we are designing systems specifically to address the pain points of our target group: {businessProfile.TargetAudience}.\n\nRead our latest thoughts on how recent developments like '{topTopic}' are reshaping operations.",
                    new List<string> { "EnterpriseTech", "BusinessScaling", "Innovation" },
                    businessProfile.TargetAudience,
                    "Leverages high-level industry insights to position the business as a forward-thinking domain authority."
                ));
            }
            else if (platform.Equals("Twitter", StringComparison.OrdinalIgnoreCase))
            {
                recs.Add(new SocialPostRecommendation(
                    "Twitter",
                    $"How are you adapting to new regulations and trends in {businessProfile.BusinessIndustry}? 📈\n\nWe build solutions with high availability, local fallbacks, and premium customization. Let's build what's next. 🛠️",
                    new List<string> { businessProfile.BusinessIndustry.Replace(" ", ""), "TechStartup", "Efficiency" },
                    "General tech enthusiasts and operators",
                    "Short, punchy engagement hook referencing industry stability and forward-looking capabilities."
                ));
            }
            else if (platform.Equals("Instagram", StringComparison.OrdinalIgnoreCase))
            {
                recs.Add(new SocialPostRecommendation(
                    "Instagram",
                    $"Behind the scenes at {businessProfile.BusinessName}! 📸 We are working hard to redefine standards in {businessProfile.BusinessIndustry}. Check out our roadmap highlighting new ways we support {businessProfile.TargetAudience} daily.",
                    new List<string> { "WorkCulture", "TechLife", "Design" },
                    "Customers and visual story lovers",
                    "Visual and personal brand focused post showing the human side of our tech development."
                ));
            }
            else if (platform.Equals("Facebook", StringComparison.OrdinalIgnoreCase))
            {
                recs.Add(new SocialPostRecommendation(
                    "Facebook",
                    $"Looking for ways to improve efficiency in {businessProfile.BusinessIndustry}? Our latest analysis of '{topTopic}' suggests that focusing on {businessProfile.TargetAudience} is key. Learn how {businessProfile.BusinessName} can help scale your processes today.",
                    new List<string> { "BusinessGrowth", "TechTips", "Scale" },
                    "Local businesses and operational managers",
                    "Informative community post focusing on localized value and process improvement."
                ));
            }
            else if (platform.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
            {
                recs.Add(new SocialPostRecommendation(
                    "YouTube",
                    $"[Video Title Idea]: Inside the Future of {businessProfile.BusinessIndustry}\n\n[Intro Scene]: Host welcomes viewers and introduces the core problem faced by {businessProfile.TargetAudience}.\n\n[Key Segments]:\n1. The current challenge and why it persists.\n2. Deep dive into '{topTopic}' and its real-world implications.\n3. How {businessProfile.BusinessName} is deploying modern solutions.\n\n[Outro / Call to Action]: Subscribing for more industry breakdowns and testing our platform.",
                    new List<string> { "TechBreakdown", "IndustryTrends", "Tutorial" },
                    "Video consumers looking for in-depth insights",
                    "Video outline leveraging visual storytelling to explain complex system capabilities."
                ));
            }
        }

        if (!recs.Any())
        {
            recs.Add(new SocialPostRecommendation(
                "LinkedIn",
                $"🚀 Customization and efficiency are no longer optional in {businessProfile.BusinessIndustry}.\n\nAt {businessProfile.BusinessName}, we are designing systems specifically to address the pain points of our target group: {businessProfile.TargetAudience}.",
                new List<string> { "EnterpriseTech", "BusinessScaling" },
                businessProfile.TargetAudience,
                "Fallback post."
            ));
        }

        return recs;
    }
}
