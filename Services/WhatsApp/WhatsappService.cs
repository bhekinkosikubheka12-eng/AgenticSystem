using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Firebase.Database;
using Firebase.Database.Query;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class WhatsappService
{
    private readonly HttpClient _httpClient;
    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;
    private readonly IConfiguration _config;

    // Local fallbacks in memory
    private static readonly ConcurrentDictionary<string, WhatsappConfig> _localConfigs = new();

    public WhatsappService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. Using local in-memory fallback for WhatsApp configurations.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase WhatsApp database initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize WhatsApp FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }
    }

    private string GetSecretKey()
    {
        return _config["AiConfig:FirebaseApiKey"] ?? _config["AiConfig:GeminiApiKey"] ?? "FallbackSecureWhatsappConfigKey_2026!";
    }

    public async Task<WhatsappConfig> GetConfigAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            if (!_localConfigs.TryGetValue(userId, out var config))
            {
                config = new WhatsappConfig { UserId = userId };
                _localConfigs[userId] = config;
            }

            // Decrypt the token in returned config
            var decryptedConfig = CloneConfig(config);
            if (!string.IsNullOrEmpty(config.AccessToken))
            {
                decryptedConfig.AccessToken = EncryptionHelper.Decrypt(
                    config.AccessToken,
                    GetSecretKey(),
                    userId
                );
            }
            return decryptedConfig;
        }

        try
        {
            var config = await _firebaseClient
                .Child("WhatsappConfigs")
                .Child(userId)
                .OnceSingleAsync<WhatsappConfig>();

            if (config == null)
            {
                config = new WhatsappConfig { UserId = userId };
                await SaveConfigAsync(userId, config);
            }
            else if (!string.IsNullOrEmpty(config.AccessToken))
            {
                config.AccessToken = EncryptionHelper.Decrypt(
                    config.AccessToken,
                    GetSecretKey(),
                    userId
                );
            }
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch WhatsApp config for user {userId}: {ex.Message}. Using in-memory fallback.");
            if (!_localConfigs.TryGetValue(userId, out var config))
            {
                config = new WhatsappConfig { UserId = userId };
                _localConfigs[userId] = config;
            }
            var decryptedConfig = CloneConfig(config);
            if (!string.IsNullOrEmpty(config.AccessToken))
            {
                decryptedConfig.AccessToken = EncryptionHelper.Decrypt(
                    config.AccessToken,
                    GetSecretKey(),
                    userId
                );
            }
            return decryptedConfig;
        }
    }

    public async Task SaveConfigAsync(string userId, WhatsappConfig config)
    {
        config.UserId = userId;

        string encryptedToken = string.Empty;
        if (!string.IsNullOrEmpty(config.AccessToken))
        {
            encryptedToken = EncryptionHelper.Encrypt(
                config.AccessToken,
                GetSecretKey(),
                userId
            );
        }

        var secureConfig = CloneConfig(config);
        secureConfig.AccessToken = encryptedToken;

        _localConfigs[userId] = secureConfig;

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("WhatsappConfigs")
                .Child(userId)
                .PutAsync(secureConfig);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save WhatsApp config to Firebase for user {userId}: {ex.Message}");
        }
    }

    public async Task<(bool success, string challenge)> VerifyWebhookAsync(string userId, string mode, string verifyToken, string challenge)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(verifyToken))
        {
            return (false, string.Empty);
        }

        var config = await GetConfigAsync(userId);
        if (config != null && config.VerifyToken == verifyToken && !string.IsNullOrEmpty(verifyToken))
        {
            return (true, challenge);
        }

        return (false, string.Empty);
    }

    public async Task<bool> SendWhatsappMessageAsync(string userId, string recipientPhone, string messageText)
    {
        var config = await GetConfigAsync(userId);
        if (config == null || !config.IsConfigured)
        {
            Console.WriteLine($"WhatsApp config is not configured for user {userId}. Cannot send message.");
            return false;
        }

        var cleanPhone = CleanPhoneNumber(recipientPhone);
        var url = $"https://graph.facebook.com/v20.0/{config.PhoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = cleanPhone,
            type = "text",
            text = new
            {
                preview_url = false,
                body = messageText
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"WhatsApp message sent successfully to {cleanPhone}. Response: {responseBody}");
                return true;
            }
            else
            {
                Console.WriteLine($"Failed to send WhatsApp message. Status code: {response.StatusCode}, Response: {responseBody}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception while sending WhatsApp message: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ProcessWebhookPayloadAsync(
        string userId,
        string jsonPayload,
        LeadsCenterOrchestrator orchestrator,
        FirebaseProfileService profileService)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("object", out var objectProp) || objectProp.GetString() != "whatsapp_business_account")
            {
                return false;
            }

            if (!root.TryGetProperty("entry", out var entryArray) || entryArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var entry in entryArray.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changesArray) || changesArray.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var change in changesArray.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var valueProp))
                        continue;

                    if (!valueProp.TryGetProperty("messages", out var messagesArray) || messagesArray.ValueKind != JsonValueKind.Array)
                        continue;

                    // Build profile wa_id -> profile name dictionary
                    var contactNames = new Dictionary<string, string>();
                    if (valueProp.TryGetProperty("contacts", out var contactsArray) && contactsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var contact in contactsArray.EnumerateArray())
                        {
                            var waId = contact.TryGetProperty("wa_id", out var waIdProp) ? waIdProp.GetString() : null;
                            var profile = contact.TryGetProperty("profile", out var profileProp) ? profileProp : (JsonElement?)null;
                            var name = profile?.TryGetProperty("name", out var nameProp) == true ? nameProp.GetString() : null;
                            if (!string.IsNullOrEmpty(waId) && !string.IsNullOrEmpty(name))
                            {
                                contactNames[waId] = name;
                            }
                        }
                    }

                    foreach (var msg in messagesArray.EnumerateArray())
                    {
                        // Only process text messages
                        if (!msg.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "text")
                            continue;

                        var from = msg.TryGetProperty("from", out var fromProp) ? fromProp.GetString() : null;
                        var textObj = msg.TryGetProperty("text", out var textProp) ? textProp : (JsonElement?)null;
                        var textBody = textObj?.TryGetProperty("body", out var bodyProp) == true ? bodyProp.GetString() : null;

                        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(textBody))
                            continue;

                        contactNames.TryGetValue(from, out var leadName);
                        if (string.IsNullOrEmpty(leadName))
                        {
                            leadName = $"WhatsApp Lead ({from})";
                        }

                        var profileObj = await profileService.GetProfileAsync(userId);
                        if (profileObj == null)
                        {
                            Console.WriteLine($"Profile not found for user {userId}, skipping incoming WhatsApp lead.");
                            continue;
                        }

                        // Process incoming lead via Orchestrator
                        await orchestrator.ProcessIncomingLeadAsync(
                            userId,
                            "WhatsApp",
                            leadName,
                            from,
                            textBody,
                            profileObj
                        );
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing WhatsApp webhook payload: {ex.Message}");
            return false;
        }
    }

    private string CleanPhoneNumber(string phone)
    {
        if (string.IsNullOrEmpty(phone)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in phone)
        {
            if (char.IsDigit(c))
            {
                sb.Append(c);
            }
        }
        var cleaned = sb.ToString();
        if (cleaned.StartsWith("00"))
        {
            cleaned = cleaned.Substring(2);
        }
        return cleaned;
    }

    private WhatsappConfig CloneConfig(WhatsappConfig source)
    {
        return new WhatsappConfig
        {
            UserId = source.UserId,
            AccessToken = source.AccessToken,
            PhoneNumberId = source.PhoneNumberId,
            WabaId = source.WabaId,
            VerifyToken = source.VerifyToken
        };
    }
}
