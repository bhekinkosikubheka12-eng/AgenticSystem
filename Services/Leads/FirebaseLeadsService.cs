using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class FirebaseLeadsService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;
    private readonly IConfiguration _config;

    // Local fallbacks in memory
    private readonly ConcurrentDictionary<string, LeadsSettings> _localSettings = new();
    private readonly ConcurrentDictionary<string, List<LeadMessage>> _localMessages = new();
    private readonly ConcurrentDictionary<string, EmailConfig> _localEmailConfigs = new();

    public FirebaseLeadsService(IConfiguration config)
    {
        _config = config;
        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. Using local in-memory fallback for leads operations.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase Leads database initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize leads FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }
    }

    #region Settings Operations

    public async Task<LeadsSettings> GetLeadsSettingsAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            if (!_localSettings.TryGetValue(userId, out var settings))
            {
                settings = new LeadsSettings { UserId = userId };
                _localSettings[userId] = settings;
            }
            return settings;
        }

        try
        {
            var settings = await _firebaseClient
                .Child("LeadsSettings")
                .Child(userId)
                .OnceSingleAsync<LeadsSettings>();

            if (settings == null)
            {
                settings = new LeadsSettings { UserId = userId };
                await SaveLeadsSettingsAsync(settings);
            }
            return settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch leads settings for user {userId}: {ex.Message}. Using in-memory fallback.");
            if (!_localSettings.TryGetValue(userId, out var settings))
            {
                settings = new LeadsSettings { UserId = userId };
                _localSettings[userId] = settings;
            }
            return settings;
        }
    }

    public async Task SaveLeadsSettingsAsync(LeadsSettings settings)
    {
        _localSettings[settings.UserId] = settings;

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("LeadsSettings")
                .Child(settings.UserId)
                .PutAsync(settings);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save leads settings to Firebase for user {settings.UserId}: {ex.Message}");
        }
    }

    #endregion

    #region Lead Messages Operations

    public async Task<List<LeadMessage>> GetLeadMessagesAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localMessages.TryGetValue(userId, out var list);
            return list?.OrderByDescending(m => m.ReceivedAt).ToList() ?? new List<LeadMessage>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("LeadsMessages")
                .Child(userId)
                .OnceAsync<LeadMessage>();

            return items.Select(x => x.Object).OrderByDescending(m => m.ReceivedAt).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch lead messages from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localMessages.TryGetValue(userId, out var list);
            return list?.OrderByDescending(m => m.ReceivedAt).ToList() ?? new List<LeadMessage>();
        }
    }

    public async Task SaveLeadMessageAsync(string userId, LeadMessage message)
    {
        var list = _localMessages.GetOrAdd(userId, _ => new List<LeadMessage>());
        list.RemoveAll(m => m.Id == message.Id);
        list.Add(message);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("LeadsMessages")
                .Child(userId)
                .Child(message.Id)
                .PutAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save lead message to Firebase: {ex.Message}");
        }
    }

    public async Task DeleteLeadMessageAsync(string userId, string messageId)
    {
        if (_localMessages.TryGetValue(userId, out var list))
        {
            list.RemoveAll(m => m.Id == messageId);
        }

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("LeadsMessages")
                .Child(userId)
                .Child(messageId)
                .DeleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete lead message from Firebase: {ex.Message}");
        }
    }

    #endregion

    #region Email Configuration Operations

    public async Task<EmailConfig?> GetEmailConfigAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            if (!_localEmailConfigs.TryGetValue(userId, out var config))
            {
                config = GetDefaultEmailConfig(userId);
                _localEmailConfigs[userId] = config;
            }
            
            // Decrypt the password in returned config
            var decryptedConfig = CloneConfig(config);
            decryptedConfig.EncryptedPassword = EncryptionHelper.Decrypt(
                config.EncryptedPassword, 
                GetSecretKey(), 
                userId
            );
            return decryptedConfig;
        }

        try
        {
            var config = await _firebaseClient
                .Child("EmailConfigs")
                .Child(userId)
                .OnceSingleAsync<EmailConfig>();

            if (config == null)
            {
                config = GetDefaultEmailConfig(userId);
                await SaveEmailConfigAsync(userId, config);
            }
            else
            {
                config.EncryptedPassword = EncryptionHelper.Decrypt(
                    config.EncryptedPassword, 
                    GetSecretKey(), 
                    userId
                );
            }
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch email config for user {userId}: {ex.Message}. Using in-memory fallback.");
            if (!_localEmailConfigs.TryGetValue(userId, out var config))
            {
                config = GetDefaultEmailConfig(userId);
                _localEmailConfigs[userId] = config;
            }
            var decryptedConfig = CloneConfig(config);
            decryptedConfig.EncryptedPassword = EncryptionHelper.Decrypt(
                config.EncryptedPassword, 
                GetSecretKey(), 
                userId
            );
            return decryptedConfig;
        }
    }

    public async Task SaveEmailConfigAsync(string userId, EmailConfig config)
    {
        config.UserId = userId;
        var encryptedPassword = EncryptionHelper.Encrypt(
            config.EncryptedPassword, 
            GetSecretKey(), 
            userId
        );

        // Store a copy with encrypted password in the database/in-memory local storage
        var secureConfig = CloneConfig(config);
        secureConfig.EncryptedPassword = encryptedPassword;

        _localEmailConfigs[userId] = secureConfig;

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("EmailConfigs")
                .Child(userId)
                .PutAsync(secureConfig);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save email config to Firebase for user {userId}: {ex.Message}");
        }
    }

    private string GetSecretKey()
    {
        return _config["AiConfig:FirebaseApiKey"] ?? _config["AiConfig:GeminiApiKey"] ?? "FallbackSecureEmailConfigKey_2026!";
    }

    private EmailConfig GetDefaultEmailConfig(string userId)
    {
        return new EmailConfig
        {
            UserId = userId,
            Username = string.Empty,
            EncryptedPassword = string.Empty,
            IncomingServer = string.Empty,
            ImapPort = 993,
            Pop3Port = 995,
            OutgoingServer = string.Empty,
            SmtpPort = 465,
            IncomingProtocol = "IMAP",
            RequiresAuthentication = true
        };
    }

    private EmailConfig CloneConfig(EmailConfig source)
    {
        return new EmailConfig
        {
            UserId = source.UserId,
            Username = source.Username,
            EncryptedPassword = source.EncryptedPassword,
            IncomingServer = source.IncomingServer,
            ImapPort = source.ImapPort,
            Pop3Port = source.Pop3Port,
            OutgoingServer = source.OutgoingServer,
            SmtpPort = source.SmtpPort,
            IncomingProtocol = source.IncomingProtocol,
            RequiresAuthentication = source.RequiresAuthentication
        };
    }

    #endregion
}

