namespace AuraUpBack.Domain.Services;

public static class PostTopicClassifier
{
    private static readonly TopicRule[] Rules =
    [
        new("fitness", "Workout education and transformation", "Educational how-to", "Problem-solution", ["workout", "gym", "fitness", "training", "bench", "legs", "muscle", "coach"]),
        new("cars", "Cars, builds and automotive performance", "Demonstration", "Showcase", ["car", "cars", "bmw", "ferrari", "porsche", "engine", "launch", "exhaust", "garage"]),
        new("luxury", "Luxury lifestyle and premium branding", "Authority positioning", "Status framing", ["luxury", "premium", "brand", "expensive", "elegant", "wealth", "status"]),
        new("business", "Business, marketing and growth", "Tactical breakdown", "Curiosity hook", ["business", "marketing", "sales", "client", "brand", "offer", "content", "strategy", "growth"]),
        new("motivation", "Mindset, grit and motivation", "Personal inspiration", "Identity hook", ["motivation", "discipline", "mindset", "believe", "focus", "hard work", "success"]),
        new("family", "Family, relationships and daily life", "Personal story", "Relatable hook", ["family", "daughter", "son", "wife", "mom", "dad", "home", "kids"]),
        new("entertainment", "Entertainment, comedy and celebrity moments", "Entertainment clip", "Pattern interrupt", ["funny", "comedy", "laugh", "movie", "scene", "actor", "show", "challenge"])
    ];

    public static PostTopicClassificationResult Classify(string caption, string? transcript)
    {
        var source = $"{caption} {transcript}".Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(source))
        {
            return new PostTopicClassificationResult("general", 0.2m, "General content", "Generic hook", "General creator content without a clear topic.");
        }

        var bestRule = Rules
            .Select(rule => new
            {
                Rule = rule,
                Score = rule.Keywords.Count(keyword => source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(x => x.Score)
            .First();

        if (bestRule.Score == 0)
        {
            return new PostTopicClassificationResult("general", 0.35m, "General content", InferHookStyle(source), "General creator content without a dominant repeated theme.");
        }

        var confidence = Math.Min(0.95m, 0.45m + (bestRule.Score * 0.12m));
        return new PostTopicClassificationResult(
            bestRule.Rule.Topic,
            confidence,
            bestRule.Rule.ContentAngle,
            InferHookStyle(source, bestRule.Rule.DefaultHookStyle),
            bestRule.Rule.ThemeSummary);
    }

    private static string InferHookStyle(string source, string? fallback = null)
    {
        if (source.Contains("why ", StringComparison.OrdinalIgnoreCase))
        {
            return "Why-hook";
        }

        if (source.Contains("how ", StringComparison.OrdinalIgnoreCase))
        {
            return "How-to hook";
        }

        if (char.IsDigit(source.FirstOrDefault()))
        {
            return "List hook";
        }

        if (source.Contains("mistake", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("wrong", StringComparison.OrdinalIgnoreCase))
        {
            return "Mistake hook";
        }

        return fallback ?? "Direct hook";
    }

    public sealed record PostTopicClassificationResult(
        string Topic,
        decimal TopicConfidence,
        string ContentAngle,
        string HookStyle,
        string ThemeSummary);

    private sealed record TopicRule(
        string Topic,
        string ThemeSummary,
        string ContentAngle,
        string DefaultHookStyle,
        IReadOnlyCollection<string> Keywords);
}
