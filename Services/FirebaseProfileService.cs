using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class FirebaseProfileService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;
    private UserBusinessProfile? _localFallbackProfile; // Fallback profile storage in RAM

    public FirebaseProfileService(IConfiguration config)
    {
        var url = config["AiConfig:FirebaseProfilesUrl"] ?? config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase Profiles URL is empty or generic. Using local in-memory fallback for profiles.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase Profile database initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize profile FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }
    }

    public async Task<UserBusinessProfile?> GetProfileAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            return _localFallbackProfile != null && _localFallbackProfile.UserId == userId ? _localFallbackProfile : null;
        }

        try
        {
            var profile = await _firebaseClient
                .Child("UserProfiles")
                .Child(userId)
                .OnceSingleAsync<UserBusinessProfile>();
            
            return profile;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch profile for user {userId} from Firebase: {ex.Message}. Falling back to in-memory.");
            return _localFallbackProfile != null && _localFallbackProfile.UserId == userId ? _localFallbackProfile : null;
        }
    }

    public async Task SaveProfileAsync(UserBusinessProfile profile)
    {
        // Always cache in memory as fallback
        _localFallbackProfile = profile;

        if (_useLocalFallback || _firebaseClient == null)
        {
            Console.WriteLine($"[Local Profile Save] User: {profile.UserId} | Business: {profile.BusinessName}");
            return;
        }

        try
        {
            await _firebaseClient
                .Child("UserProfiles")
                .Child(profile.UserId)
                .PutAsync(profile);
            Console.WriteLine($"Profile saved successfully to Firebase for user {profile.UserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save profile to Firebase: {ex.Message}. Profile cached in-memory.");
        }
    }

    public async Task<List<UserBusinessProfile>> GetAllProfilesAsync()
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            return _localFallbackProfile != null ? new List<UserBusinessProfile> { _localFallbackProfile } : new List<UserBusinessProfile>();
        }

        try
        {
            var profiles = await _firebaseClient
                .Child("UserProfiles")
                .OnceAsync<UserBusinessProfile>();
            
            return profiles.Select(p => p.Object).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch all profiles from Firebase: {ex.Message}. Falling back to cached profile.");
            return _localFallbackProfile != null ? new List<UserBusinessProfile> { _localFallbackProfile } : new List<UserBusinessProfile>();
        }
    }
}
