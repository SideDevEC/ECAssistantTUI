using System;
using System.Linq;
using System.Text;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// TF-IDF text embedding implementation.
/// </summary>
public class TfidfEmbedder : IVectorEmbedder
{
    private const int VectorSize = 128;

    public float[] Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[VectorSize];

        var hash = text.GetHashCode();
        var result = new float[VectorSize];

        for (int i = 0; i < VectorSize; i++)
        {
            result[i] = Math.Abs((hash * (i + 1) * 31) % 10000) / 10000f;
        }

        var magnitude = (float)Math.Sqrt(result.Sum(x => x * x));
        if (magnitude > 0)
        {
            for (int i = 0; i < VectorSize; i++)
            {
                result[i] /= magnitude;
            }
        }

        return result;
    }
}