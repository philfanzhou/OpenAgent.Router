using System.Text;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// Produces small local feature-hashing embeddings for catalog coarse ranking. It intentionally has
/// no model or network dependency: the decision model remains responsible for the final choice.
/// </summary>
internal static class CandidateEmbeddingPreselector
{
    private const int Dimensions = 512;

    internal static IReadOnlyList<AgentSummary> Select(
        string message,
        IReadOnlyList<AgentSummary> candidates,
        int limit)
    {
        if (limit <= 0 || candidates.Count == 0) return [];
        if (candidates.Count <= limit) return candidates;

        double[] query = Embed(message);
        return candidates
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Index = index,
                Score = Cosine(query, Embed(CandidateText(candidate)))
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(limit)
            .Select(item => item.Candidate)
            .ToArray();
    }

    private static string CandidateText(AgentSummary candidate) =>
        $"{candidate.AgentId} {candidate.Name} {candidate.Description}";

    private static double[] Embed(string text)
    {
        var vector = new double[Dimensions];
        foreach (string token in Tokens(text))
        {
            uint hash = Fnv1A(token);
            int index = (int)(hash % Dimensions);
            vector[index] += (hash & 0x80000000) == 0 ? 1 : -1;
        }

        double norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
        {
            for (int index = 0; index < vector.Length; index++) vector[index] /= norm;
        }
        return vector;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(Rune.ToLowerInvariant(rune));
            }
            else if (current.Length > 0)
            {
                foreach (string token in Expand(current.ToString())) yield return token;
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            foreach (string token in Expand(current.ToString())) yield return token;
        }
    }

    private static IEnumerable<string> Expand(string token)
    {
        yield return token;
        Rune[] runes = token.EnumerateRunes().ToArray();
        int width = runes.Length >= 3 ? 3 : 2;
        if (runes.Length < width) yield break;
        for (int index = 0; index <= runes.Length - width; index++)
        {
            yield return string.Concat(runes.AsSpan(index, width).ToArray().Select(rune => rune.ToString()));
        }
    }

    private static uint Fnv1A(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (byte item in Encoding.UTF8.GetBytes(value))
        {
            hash ^= item;
            hash *= prime;
        }
        return hash;
    }

    private static double Cosine(double[] left, double[] right)
    {
        double result = 0;
        for (int index = 0; index < left.Length; index++) result += left[index] * right[index];
        return result;
    }
}
