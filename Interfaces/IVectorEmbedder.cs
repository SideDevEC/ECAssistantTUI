namespace ECAssistant.Interfaces;

/// <summary>
/// Text embedding abstraction.
/// </summary>
public interface IVectorEmbedder
{
    float[] Embed(string text);
}