using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Connectors.Google;

namespace AgenticSystem.Services;

public class QdrantMemoryService
{
    private readonly ISemanticTextMemory _memorySystem;
    private const string CollectionName = "agent_knowledge_base";
    public bool IsFallbackActive { get; } = true;

    public QdrantMemoryService(IConfiguration config)
    {
        // Since QdrantMemoryStore is legacy and superseded by Vector Store abstractions, 
        // we use VolatileMemoryStore for robust, local in-memory vector indexing and search.
        var store = new VolatileMemoryStore();
        Console.WriteLine("Qdrant Memory Service initialized in Local Volatile Memory mode.");

        try
        {
            _memorySystem = new MemoryBuilder()
                .WithGoogleAITextEmbeddingGeneration(config["AiConfig:EmbeddingModelId"]!, config["AiConfig:GeminiApiKey"]!)
                .WithMemoryStore(store)
                .Build();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to build MemorySystem with Google embeddings: {ex.Message}. Falling back to simple volatile memory configuration.");
            _memorySystem = new MemoryBuilder()
                .WithMemoryStore(store)
                .Build();
        }
    }

    public static event Func<string, string, string, string?, Task>? OnKnowledgeSaved;

    public async Task SaveKnowledgeAsync(string id, string text, string tags, string? userId = null)
    {
        try
        {
            await _memorySystem.SaveInformationAsync(
                collection: CollectionName,
                text: text,
                id: id,
                description: tags
            );
            Console.WriteLine($"Knowledge saved to vector memory: {id} - {tags}");
            
            if (OnKnowledgeSaved != null)
            {
                await OnKnowledgeSaved.Invoke(id, text, tags, userId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save to vector memory: {ex.Message}.");
        }
    }

    public async Task RemoveKnowledgeAsync(string id)
    {
        try
        {
            await _memorySystem.RemoveAsync(CollectionName, id);
            Console.WriteLine($"Knowledge removed from vector memory: {id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to remove from vector memory: {ex.Message}.");
        }
    }

    public async Task<List<string>> SearchMemoryAsync(string userQuery, int limit = 2)
    {
        try
        {
            var matches = _memorySystem.SearchAsync(CollectionName, userQuery, limit: limit, minRelevanceScore: 0.6);
            
            var list = new List<string>();
            await foreach (var match in matches)
            {
                list.Add(match.Metadata.Text);
            }
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to search vector memory: {ex.Message}. Returning empty list.");
            return new List<string>();
        }
    }
}
