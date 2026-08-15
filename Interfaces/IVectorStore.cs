using System.Collections.Generic;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Vector similarity search abstraction.
/// </summary>
public interface IVectorStore
{
    Task IndexAsync(string content, string metadata);
    Task<List<VectorResult>> SearchAsync(float[] embedding, int maxResults = 5);
}