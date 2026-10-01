using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace AgenticSystem.Services;

public class FirebaseStorageService
{
    private readonly HttpClient _httpClient;
    private readonly string _storageBucket;

    public FirebaseStorageService(IConfiguration config, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _storageBucket = config["AiConfig:FirebaseStorageBucket"] ?? "humanicbotagent.firebasestorage.app";
    }

    private class FirebaseStorageResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("downloadTokens")]
        public string DownloadTokens { get; set; } = string.Empty;
    }

    /// <summary>
    /// Uploads a file to Firebase Storage.
    /// </summary>
    public async Task<string> UploadFileAsync(string userId, string fileName, Stream fileStream, string contentType, string? idToken)
    {
        // Unique file name to avoid collisions
        var fileId = Guid.NewGuid().ToString();
        var storagePath = $"knowledgebase/{userId}/{fileId}_{fileName}";
        var escapedPath = Uri.EscapeDataString(storagePath);
        
        var uploadUrl = $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o?uploadType=media&name={escapedPath}";

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        
        // Add Authorization header if user is authenticated
        if (!string.IsNullOrEmpty(idToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        }

        // Set content and content type
        var content = new StreamContent(fileStream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"Firebase Storage upload failed with status {response.StatusCode}: {errorContent}");
        }

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var storageInfo = JsonSerializer.Deserialize<FirebaseStorageResponse>(jsonResponse);

        if (storageInfo == null || string.IsNullOrEmpty(storageInfo.DownloadTokens))
        {
            // Fallback URL construct if token is not parsed properly, but normally it is there
            return $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o/{escapedPath}?alt=media";
        }

        // Return download URL with media parameter and download token
        return $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o/{escapedPath}?alt=media&token={storageInfo.DownloadTokens}";
    }

    /// <summary>
    /// Deletes a file from Firebase Storage.
    /// </summary>
    public async Task DeleteFileAsync(string fileUrl, string? idToken)
    {
        if (string.IsNullOrEmpty(fileUrl)) return;

        try
        {
            // Extract the storage path from the Firebase storage URL
            // Example URL: https://firebasestorage.googleapis.com/v0/b/bucket/o/knowledgebase%2Fuser%2Ffile?alt=media&token=xxx
            var uri = new Uri(fileUrl);
            var pathSegment = uri.AbsolutePath;
            
            // AbsolutePath starts with /v0/b/{bucket}/o/
            var prefix = $"/v0/b/{_storageBucket}/o/";
            var prefixIndex = pathSegment.IndexOf(prefix);
            if (prefixIndex == -1) return;

            var escapedPath = pathSegment.Substring(prefixIndex + prefix.Length);
            var deleteUrl = $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o/{escapedPath}";

            using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
            if (!string.IsNullOrEmpty(idToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            }

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Warning: Failed to delete file {fileUrl} from Firebase Storage. Status: {response.StatusCode}. Error: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception deleting file {fileUrl} from Firebase Storage: {ex.Message}");
        }
    }
}
