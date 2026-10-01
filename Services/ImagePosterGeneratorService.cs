using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class ImagePosterGeneratorService
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseSearchSocialService _searchSocialService;
    private readonly FirebaseAuthService _authService;
    private readonly IConfiguration _config;

    public ImagePosterGeneratorService(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        FirebaseSearchSocialService searchSocialService,
        FirebaseAuthService authService)
    {
        _config = config;
        _firebaseLogger = firebaseLogger;
        _searchSocialService = searchSocialService;
        _authService = authService;

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );
        _kernel = builder.Build();
    }

    public async Task<ImagePosterDetail> GenerateImagePosterAsync(
        string userId, 
        UserBusinessProfile businessProfile, 
        string postPlatform, 
        string postContent, 
        string userVisualGuidance)
    {
        var taskId = $"poster-{Guid.NewGuid().ToString().Substring(0, 8)}";

        var startLog = new AgentTaskLog(
            taskId,
            $"[ImagePosterAgent] Generate Poster design blueprint for {businessProfile.BusinessName} ({postPlatform})",
            "Running",
            $"Synthesizing campaign post content into layout layers. User guidance: \"{userVisualGuidance}\"...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        try
        {
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();

            var systemPrompt = $@"You are a professional Creative Design Director and Graphic Designer.
Your business profile context:
- Name: {businessProfile.BusinessName}
- Industry: {businessProfile.BusinessIndustry}
- Description: {businessProfile.BusinessDescription}
- Target Audience: {businessProfile.TargetAudience}

You need to draft a complete marketing Image Poster graphic concept and copy script based on this social media post:
Platform Target: {postPlatform}
Post Content: {postContent}

Additional user visual style or brand theme instructions:
""{userVisualGuidance}""

Create a detailed poster layout blueprint. You MUST return the result ONLY as a JSON object matching this structure. Do not wrap it in markdown code blocks, do not output anything else.
Format structure:
{{
  ""title"": ""A catchy design concept/campaign name for the poster"",
  ""platform"": ""{postPlatform}"",
  ""visualTheme"": ""Detailed description of the visual layout theme, color guidelines, graphics, fonts, and background style."",
  ""primaryHeadline"": ""Primary poster headline copy (short, punchy overlay text)"",
  ""bodyCopy"": ""Supporting overlay text details or key bullets (1-2 sentences max)"",
  ""imagePrompt"": ""An optimized, detailed text-to-image prompt (e.g. for Midjourney, DALL-E, or Gemini) to generate the exact central graphic visual for this poster. Avoid generic descriptions, use vivid details, styles, lighting, and composition terms."",
  ""callToAction"": ""Call to action text at the footer (e.g. Scan QR Code, or Visit acme.com/offer)""
}}";

            var chat = new ChatHistory(systemPrompt);
            chat.AddUserMessage("Generate the poster blueprint JSON based on the post and guidance.");

            var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
            var cleanResponse = CleanJson(response.Content ?? "");

            ImagePosterDetail? parsedPoster = null;
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tempPoster = JsonSerializer.Deserialize<ImagePosterDetailDto>(cleanResponse, options);

                if (tempPoster != null)
                {
                    parsedPoster = new ImagePosterDetail(
                        Guid.NewGuid().ToString().Substring(0, 8),
                        tempPoster.Title ?? $"Poster - {postPlatform}",
                        postPlatform,
                        tempPoster.VisualTheme ?? "Modern corporate branding style",
                        tempPoster.PrimaryHeadline ?? "Redefining limits",
                        tempPoster.BodyCopy ?? "Discover our bespoke enterprise solutions today.",
                        tempPoster.ImagePrompt ?? "Professional tech office workspace graphic, clean rendering, soft glow.",
                        tempPoster.CallToAction ?? $"Visit {businessProfile.BusinessName} to learn more",
                        DateTime.UtcNow,
                        "" // ImageUrl (empty initially)
                    );
                }
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"JSON Deserialization failed for ImagePosterAgent: {jsonEx.Message}. Raw: {response.Content}");
            }

            // Fallback if parsing failed
            if (parsedPoster == null)
            {
                parsedPoster = GenerateFallbackPoster(businessProfile, postPlatform, postContent, userVisualGuidance);
            }

            // Perform image generation and upload to Firebase Storage
            string imageUrl = "";
            if (!string.IsNullOrEmpty(parsedPoster.ImagePrompt))
            {
                try
                {
                    var imageLog = startLog with {
                        Status = "Running",
                        AgentThought = $"Invoking Make webhook to generate poster graphics from prompt: \"{parsedPoster.ImagePrompt}\"...",
                        Timestamp = DateTime.UtcNow
                    };
                    await _firebaseLogger.LogStateAsync(imageLog);

                    byte[] imageBytes = await GenerateImageBytesAsync(parsedPoster.ImagePrompt);

                    var uploadLog = startLog with {
                        Status = "Running",
                        AgentThought = $"Uploading generated graphic to Firebase Storage under bucket...",
                        Timestamp = DateTime.UtcNow
                    };
                    await _firebaseLogger.LogStateAsync(uploadLog);

                    var idToken = _authService.CurrentUser?.IdToken;
                    imageUrl = await UploadImageToStorageAsync(userId, parsedPoster.Id, imageBytes, idToken);
                }
                catch (Exception imgEx)
                {
                    Console.WriteLine($"[ImagePosterAgent] Failed to generate/upload image: {imgEx.Message}");
                    var warningLog = startLog with {
                        Status = "Running",
                        AgentThought = $"Warning: Image generation or storage upload failed ({imgEx.Message}). Storing blueprint without custom image.",
                        Timestamp = DateTime.UtcNow
                    };
                    await _firebaseLogger.LogStateAsync(warningLog);
                }
            }

            if (!string.IsNullOrEmpty(imageUrl))
            {
                parsedPoster = parsedPoster with { ImageUrl = imageUrl };
            }

            await _searchSocialService.SaveImagePosterAsync(userId, parsedPoster);

            var endLog = startLog with {
                Status = "Completed",
                AgentThought = $"Designed poster layout: \"{parsedPoster.PrimaryHeadline}\" for platform {parsedPoster.Platform}. Stored as concept ID: {parsedPoster.Id}. Image uploaded: {(!string.IsNullOrEmpty(imageUrl)).ToString()}",
                ToolOutput = $"Image poster script blueprint and graphic generated successfully.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(endLog);

            return parsedPoster;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ImagePosterAgent failed: {ex.Message}");
            var errorLog = startLog with {
                Status = "Failed",
                AgentThought = $"Encountered error: {ex.Message}",
                ToolOutput = "Failed to compile image poster storyboard concept.",
                Timestamp = DateTime.UtcNow
            };
            await _firebaseLogger.LogStateAsync(errorLog);

            var fallbackPoster = GenerateFallbackPoster(businessProfile, postPlatform, postContent, userVisualGuidance);
            await _searchSocialService.SaveImagePosterAsync(userId, fallbackPoster);
            return fallbackPoster;
        }
    }

    public async Task<string> GenerateAndSaveImageOnlyAsync(string userId, ImagePosterDetail poster)
    {
        byte[] imageBytes = await GenerateImageBytesAsync(poster.ImagePrompt);
        var idToken = _authService.CurrentUser?.IdToken;
        string imageUrl = await UploadImageToStorageAsync(userId, poster.Id, imageBytes, idToken);

        // Update database record with the new URL
        var updatedPoster = poster with { ImageUrl = imageUrl };
        await _searchSocialService.SaveImagePosterAsync(userId, updatedPoster);

        return imageUrl;
    }

    private async Task<byte[]> GenerateImageBytesAsync(string prompt)
    {
        using var client = new HttpClient();
        var payload = new { prompt = prompt };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("https://hook.us2.make.com/zw8fm8n6rehn2b154w6snnw7gawxhe9q", content);
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(responseString))
        {
            throw new Exception("Webhook returned an empty response.");
        }

        responseString = responseString.Trim();

        // Check if the response is a valid URL
        if (Uri.TryCreate(responseString, UriKind.Absolute, out var uriResult) && 
            (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
        {
            return await client.GetByteArrayAsync(uriResult);
        }

        // Try parsing response as a JSON string (e.g. if the webhook returned a JSON-encoded string)
        try
        {
            var unescaped = JsonSerializer.Deserialize<string>(responseString);
            if (!string.IsNullOrEmpty(unescaped))
            {
                if (Uri.TryCreate(unescaped, UriKind.Absolute, out var unescapedUri) && 
                    (unescapedUri.Scheme == Uri.UriSchemeHttp || unescapedUri.Scheme == Uri.UriSchemeHttps))
                {
                    return await client.GetByteArrayAsync(unescapedUri);
                }
                
                string base64Unescaped = unescaped;
                if (base64Unescaped.Contains(","))
                {
                    base64Unescaped = base64Unescaped.Substring(base64Unescaped.IndexOf(",") + 1);
                }
                return Convert.FromBase64String(base64Unescaped);
            }
        }
        catch
        {
            // Ignore JSON parsing errors
        }

        // Check if the response is a base64-encoded image
        string base64Data = responseString;
        if (base64Data.Contains(","))
        {
            base64Data = base64Data.Substring(base64Data.IndexOf(",") + 1);
        }

        try
        {
            return Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            throw new Exception("Webhook response could not be parsed as a URL or Base64 image. Response: " + responseString);
        }
    }

    private async Task<string> UploadImageToStorageAsync(string userId, string posterId, byte[] imageBytes, string? idToken)
    {
        using var httpClient = new HttpClient();

        var bucket = _config["AiConfig:FirebaseStorageBucket"];
        if (string.IsNullOrEmpty(bucket))
        {
            var firebaseUrl = _config["AiConfig:FirebaseUrl"] ?? "";
            if (!string.IsNullOrEmpty(firebaseUrl))
            {
                try
                {
                    var uri = new Uri(firebaseUrl);
                    var host = uri.Host;
                    var parts = host.Split('.');
                    if (parts.Length > 0)
                    {
                        var projectId = parts[0];
                        if (projectId.EndsWith("-default-rtdb"))
                        {
                            projectId = projectId.Substring(0, projectId.Length - "-default-rtdb".Length);
                        }
                        bucket = $"{projectId}.appspot.com";
                    }
                }
                catch
                {
                    bucket = "humanicbotagent-default.appspot.com";
                }
            }
            else
            {
                bucket = "humanicbotagent-default.appspot.com";
            }
        }

        var objectPath = $"ImagePosters/{userId}/{posterId}.png";
        var escapedPath = Uri.EscapeDataString(objectPath);
        var url = $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o?name={escapedPath}";

        var content = new ByteArrayContent(imageBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        if (!string.IsNullOrEmpty(idToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        }

        var response = await httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new Exception($"Firebase Storage upload failed: {response.StatusCode} - {err}");
        }

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);

        string downloadToken = "";
        if (doc.RootElement.TryGetProperty("downloadTokens", out var tokenProp))
        {
            downloadToken = tokenProp.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(downloadToken))
        {
            return $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o/{escapedPath}?alt=media";
        }

        return $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o/{escapedPath}?alt=media&token={downloadToken}";
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

    private ImagePosterDetail GenerateFallbackPoster(
        UserBusinessProfile businessProfile, 
        string platform, 
        string postContent, 
        string guidance)
    {
        return new ImagePosterDetail(
            Guid.NewGuid().ToString().Substring(0, 8),
            $"Visual Campaign - {platform} Poster",
            platform,
            $"Premium dark blue background with abstract geometric connections representing {businessProfile.BusinessIndustry}.",
            $"Scale Your Operations with {businessProfile.BusinessName}",
            $"Bespeaking customized intelligence workflows designed specifically for {businessProfile.TargetAudience}.",
            $"Ultra-detailed cinematic graphic of a hyper-futuristic glowing holographic globe, neural link network, data streams around, sharp focus, 8k resolution, photorealistic, neon highlights.",
            $"Get started today at {businessProfile.BusinessName.Replace(" ", "").ToLower()}.com",
            DateTime.UtcNow,
            "" // ImageUrl
        );
    }

    private class ImagePosterDetailDto
    {
        public string? Title { get; set; }
        public string? Platform { get; set; }
        public string? VisualTheme { get; set; }
        public string? PrimaryHeadline { get; set; }
        public string? BodyCopy { get; set; }
        public string? ImagePrompt { get; set; }
        public string? CallToAction { get; set; }
    }
}
