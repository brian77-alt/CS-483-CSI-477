using CS_483_CSI_477.Pages;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CS_483_CSI_477.Services
{
    public class GeminiService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _config;
        private readonly ILogger<GeminiService> _logger;
        private readonly IMemoryCache _cache;

        public GeminiService(HttpClient http, IConfiguration config,
            ILogger<GeminiService> logger, IMemoryCache cache)
        {
            _http = http;
            _config = config;
            _logger = logger;
            _cache = cache;
        }

        public async Task<string> GenerateWithHistoryAsync(
            List<ChatMessage> conversationHistory, string currentPrompt)
        {
            var apiKey = _config["Gemini:ApiKey"];
            var model = _config["Gemini:Model"] ?? "gemini-2.5-flash";
            var fallbackModel = _config["Gemini:FallbackModel"] ?? "gemini-2.5-flash-lite";
            var apiBase = _config["Gemini:ApiBaseUrl"]
                ?? "https://generativelanguage.googleapis.com/v1beta";

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Gemini API key not configured.");

            // Cache key based on prompt hash (skip caching for personalized responses)
            var isGenericQuestion = IsGenericQuestion(currentPrompt);
            if (isGenericQuestion)
            {
                var cacheKey = "gemini_" + GetHash(currentPrompt);
                if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
                {
                    _logger.LogInformation("Gemini cache hit");
                    return cached;
                }
            }

            // Only keep last 10 messages to reduce token count and improve speed
            var recentHistory = conversationHistory.TakeLast(10).ToList();

            var contents = new List<object>();
            foreach (var msg in recentHistory)
            {
                contents.Add(new
                {
                    role = msg.Role == "assistant" ? "model" : "user",
                    parts = new object[] { new { text = TruncateMessage(msg.Content, 500) } }
                });
            }

            contents.Add(new
            {
                role = "user",
                parts = new object[] { new { text = currentPrompt } }
            });

            var requestBody = new
            {
                contents,
                generationConfig = new
                {
                    temperature = 0.2,
                    topP = 0.9,
                    topK = 40,
                    maxOutputTokens = 8192,
                    candidateCount = 1
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{apiBase}/models/{model}:generateContent?key={apiKey}";

            var resp = await _http.PostAsync(url, content);
            var respJson = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && model != fallbackModel)
                {
                    _logger.LogWarning("Rate limited on {Model}, falling back to {Fallback}", model, fallbackModel);
                    var fallbackUrl = $"{apiBase}/models/{fallbackModel}:generateContent?key={apiKey}";
                    using var fallbackContent = new StringContent(json, Encoding.UTF8, "application/json");
                    var fallbackResp = await _http.PostAsync(fallbackUrl, fallbackContent);
                    var fallbackRespJson = await fallbackResp.Content.ReadAsStringAsync();
                    if (fallbackResp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Fallback to {Fallback} succeeded", fallbackModel);
                        return ExtractText(fallbackRespJson);
                    }
                    _logger.LogError("Fallback also failed: {Body}", fallbackRespJson);
                }
                _logger.LogError("Gemini error {Status} Body: {Body}", resp.StatusCode, respJson);
                throw new Exception($"Gemini API error: {resp.StatusCode}");
            }

            var result = ExtractText(respJson);

            // Cache generic responses for 10 minutes
            if (isGenericQuestion && !string.IsNullOrEmpty(result))
            {
                var cacheKey = "gemini_" + GetHash(currentPrompt);
                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
            }

            return result;
        }

        private static bool IsGenericQuestion(string prompt)
        {
            var genericKeywords = new[] {
                "what is", "explain", "define", "how does", "what are core 39",
                "graduation requirements", "what credits"
            };
            return genericKeywords.Any(k =>
                prompt.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private static string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
                return message;
            return message[..maxLength] + "...";
        }

        private static string GetHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..16];
        }

        private string ExtractText(string responseJson)
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Log finish reason
            try
            {
                if (root.TryGetProperty("candidates", out var cands2) &&
                    cands2.GetArrayLength() > 0)
                    if (cands2[0].TryGetProperty("finishReason", out var reason))
                        _logger.LogInformation($"Gemini finishReason: {reason.GetString()}");
            }
            catch { }

            if (!root.TryGetProperty("candidates", out var candidates) ||
                candidates.GetArrayLength() == 0)
                return "No response generated.";

            var content = candidates[0].GetProperty("content");
            if (!content.TryGetProperty("parts", out var parts) ||
                parts.GetArrayLength() == 0)
                return "No response generated.";

            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());

            var final = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(final) ? "No response generated." : final;
        }
    }
}