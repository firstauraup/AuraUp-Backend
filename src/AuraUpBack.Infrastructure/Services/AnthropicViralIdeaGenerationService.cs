using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class AnthropicViralIdeaGenerationService(
    IHttpClientFactory httpClientFactory,
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicViralIdeaGenerationService> logger)
    : IViralIdeaGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AnthropicOptions _options = options.Value;

    public async Task<IReadOnlyCollection<ViralReelIdea>> GenerateIdeasAsync(
        ViralIdeaGenerationRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CreateTimeoutToken(cancellationToken);
        var prompt = BuildPrompt(request);
        var payload = CreateRequestPayload(prompt, stream: false);
        var responseText = await SendJsonRequestAsync(payload, timeoutCts.Token);
        return ParseIdeasFromResponseText(responseText);
    }

    public async Task StreamIdeasAsync(
        ViralIdeaGenerationRequest request,
        Func<ViralIdeaGenerationStreamEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        EnsureConfigured();

        using var timeoutCts = CreateTimeoutToken(cancellationToken);
        var prompt = BuildPrompt(request);
        var payload = CreateRequestPayload(prompt, stream: true);
        var httpClient = httpClientFactory.CreateClient(nameof(AnthropicViralIdeaGenerationService));
        using var httpRequest = CreateHttpRequest(payload, accept: "text/event-stream");

        logger.LogInformation(
            "Streaming viral reel ideas for @{Handle} from {SourceCount} reels",
            request.AccountHandle,
            request.SourceReels.Count);

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            throw new InvalidOperationException(
                $"Anthropic stream request failed with status {(int)response.StatusCode}: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(stream);
        var currentEvent = string.Empty;
        var currentData = new StringBuilder();
        var outputBuffer = new StringBuilder();

        while (!reader.EndOfStream)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(timeoutCts.Token) ?? string.Empty;
            if (string.IsNullOrEmpty(line))
            {
                await ProcessStreamFrameAsync(currentEvent, currentData.ToString(), outputBuffer, onEvent);
                currentEvent = string.Empty;
                currentData.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentData.Length > 0)
                {
                    currentData.Append('\n');
                }

                currentData.Append(line["data:".Length..].Trim());
            }
        }

        await ProcessStreamFrameAsync(currentEvent, currentData.ToString(), outputBuffer, onEvent);

        var ideas = ParseIdeasFromGeneratedText(outputBuffer.ToString());
        await onEvent(new ViralIdeaGenerationStreamEvent("completed", string.Empty, ideas));
    }

    private async Task<string> SendJsonRequestAsync(
        AnthropicMessagesRequest payload,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var httpClient = httpClientFactory.CreateClient(nameof(AnthropicViralIdeaGenerationService));
        using var httpRequest = CreateHttpRequest(payload, accept: "application/json");

        logger.LogInformation(
            "Generating viral reel ideas for prompt with {Model} and timeout {TimeoutSeconds}s",
            _options.Model,
            _options.RequestTimeoutSeconds);

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Anthropic request failed with status {(int)response.StatusCode}: {responseText}");
            }

            return responseText;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Anthropic request timed out after {_options.RequestTimeoutSeconds} seconds.");
        }
    }

    private HttpRequestMessage CreateHttpRequest(AnthropicMessagesRequest payload, string accept)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.Add("x-api-key", _options.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return httpRequest;
    }

    private AnthropicMessagesRequest CreateRequestPayload(string prompt, bool stream)
    {
        return new AnthropicMessagesRequest(
            _options.Model,
            Math.Max(4096, _options.MaxTokens),
            [
                new AnthropicMessage("user", prompt)
            ],
            stream);
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.RequestTimeoutSeconds)));
        return timeoutCts;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Anthropic API key is not configured. Set Anthropic__ApiKey in the environment.");
        }
    }

    private async Task ProcessStreamFrameAsync(
        string eventName,
        string data,
        StringBuilder outputBuffer,
        Func<ViralIdeaGenerationStreamEvent, Task> onEvent)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AnthropicStreamEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AnthropicStreamEnvelope>(data, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (envelope is null)
        {
            return;
        }

        if (string.Equals(envelope.Type, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                envelope.Error?.Message?.Trim() ?? "Anthropic stream returned an error.");
        }

        if (!string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(envelope.Type, "content_block_delta", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deltaText = envelope.Delta?.Text?.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(deltaText))
        {
            return;
        }

        outputBuffer.Append(deltaText);
        await onEvent(new ViralIdeaGenerationStreamEvent("delta", deltaText, null));
    }

    private IReadOnlyCollection<ViralReelIdea> ParseIdeasFromResponseText(string responseText)
    {
        var anthropicResponse = JsonSerializer.Deserialize<AnthropicMessagesResponse>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("Anthropic returned an empty response.");

        var text = string.Join(
            "\n",
            anthropicResponse.Content
                .Where(item => item.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Text?.Trim())
                .Where(textItem => !string.IsNullOrWhiteSpace(textItem)));

        return ParseIdeasFromGeneratedText(text);
    }

    private IReadOnlyCollection<ViralReelIdea> ParseIdeasFromGeneratedText(string generatedText)
    {
        var jsonPayload = ExtractJsonPayload(generatedText);
        var parsed = JsonSerializer.Deserialize<ViralIdeaResponseEnvelope>(jsonPayload, JsonOptions)
            ?? throw new InvalidOperationException("Anthropic returned invalid idea JSON.");

        var ideas = parsed.Ideas
            .Where(idea => !string.IsNullOrWhiteSpace(idea.Title))
            .Select((idea, index) => new ViralReelIdea(
                idea.Rank > 0 ? idea.Rank : index + 1,
                (idea.Title ?? string.Empty).Trim(),
                (idea.Hook ?? string.Empty).Trim(),
                (idea.Premise ?? string.Empty).Trim(),
                (idea.Format ?? string.Empty).Trim(),
                (idea.WhyItCouldWork ?? string.Empty).Trim(),
                (idea.SourceReels ?? string.Empty).Trim(),
                Math.Clamp(idea.Confidence, 1, 100)))
            .Where(idea => !string.IsNullOrWhiteSpace(idea.Title))
            .OrderBy(idea => idea.Rank)
            .ToList();

        if (ideas.Count != 90)
        {
            throw new InvalidOperationException($"Anthropic returned {ideas.Count} ideas. Expected exactly 90.");
        }

        return ideas;
    }

    private static string BuildPrompt(ViralIdeaGenerationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Eres un estratega senior de contenido short-form, obsesionado con patrones virales replicables.");
        sb.AppendLine("Tu tarea es generar EXACTAMENTE 90 ideas virales para reels nuevos.");
        sb.AppendLine("Idioma de salida: español.");
        sb.AppendLine("Usa exclusivamente los reels fuente, sus transcripciones y sus métricas.");
        sb.AppendLine("No repitas ideas. Mezcla formatos, hooks, ángulos, estructuras y niveles de audacia.");
        sb.AppendLine("Prioriza ideas que sean grabables y entendibles por un creador humano.");
        sb.AppendLine("Devuelve SOLO JSON válido, sin markdown, sin explicación extra.");
        sb.AppendLine(@"Formato exacto:
{
  ""ideas"": [
    {
      ""rank"": 1,
      ""title"": ""..."",
      ""hook"": ""..."",
      ""premise"": ""..."",
      ""format"": ""..."",
      ""whyItCouldWork"": ""..."",
      ""sourceReels"": ""..."",
      ""confidence"": 87
    }
  ]
}");
        sb.AppendLine();
        sb.AppendLine($"Cuenta objetivo: @{request.AccountHandle}");
        sb.AppendLine($"Objetivo adicional del usuario: {request.Objective.Trim()}");
        sb.AppendLine();
        sb.AppendLine("Reels fuente:");

        var index = 1;
        foreach (var reel in request.SourceReels)
        {
            sb.AppendLine($"[{index}] ExternalId: {reel.ExternalId}");
            sb.AppendLine($"[{index}] Title: {reel.Title}");
            sb.AppendLine($"[{index}] Metrics: views={reel.Views}, likes={reel.Likes}, comments={reel.Comments}, shares={reel.Shares}, multiplier={reel.PerformanceMultiplier:0.##}x");
            sb.AppendLine($"[{index}] Topic: {reel.Topic}");
            sb.AppendLine($"[{index}] HookStyle: {reel.HookStyle}");
            sb.AppendLine($"[{index}] ContentAngle: {reel.ContentAngle}");
            sb.AppendLine($"[{index}] ThemeSummary: {reel.ThemeSummary}");
            sb.AppendLine($"[{index}] Caption: {reel.Caption}");
            sb.AppendLine($"[{index}] Transcript: {reel.Transcript}");
            sb.AppendLine();
            index++;
        }

        sb.AppendLine("Reglas adicionales:");
        sb.AppendLine("- Genera exactamente 90 ideas.");
        sb.AppendLine("- Cada idea debe ser distinta.");
        sb.AppendLine("- Usa confidence entre 1 y 100.");
        sb.AppendLine("- sourceReels debe citar el o los índices fuente más relevantes, por ejemplo: \"1, 3\".");
        sb.AppendLine("- format debe ser corto: lista, confesión, POV, sketch, tutorial, historia, debate, reacción, ranking, comparación, etc.");
        sb.AppendLine("- hook debe ser la frase de apertura sugerida.");
        sb.AppendLine("- premise debe explicar en 1-2 frases qué reel grabar.");
        sb.AppendLine("- whyItCouldWork debe conectar la idea con patrones observados en los reels fuente.");

        return sb.ToString();
    }

    private static string ExtractJsonPayload(string source)
    {
        var trimmed = source.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Anthropic did not return a JSON payload.");
        }

        return trimmed[start..(end + 1)];
    }

    private sealed record AnthropicMessagesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("messages")] IReadOnlyCollection<AnthropicMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record AnthropicMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record AnthropicMessagesResponse(
        [property: JsonPropertyName("content")] IReadOnlyCollection<AnthropicContentBlock> Content);

    private sealed record AnthropicContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record AnthropicStreamEnvelope(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("delta")] AnthropicStreamDelta? Delta,
        [property: JsonPropertyName("error")] AnthropicStreamError? Error);

    private sealed record AnthropicStreamDelta(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record AnthropicStreamError(
        [property: JsonPropertyName("message")] string? Message);

    private sealed record ViralIdeaResponseEnvelope(
        [property: JsonPropertyName("ideas")] IReadOnlyCollection<ViralIdeaItem> Ideas);

    private sealed record ViralIdeaItem(
        [property: JsonPropertyName("rank")] int Rank,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("hook")] string Hook,
        [property: JsonPropertyName("premise")] string Premise,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("whyItCouldWork")] string WhyItCouldWork,
        [property: JsonPropertyName("sourceReels")] string SourceReels,
        [property: JsonPropertyName("confidence")] int Confidence);
}
