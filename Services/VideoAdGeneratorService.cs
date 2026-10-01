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

public class VideoAdGeneratorService
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;

    public VideoAdGeneratorService(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        FirebaseSearchSocialService searchSocialService)
    {
        _firebaseLogger = firebaseLogger;
        _searchSocialService = searchSocialService;

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );
        _kernel = builder.Build();
    }

    public async Task<VideoAdDetail> GenerateVideoAdAsync(
        string userId, 
        UserBusinessProfile businessProfile, 
        string postPlatform, 
        string postContent, 
        string userVisualGuidance)
    {
        var taskId = $"videoad-{Guid.NewGuid().ToString().Substring(0, 8)}";

        var startLog = new AgentTaskLog(
            taskId,
            $"[VideoAdAgent] Generate Video Ad blueprint for {businessProfile.BusinessName} ({postPlatform})",
            "Running",
            $"Synthesizing campaign post content into a scene-by-scene storyboard. User guidance: \"{userVisualGuidance}\"...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        try
        {
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();

            var systemPrompt = $@"You are a professional Video Advertising Director and Creative Copywriter.
Your business profile context:
- Name: {businessProfile.BusinessName}
- Industry: {businessProfile.BusinessIndustry}
- Description: {businessProfile.BusinessDescription}
- Target Audience: {businessProfile.TargetAudience}

You need to write a complete, high-converting video ad script and scene storyboard concept based on this social media post:
Platform Target: {postPlatform}
Post Content: {postContent}

Additional user visual style or theme guidance:
""{userVisualGuidance}""

Create a detailed video storyboard. You MUST return the result ONLY as a JSON object matching this structure. Do not wrap it in markdown code blocks, do not output anything else.
Format structure:
{{
  ""title"": ""A catchy title/campaign name for the ad video"",
  ""platform"": ""{postPlatform}"",
  ""coreMessage"": ""Core visual message, hook, or value proposition of the video"",
  ""estimatedDuration"": ""e.g. 30 seconds"",
  ""scenes"": [
    {{
      ""sceneNumber"": 1,
      ""visualDescription"": ""Detailed visual description of what is shown on screen, including scene setting, actors, lighting, or animations."",
      ""audioVoiceover"": ""The spoken voiceover script, text overlay copy, or Sound Effects (SFX) description."",
      ""musicInstructions"": ""Instructions for the background music style, tempo, and visual sync cues.""
    }}
  ]
}}";

            var chat = new ChatHistory(systemPrompt);
            chat.AddUserMessage("Generate the ad video storyboard JSON based on the post and guidance.");

            var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
            var cleanResponse = CleanJson(response.Content ?? "");

            VideoAdDetail? parsedAd = null;
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tempAd = JsonSerializer.Deserialize<VideoAdDetailDto>(cleanResponse, options);

                if (tempAd != null)
                {
                    var scenesList = tempAd.Scenes?.Select(s => new VideoAdScene(
                        s.SceneNumber,
                        s.VisualDescription ?? string.Empty,
                        s.AudioVoiceover ?? string.Empty,
                        s.MusicInstructions ?? string.Empty
                    )).ToList() ?? new List<VideoAdScene>();

                    parsedAd = new VideoAdDetail(
                        Guid.NewGuid().ToString().Substring(0, 8),
                        tempAd.Title ?? $"Campaign for {postPlatform}",
                        postPlatform,
                        tempAd.CoreMessage ?? "High-converting promo video",
                        tempAd.EstimatedDuration ?? "30 seconds",
                        scenesList,
                        DateTime.UtcNow
                    );
                }
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"JSON Deserialization failed for VideoAdAgent: {jsonEx.Message}. Raw: {response.Content}");
            }

            // Fallback if parsing failed
            if (parsedAd == null)
            {
                parsedAd = GenerateFallbackVideoAd(businessProfile, postPlatform, postContent, userVisualGuidance);
            }

            await _searchSocialService.SaveVideoAdAsync(userId, parsedAd);

            var endLog = startLog with {
                Status = "Completed",
                AgentThought = $"Generated {parsedAd.Scenes.Count} visual scenes with custom audio and music scores. Stored as campaign ID: {parsedAd.Id}.",
                ToolOutput = $"Video ad storyboard generated successfully.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(endLog);

            return parsedAd;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoAdAgent failed: {ex.Message}");
            var errorLog = startLog with {
                Status = "Failed",
                AgentThought = $"Encountered error: {ex.Message}",
                ToolOutput = "Failed to compile video ad script.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(errorLog);

            var fallbackAd = GenerateFallbackVideoAd(businessProfile, postPlatform, postContent, userVisualGuidance);
            await _searchSocialService.SaveVideoAdAsync(userId, fallbackAd);
            return fallbackAd;
        }
    }

    private string CleanJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "{}";
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

    private VideoAdDetail GenerateFallbackVideoAd(
        UserBusinessProfile businessProfile, 
        string platform, 
        string postContent, 
        string guidance)
    {
        var scenes = new List<VideoAdScene>
        {
            new VideoAdScene(
                1,
                $"Opening shot showing a developer or operations manager sitting at a desk with an interface showing {businessProfile.BusinessName}. Smooth transition to highlights of {businessProfile.BusinessIndustry}.",
                $"Voiceover: \"Struggling with standard procedures in {businessProfile.BusinessIndustry}? Discover what customization really means.\"",
                "Pensive tech synth music, slow tempo building up."
            ),
            new VideoAdScene(
                2,
                "Dynamic graphical screens demonstrating automation features, systems analytics charts, and customer dashboard widgets.",
                $"Voiceover: \"We build targeted systems designed specifically for {businessProfile.TargetAudience}. Speed, uptime, and hyper-relevance in one single place.\"",
                "Upbeat electronic beats kick in, moderate tempo."
            ),
            new VideoAdScene(
                3,
                $"Call to action overlay displaying the {businessProfile.BusinessName} logo and website link.",
                "Voiceover: \"Visit us today and restructure your workflow. Let's build what's next together!\"",
                "Climax tech riser chord, slowly fading out with a clean click sound."
            )
        };

        return new VideoAdDetail(
            Guid.NewGuid().ToString().Substring(0, 8),
            $"Promo Campaign - {platform} Focus",
            platform,
            $"How {businessProfile.BusinessName} helps {businessProfile.TargetAudience} scale",
            "30 seconds",
            scenes,
            DateTime.UtcNow
        );
    }

    // Helper class for parsing DTO structure from Gemini JSON response safely
    private class VideoAdDetailDto
    {
        public string? Title { get; set; }
        public string? Platform { get; set; }
        public string? CoreMessage { get; set; }
        public string? EstimatedDuration { get; set; }
        public List<VideoAdSceneDto>? Scenes { get; set; }
    }

    private class VideoAdSceneDto
    {
        public int SceneNumber { get; set; }
        public string? VisualDescription { get; set; }
        public string? AudioVoiceover { get; set; }
        public string? MusicInstructions { get; set; }
    }
}
