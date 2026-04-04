using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Services;

public static class OutlierCalculator
{
    public static void Apply(IReadOnlyCollection<TrackedPost> posts)
    {
        if (posts.Count == 0)
        {
            return;
        }

        var baseline = Math.Max(1m, (decimal)posts.Average(x => Math.Max(0L, x.Views)));

        foreach (var post in posts)
        {
            var multiplier = Math.Max(0m, post.Views / baseline);
            var roundedMultiplier = Math.Round(multiplier, 2, MidpointRounding.AwayFromZero);
            var isOutlier = roundedMultiplier >= 2m;
            post.SetPerformance(roundedMultiplier == 0m ? 0.01m : roundedMultiplier, isOutlier);
        }
    }
}
