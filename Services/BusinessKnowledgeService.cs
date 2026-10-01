using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Firebase.Database;
using Firebase.Database.Query;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class BusinessKnowledgeService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly QdrantMemoryService _qdrantMemory;
    private readonly FirebaseStorageService _storageService;
    private readonly bool _useLocalFallback = false;
    private readonly ConcurrentDictionary<string, List<BusinessKnowledgeItem>> _localKnowledge = new();

    public BusinessKnowledgeService(
        IConfiguration config,
        QdrantMemoryService qdrantMemory,
        FirebaseStorageService storageService)
    {
        _qdrantMemory = qdrantMemory;
        _storageService = storageService;

        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. Using local in-memory fallback for Business Knowledgebase.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase Business Knowledgebase initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize knowledge FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }
    }

    /// <summary>
    /// Gets all knowledge base items (notes and files) for a specific user.
    /// </summary>
    public async Task<List<BusinessKnowledgeItem>> GetKnowledgeItemsAsync(string userId)
    {
        if (_useLocalFallback || _firebaseClient == null)
        {
            _localKnowledge.TryGetValue(userId, out var list);
            return list ?? new List<BusinessKnowledgeItem>();
        }

        try
        {
            var items = await _firebaseClient
                .Child("BusinessKnowledge")
                .Child(userId)
                .OnceAsync<BusinessKnowledgeItem>();
            
            return items.Select(x => x.Object).OrderByDescending(k => k.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch knowledge base from Firebase: {ex.Message}. Returning in-memory fallback.");
            _localKnowledge.TryGetValue(userId, out var list);
            return list ?? new List<BusinessKnowledgeItem>();
        }
    }

    /// <summary>
    /// Saves a knowledge item (note or file metadata) and indexes it in vector memory.
    /// </summary>
    public async Task SaveKnowledgeItemAsync(BusinessKnowledgeItem item)
    {
        // Update local cache
        var list = _localKnowledge.GetOrAdd(item.UserId, _ => new List<BusinessKnowledgeItem>());
        list.RemoveAll(x => x.Id == item.Id);
        list.Add(item);

        // Save to Firebase Realtime Database
        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                await _firebaseClient
                    .Child("BusinessKnowledge")
                    .Child(item.UserId)
                    .Child(item.Id)
                    .PutAsync(item);
                Console.WriteLine($"Knowledge item metadata saved to Firebase Realtime Database: {item.Id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save knowledge item metadata to Firebase Realtime Database: {ex.Message}");
            }
        }

        // Index the content in Vector Memory (Qdrant) so agents can perform semantic queries
        try
        {
            // Format content with metadata so vector embedding captures the structural context
            var typeLabel = item.FileType == "Note" ? "Note" : $"File ({item.FileName})";
            var vectorText = $"[Business Knowledge - {typeLabel}]\nTitle: {item.Title}\nContent:\n{item.Content}";
            
            await _qdrantMemory.SaveKnowledgeAsync(item.Id, vectorText, "BusinessKnowledge", item.UserId);
            Console.WriteLine($"Knowledge item content vectorized successfully: {item.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to index knowledge item in vector store: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a knowledge item, cleans up vector memory, and removes any file from Firebase Storage.
    /// </summary>
    public async Task DeleteKnowledgeItemAsync(string userId, string itemId, string? idToken)
    {
        BusinessKnowledgeItem? targetItem = null;

        // Get the item details first
        if (_localKnowledge.TryGetValue(userId, out var list))
        {
            targetItem = list.FirstOrDefault(x => x.Id == itemId);
            list.RemoveAll(x => x.Id == itemId);
        }

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                if (targetItem == null)
                {
                    targetItem = await _firebaseClient
                        .Child("BusinessKnowledge")
                        .Child(userId)
                        .Child(itemId)
                        .OnceSingleAsync<BusinessKnowledgeItem>();
                }

                await _firebaseClient
                    .Child("BusinessKnowledge")
                    .Child(userId)
                    .Child(itemId)
                    .DeleteAsync();
                Console.WriteLine($"Knowledge item metadata deleted from Firebase Realtime Database: {itemId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete knowledge item metadata from Firebase Realtime Database: {ex.Message}");
            }
        }

        // Delete from Vector Memory (Qdrant)
        try
        {
            await _qdrantMemory.RemoveKnowledgeAsync(itemId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to remove knowledge item from vector store: {ex.Message}");
        }

        // If it's a file, delete the file resource in Firebase Storage
        if (targetItem != null && !string.IsNullOrEmpty(targetItem.FileUrl))
        {
            try
            {
                await _storageService.DeleteFileAsync(targetItem.FileUrl, idToken);
                Console.WriteLine($"File deleted from Firebase Storage: {targetItem.FileUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete file from Firebase Storage: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Compiles all active knowledge items as a unified text block to inject as context into agent prompts.
    /// </summary>
    public async Task<string> GetUnifiedKnowledgeContextAsync(string userId)
    {
        var items = await GetKnowledgeItemsAsync(userId);
        if (!items.Any()) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Additional Corporate & Business Knowledge Base Notes/Files:");
        
        foreach (var item in items)
        {
            var typeLabel = item.FileType == "Note" ? "Note" : $"File ({item.FileName})";
            sb.AppendLine($"--- Start Item: {item.Title} ({typeLabel}) ---");
            sb.AppendLine(item.Content);
            sb.AppendLine($"--- End Item: {item.Title} ---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
