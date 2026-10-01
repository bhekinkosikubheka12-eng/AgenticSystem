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

public class ChatSessionService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;
    
    // In-memory local database fallback
    private readonly ConcurrentDictionary<string, List<ChatSession>> _localSessions = new();
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _localMessages = new();

    public ChatSessionService(IConfiguration config)
    {
        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. Using local in-memory fallback for sessions.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase ChatSession database initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize sessions FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }
    }

    public async Task<List<ChatSession>> GetSessionsAsync(string userId, string agentName)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localSessions.TryGetValue(userId, out var list);
            return list?.Where(s => s.AgentName == agentName).OrderByDescending(s => s.CreatedAt).ToList() ?? new List<ChatSession>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("ChatSessions")
                .Child(userId)
                .OnceAsync<ChatSession>();
            return items.Select(x => x.Object).Where(s => s.AgentName == agentName).OrderByDescending(s => s.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch sessions from Firebase for user {userId}: {ex.Message}. Using in-memory fallback.");
            _localSessions.TryGetValue(userId, out var list);
            return list?.Where(s => s.AgentName == agentName).OrderByDescending(s => s.CreatedAt).ToList() ?? new List<ChatSession>();
        }
    }

    public async Task<ChatSession> CreateSessionAsync(string userId, string agentName, string title)
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            AgentName = agentName,
            Title = title,
            CreatedAt = DateTime.UtcNow
        };

        var list = _localSessions.GetOrAdd(userId, _ => new List<ChatSession>());
        list.Add(session);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return session;
        }

        try
        {
            await _firebaseClient
                .Child("ChatSessions")
                .Child(userId)
                .Child(session.Id)
                .PutAsync(session);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save session to Firebase: {ex.Message}");
        }

        return session;
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(string sessionId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localMessages.TryGetValue(sessionId, out var list);
            return list?.OrderBy(m => m.Timestamp).ToList() ?? new List<ChatMessage>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("ChatMessages")
                .Child(sessionId)
                .OnceAsync<ChatMessage>();
            return items.Select(x => x.Object).OrderBy(m => m.Timestamp).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch messages from Firebase for session {sessionId}: {ex.Message}");
            _localMessages.TryGetValue(sessionId, out var list);
            return list?.OrderBy(m => m.Timestamp).ToList() ?? new List<ChatMessage>();
        }
    }

    public async Task SaveMessageAsync(ChatMessage message)
    {
        var list = _localMessages.GetOrAdd(message.SessionId, _ => new List<ChatMessage>());
        list.RemoveAll(m => m.Id == message.Id);
        list.Add(message);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("ChatMessages")
                .Child(message.SessionId)
                .Child(message.Id)
                .PutAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save message to Firebase: {ex.Message}");
        }
    }
}
