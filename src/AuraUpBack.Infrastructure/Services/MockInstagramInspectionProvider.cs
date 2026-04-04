using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Abstractions;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class MockInstagramInspectionProvider : IInstagramInspectionProvider
{
    private static readonly string[] GenericHooks =
    [
        "3 mistakes almost everyone makes",
        "Why this simple change multiplies performance",
        "What high-performing creators do differently",
        "The fastest way to improve retention",
        "The hidden reason this content works"
    ];

    public string Name => "Mock";

    public Task<InspectionPayload> InspectAccountAsync(
        InstagramInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedHandle = request.Handle.Trim().TrimStart('@').ToLowerInvariant();
        var researchPrompt = request.ResearchPrompt;
        var knownIds = new HashSet<string>(request.KnownPostExternalIds, StringComparer.OrdinalIgnoreCase);
        var startFromPostIndex = Math.Max(0, request.StartFromPostIndex);
        var seed = Math.Abs(HashCode.Combine(normalizedHandle, researchPrompt, DateTime.UtcNow.Date));
        var random = new Random(seed);
        var today = DateTime.UtcNow.Date;
        var profile = BuildProfile(normalizedHandle, researchPrompt);
        var baseViews = random.Next(profile.BaseViewsMin, profile.BaseViewsMax);
        var desiredNewPosts = request.DesiredNewPosts <= 0 ? 12 : request.DesiredNewPosts;
        var generatedPosts = Math.Max(startFromPostIndex + desiredNewPosts, request.MaxDiscoveryPosts > 0 ? request.MaxDiscoveryPosts : 12);

        var discoveredPosts = Enumerable.Range(0, generatedPosts)
            .Select(index => BuildPost(profile, normalizedHandle, researchPrompt, today, baseViews, index, random))
            .OrderByDescending(x => x.Views)
            .ToList();

        var posts = discoveredPosts
            .Skip(startFromPostIndex)
            .Where(x => !knownIds.Contains(x.ExternalId))
            .Take(desiredNewPosts)
            .ToList();

        var strongestPost = discoveredPosts.First();
        var averageViews = discoveredPosts.Average(x => x.Views);

        return Task.FromResult(new InspectionPayload
        {
            Handle = normalizedHandle,
            DisplayName = profile.DisplayName,
            ProfileImageUrl = $"https://picsum.photos/seed/{normalizedHandle}-avatar/256/256",
            Bio = profile.Bio,
            FollowersCount = random.Next(profile.FollowersMin, profile.FollowersMax),
            ResearchSummary =
                $"Audit for @{normalizedHandle}. {posts.Count} new reels analyzed, {discoveredPosts.Count - posts.Count} already known. Average views around {averageViews:0}. Top outlier reached {strongestPost.Views:n0} views with hook '{strongestPost.Caption[..Math.Min(strongestPost.Caption.Length, 52)]}...'. Prompt focus: {researchPrompt}",
            SeenPostExternalIds = discoveredPosts.Select(x => x.ExternalId).ToList(),
            Posts = posts
        });
    }

    private static InspectedPostPayload BuildPost(
        MockProfile profile,
        string handle,
        string researchPrompt,
        DateTime today,
        int baseViews,
        int index,
        Random random)
    {
        var template = profile.Posts[index % profile.Posts.Count];
        var isPrimaryOutlier = index == 0;
        var isSecondaryOutlier = index == 1 && random.NextDouble() > 0.45d;
        var outlierBoost = isPrimaryOutlier
            ? random.Next(8, 18)
            : isSecondaryOutlier
                ? random.Next(4, 9)
                : random.Next(1, 4);

        var views = baseViews * outlierBoost + random.Next(0, baseViews / 3);
        var likes = Math.Max(views / random.Next(10, 18), 120);
        var comments = Math.Max(views / random.Next(70, 180), 18);
        var publishedAtUtc = today
            .AddDays(-(index * random.Next(2, 5)) - random.Next(0, 2))
            .AddHours(random.Next(8, 22))
            .AddMinutes(random.Next(0, 59));

        var caption = $"{template.Hook}. {template.Angle}. {template.CallToAction} #{profile.PrimaryHashtag} #{profile.SecondaryHashtag}";
        var classification = PostTopicClassifier.Classify(caption, transcript: null);

        return new InspectedPostPayload
        {
            IsReel = true,
            ExternalId = $"{handle}-reel-{index + 1:00}",
            Caption = caption,
            Url = $"https://instagram.com/{handle}/reel/{GenerateReelSlug(handle, index)}",
            ThumbnailUrl = $"https://picsum.photos/seed/{handle}-{index + 1}/720/1280",
            PublishedAtUtc = publishedAtUtc,
            Views = views,
            Likes = likes,
            Comments = comments,
            Topic = classification.Topic,
            TopicConfidence = classification.TopicConfidence,
            ContentAngle = classification.ContentAngle,
            HookStyle = classification.HookStyle,
            ThemeSummary = classification.ThemeSummary
        };
    }

    private static string GenerateReelSlug(string handle, int index)
    {
        return $"{handle[..Math.Min(handle.Length, 6)]}{(index + 1):00}AX";
    }

    private static MockProfile BuildProfile(string handle, string researchPrompt)
    {
        if (handle.Contains("auto") || handle.Contains("car"))
        {
            return new MockProfile(
                "Velocity Garage",
                "Performance cars, builds, launches and viral automotive edits.",
                "supercars",
                "carcontent",
                48_000,
                160_000,
                180_000,
                960_000,
                new List<MockPostTemplate>
                {
                    new("The $4,000 mod that changed this BMW overnight", "Before/after transformation with engine sound payoff", "Comment 'build' for part list"),
                    new("Porsche launch control vs. old-school manual reaction", "High tension comparison with immediate action", "Save this for your next reel idea"),
                    new("Why this exhaust clip pulled 10x more views than average", "Hook, rev sequence, cinematic cut, payoff", "Follow for daily car outliers"),
                    new("Ferrari detail shots with one editing trick", "Luxury framing with fast pattern interrupts", "Share this with a car editor"),
                });
        }

        if (handle.Contains("fit") || handle.Contains("gym"))
        {
            return new MockProfile(
                "Apex Conditioning",
                "Strength, body composition and athlete routines with short-form education.",
                "fitness",
                "gymtips",
                22_000,
                72_000,
                60_000,
                420_000,
                new List<MockPostTemplate>
                {
                    new("3 reasons your bench press has been stuck for months", "Educational hook with fast authority positioning", "Send this to your training partner"),
                    new("The warm-up sequence that fixed my shoulder pain", "Problem-solution format with direct proof", "Save this for upper-body day"),
                    new("One leg-day mistake killing your growth", "Strong negative hook with fast retention", "Comment 'legs' if you want part 2"),
                    new("What I eat before filming and lifting", "Simple daily routine with high relatability", "Follow for realistic fitness content"),
                });
        }

        if (handle.Contains("lux") || handle.Contains("brand") || handle.Contains("aura"))
        {
            return new MockProfile(
                "Maison Elevate",
                "Luxury positioning, premium branding and perception-first content.",
                "luxurybrand",
                "positioning",
                18_000,
                55_000,
                40_000,
                260_000,
                new List<MockPostTemplate>
                {
                    new("Why premium brands never start by selling the product", "Story-first positioning with status framing", "Save this if you build premium offers"),
                    new("The visual cue that instantly raises brand perception", "Minimal design breakdown with instant application", "Send this to your creative team"),
                    new("How one sentence can make your offer feel expensive", "Copywriting hook with perceived value lens", "Comment 'copy' for more"),
                    new("The reel structure we use for premium authority", "Clean sequence: intrigue, proof, identity, CTA", "Follow for brand intelligence"),
                });
        }

        var displayName = string.Join(' ',
            handle.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => char.ToUpperInvariant(value[0]) + value[1..]));

        return new MockProfile(
            string.IsNullOrWhiteSpace(displayName) ? "Creator Studio" : displayName,
            $"Short-form educational account with focus on {researchPrompt}.",
            "creator",
            "contentstrategy",
            14_000,
            48_000,
            30_000,
            180_000,
            GenericHooks.Select((hook, index) => new MockPostTemplate(
                hook,
                $"Content angle #{index + 1} built around {researchPrompt}",
                "Save this and test it this week")).ToList());
    }

    private sealed record MockProfile(
        string DisplayName,
        string Bio,
        string PrimaryHashtag,
        string SecondaryHashtag,
        int BaseViewsMin,
        int BaseViewsMax,
        int FollowersMin,
        int FollowersMax,
        List<MockPostTemplate> Posts);

    private sealed record MockPostTemplate(
        string Hook,
        string Angle,
        string CallToAction);
}
