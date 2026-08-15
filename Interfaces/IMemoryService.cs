using System.Collections.Generic;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Persistent memory with vector search.
/// </summary>
public interface IMemoryService
{
    Task<List<MemoryEntry>> SearchAsync(string query, int maxResults = 5);
    Task AddAsync(MemoryEntry entry);
    Task SaveAsync();
    Task LoadAsync();
}