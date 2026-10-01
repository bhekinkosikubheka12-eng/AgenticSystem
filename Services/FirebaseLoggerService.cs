using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class FirebaseLoggerService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly Subject<FirebaseLogEvent> _localFallbackSubject = new();
    private readonly bool _useLocalFallback = false;

    public FirebaseLoggerService(IConfiguration config)
    {
        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or a placeholder. Using in-memory fallback logger service.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase Logger initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }
    }

    public async Task LogStateAsync(AgentTaskLog log)
    {
        if (log.Status == "Running" && BillingService.Instance != null)
        {
            await BillingService.Instance.EnforceLimitsAsync(log.Id, log.TaskDescription);
        }

        // Wrap log in our custom FirebaseLogEvent and stream to local subscribers
        var logEvent = new FirebaseLogEvent(log.Id, log);
        _localFallbackSubject.OnNext(logEvent);

        if (_useLocalFallback || _firebaseClient == null)
        {
            Console.WriteLine($"[Local Fallback Log] Task ID: {log.Id.Substring(0, Math.Min(log.Id.Length, 8))} | Status: {log.Status} | Thought: {log.AgentThought}");
            return;
        }

        try
        {
            await _firebaseClient
                .Child("ExecutionLogs")
                .Child(log.Id)
                .PutAsync(log);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Firebase LogStateAsync failed: {ex.Message}. Streamed to local fallback.");
        }
    }

    public IObservable<FirebaseLogEvent> SubscribeToLogs()
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            return _localFallbackSubject.AsObservable();
        }

        try
        {
            // Subscribe to Firebase stream and map internal FirebaseObject to our FirebaseLogEvent
            var firebaseObservable = _firebaseClient
                .Child("ExecutionLogs")
                .AsObservable<AgentTaskLog>()
                .Where(x => x != null && x.Object != null)
                .Select(x => new FirebaseLogEvent(x.Key, x.Object));
            
            // Merge with local subject so local edits/triggers are immediately captured in the UI
            return firebaseObservable.Merge(_localFallbackSubject);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Firebase subscription failed: {ex.Message}. Falling back to local observable.");
            return _localFallbackSubject.AsObservable();
        }
    }
}
