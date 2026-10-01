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

public class FirebaseSearchSocialService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;

    // Local in-memory dictionary fallbacks for multi-user support (RAM fallback)
    private readonly ConcurrentDictionary<string, SearchAgentResult> _localSearchResults = new();
    private readonly ConcurrentDictionary<string, SocialMediaAgentResult> _localSocialResults = new();
    private readonly ConcurrentDictionary<string, AgentSchedule> _localSchedules = new();
    private readonly ConcurrentDictionary<string, List<CeoDirective>> _localCeoDirectives = new();
    private readonly ConcurrentDictionary<string, ManagerPerformanceReport> _localManagerReports = new();
    private readonly ConcurrentDictionary<string, List<MemoryCatalogItem>> _localMemoryCatalog = new();
    private readonly ConcurrentDictionary<string, BusinessModelVisual> _localBusinessModels = new();
    private readonly ConcurrentDictionary<string, List<VideoAdDetail>> _localVideoAds = new();
    private readonly ConcurrentDictionary<string, List<ImagePosterDetail>> _localImagePosters = new();

    public FirebaseSearchSocialService(IConfiguration config)
    {
        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. Using local in-memory fallback for search/social operations.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase Search & Social service database initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize search/social FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }

        // Hook into QdrantMemoryService saved event to catalog memories
        QdrantMemoryService.OnKnowledgeSaved += async (id, text, tags, userId) =>
        {
            var targetUserId = userId ?? "system-default";
            var newItem = new MemoryCatalogItem(id, text, tags, DateTime.UtcNow);
            await SaveMemoryCatalogItemAsync(targetUserId, newItem);
        };
    }

    #region Search Results Operations

    public async Task<SearchAgentResult?> GetSearchResultsAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localSearchResults.TryGetValue(userId, out var result);
            return result;
        }

        try
        {
            return await _firebaseClient
                .Child("SearchAgentResults")
                .Child(userId)
                .OnceSingleAsync<SearchAgentResult>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch search results for user {userId} from Firebase: {ex.Message}. Using in-memory fallback.");
            _localSearchResults.TryGetValue(userId, out var result);
            return result;
        }
    }

    public async Task SaveSearchResultsAsync(SearchAgentResult result)
    {
        _localSearchResults[result.UserId] = result;

        if (_useLocalFallback || _firebaseClient == null)
        {
            Console.WriteLine($"[Local Save] Search Results for user: {result.UserId} | Count: {result.Items.Count}");
            return;
        }

        try
        {
            await _firebaseClient
                .Child("SearchAgentResults")
                .Child(result.UserId)
                .PutAsync(result);
            Console.WriteLine($"Search results saved successfully to Firebase for user {result.UserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save search results to Firebase for user {result.UserId}: {ex.Message}");
        }
    }

    #endregion

    #region Social Media Recommendations Operations

    public async Task<SocialMediaAgentResult?> GetSocialResultsAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localSocialResults.TryGetValue(userId, out var result);
            return result;
        }

        try
        {
            return await _firebaseClient
                .Child("SocialMediaAgentResults")
                .Child(userId)
                .OnceSingleAsync<SocialMediaAgentResult>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch social media recommendations for user {userId} from Firebase: {ex.Message}. Using in-memory fallback.");
            _localSocialResults.TryGetValue(userId, out var result);
            return result;
        }
    }

    public async Task SaveSocialResultsAsync(SocialMediaAgentResult result)
    {
        _localSocialResults[result.UserId] = result;

        if (_useLocalFallback || _firebaseClient == null)
        {
            Console.WriteLine($"[Local Save] Social Recommendations for user: {result.UserId} | Count: {result.Recommendations.Count}");
            return;
        }

        try
        {
            await _firebaseClient
                .Child("SocialMediaAgentResults")
                .Child(result.UserId)
                .PutAsync(result);
            Console.WriteLine($"Social media recommendations saved successfully to Firebase for user {result.UserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save social media recommendations to Firebase for user {result.UserId}: {ex.Message}");
        }
    }

    #endregion

    #region Schedule Operations

    public async Task<AgentSchedule?> GetScheduleAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localSchedules.TryGetValue(userId, out var schedule);
            return schedule;
        }

        try
        {
            return await _firebaseClient
                .Child("AgentSchedules")
                .Child(userId)
                .OnceSingleAsync<AgentSchedule>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch schedule for user {userId} from Firebase: {ex.Message}. Using in-memory fallback.");
            _localSchedules.TryGetValue(userId, out var schedule);
            return schedule;
        }
    }

    public async Task<List<AgentSchedule>> GetAllSchedulesAsync()
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            return _localSchedules.Values.ToList();
        }

        try
        {
            var schedules = await _firebaseClient
                .Child("AgentSchedules")
                .OnceAsync<AgentSchedule>();
            return schedules.Select(s => s.Object).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch all schedules from Firebase: {ex.Message}. Returning in-memory list.");
            return _localSchedules.Values.ToList();
        }
    }

    public async Task SaveScheduleAsync(AgentSchedule schedule)
    {
        _localSchedules[schedule.UserId] = schedule;

        if (_useLocalFallback || _firebaseClient == null)
        {
            Console.WriteLine($"[Local Save] Schedule for user: {schedule.UserId} | Interval: {schedule.IntervalHours} hrs | Next Run: {schedule.NextRunTime}");
            return;
        }

        try
        {
            await _firebaseClient
                .Child("AgentSchedules")
                .Child(schedule.UserId)
                .PutAsync(schedule);
            Console.WriteLine($"Schedule saved successfully to Firebase for user {schedule.UserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save schedule to Firebase for user {schedule.UserId}: {ex.Message}");
        }
    }

    #endregion

    #region CEO Directives Operations

    public async Task<List<CeoDirective>> GetCeoDirectivesAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localCeoDirectives.TryGetValue(userId, out var list);
            return list ?? new List<CeoDirective>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("CeoDirectives")
                .Child(userId)
                .OnceAsync<CeoDirective>();
            return items.Select(x => x.Object).OrderByDescending(d => d.Timestamp).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch CEO directives from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localCeoDirectives.TryGetValue(userId, out var list);
            return list ?? new List<CeoDirective>();
        }
    }

    public async Task SaveCeoDirectiveAsync(string userId, CeoDirective directive)
    {
        var list = _localCeoDirectives.GetOrAdd(userId, _ => new List<CeoDirective>());
        list.RemoveAll(d => d.Id == directive.Id);
        list.Add(directive);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("CeoDirectives")
                .Child(userId)
                .Child(directive.Id)
                .PutAsync(directive);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save CEO directive to Firebase: {ex.Message}");
        }
    }

    #endregion

    #region Manager Reports Operations

    public async Task<ManagerPerformanceReport?> GetManagerReportAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localManagerReports.TryGetValue(userId, out var report);
            return report;
        }

        try
        {
            return await _firebaseClient
                .Child("ManagerPerformanceReports")
                .Child(userId)
                .OnceSingleAsync<ManagerPerformanceReport>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch manager report from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localManagerReports.TryGetValue(userId, out var report);
            return report;
        }
    }

    public async Task SaveManagerReportAsync(ManagerPerformanceReport report)
    {
        _localManagerReports[report.UserId] = report;

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("ManagerPerformanceReports")
                .Child(report.UserId)
                .PutAsync(report);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save manager report to Firebase: {ex.Message}");
        }
    }

    #endregion

    #region Memory Catalog Operations

    public async Task<List<MemoryCatalogItem>> GetMemoryCatalogAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localMemoryCatalog.TryGetValue(userId, out var list);
            return list ?? new List<MemoryCatalogItem>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("MemoryCatalog")
                .Child(userId)
                .OnceAsync<MemoryCatalogItem>();
            return items.Select(x => x.Object).OrderByDescending(c => c.CreatedTime).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch memory catalog from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localMemoryCatalog.TryGetValue(userId, out var list);
            return list ?? new List<MemoryCatalogItem>();
        }
    }

    public async Task SaveMemoryCatalogItemAsync(string userId, MemoryCatalogItem item)
    {
        var list = _localMemoryCatalog.GetOrAdd(userId, _ => new List<MemoryCatalogItem>());
        list.RemoveAll(c => c.Id == item.Id);
        list.Add(item);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("MemoryCatalog")
                .Child(userId)
                .Child(item.Id)
                .PutAsync(item);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save memory catalog item to Firebase: {ex.Message}");
        }
    }

    public async Task DeleteMemoryCatalogItemAsync(string userId, string itemId)
    {
        if (_localMemoryCatalog.TryGetValue(userId, out var list))
        {
            list.RemoveAll(c => c.Id == itemId);
        }

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("MemoryCatalog")
                .Child(userId)
                .Child(itemId)
                .DeleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete memory catalog item from Firebase: {ex.Message}");
        }
    }

    #endregion

    #region Business Model Operations

    public async Task<BusinessModelVisual?> GetBusinessModelAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localBusinessModels.TryGetValue(userId, out var model);
            return model;
        }

        try
        {
            return await _firebaseClient
                .Child("BusinessModels")
                .Child(userId)
                .OnceSingleAsync<BusinessModelVisual>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch business model from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localBusinessModels.TryGetValue(userId, out var model);
            return model;
        }
    }

    public async Task SaveBusinessModelAsync(BusinessModelVisual model)
    {
        _localBusinessModels[model.UserId] = model;

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("BusinessModels")
                .Child(model.UserId)
                .PutAsync(model);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save business model to Firebase: {ex.Message}");
        }
    }

    #endregion

    #region Video Ad Operations

    public async Task<List<VideoAdDetail>> GetVideoAdsAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localVideoAds.TryGetValue(userId, out var list);
            return list ?? new List<VideoAdDetail>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("VideoAds")
                .Child(userId)
                .OnceAsync<VideoAdDetail>();
            return items.Select(x => x.Object).OrderByDescending(c => c.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch video ads from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localVideoAds.TryGetValue(userId, out var list);
            return list ?? new List<VideoAdDetail>();
        }
    }

    public async Task SaveVideoAdAsync(string userId, VideoAdDetail videoAd)
    {
        var list = _localVideoAds.GetOrAdd(userId, _ => new List<VideoAdDetail>());
        list.RemoveAll(c => c.Id == videoAd.Id);
        list.Add(videoAd);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("VideoAds")
                .Child(userId)
                .Child(videoAd.Id)
                .PutAsync(videoAd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save video ad to Firebase: {ex.Message}");
        }
    }

    public async Task DeleteVideoAdAsync(string userId, string videoAdId)
    {
        if (_localVideoAds.TryGetValue(userId, out var list))
        {
            list.RemoveAll(c => c.Id == videoAdId);
        }

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("VideoAds")
                .Child(userId)
                .Child(videoAdId)
                .DeleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete video ad from Firebase: {ex.Message}");
        }
    }

    #endregion

    #region Image Poster Operations

    public async Task<List<ImagePosterDetail>> GetImagePostersAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localImagePosters.TryGetValue(userId, out var list);
            return list ?? new List<ImagePosterDetail>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("ImagePosters")
                .Child(userId)
                .OnceAsync<ImagePosterDetail>();
            return items.Select(x => x.Object).OrderByDescending(c => c.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch image posters from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localImagePosters.TryGetValue(userId, out var list);
            return list ?? new List<ImagePosterDetail>();
        }
    }

    public async Task SaveImagePosterAsync(string userId, ImagePosterDetail poster)
    {
        var list = _localImagePosters.GetOrAdd(userId, _ => new List<ImagePosterDetail>());
        list.RemoveAll(c => c.Id == poster.Id);
        list.Add(poster);

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("ImagePosters")
                .Child(userId)
                .Child(poster.Id)
                .PutAsync(poster);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save image poster to Firebase: {ex.Message}");
        }
    }

    public async Task DeleteImagePosterAsync(string userId, string posterId)
    {
        if (_localImagePosters.TryGetValue(userId, out var list))
        {
            list.RemoveAll(c => c.Id == posterId);
        }

        if (_useLocalFallback || _firebaseClient == null)
        {
            return;
        }

        try
        {
            await _firebaseClient
                .Child("ImagePosters")
                .Child(userId)
                .Child(posterId)
                .DeleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete image poster from Firebase: {ex.Message}");
        }
    }

    #endregion
}
