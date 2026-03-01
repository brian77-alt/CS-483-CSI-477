using CS_483_CSI_477.Pages;
using System.Text;
using System.Text.Json;

namespace CS_483_CSI_477.Services
{
    public class GeminiService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _config;
        private readonly ILogger<GeminiService> _logger;

        public GeminiService(HttpClient http, IConfiguration config, ILogger<GeminiService> logger)
        {
            _http = http;
            _config = config;
            _logger = logger;
        }

        public async Task<string> GenerateWithHistoryAsync(List<ChatMessage> conversationHistory, string currentPrompt)
        {
            var apiKey = _config["Gemini:ApiKey"];
            var model = _config["Gemini:Model"] ?? "gemini-2.5-flash";
            var apiBase = _config["Gemini:ApiBaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Gemini API key not configured (Gemini:ApiKey).");

            // Build conversation history for Gemini
            var contents = new List<object>();

            // Add previous messages (max last 6 messages to stay within token limits)
            var recentHistory = conversationHistory.TakeLast(6).ToList();
            foreach (var msg in recentHistory)
            {
                var role = msg.Role == "assistant" ? "model" : "user";
                contents.Add(new
                {
                    role = role,
                    parts = new object[] { new { text = msg.Content } }
                });
            }

            // Add current prompt
            contents.Add(new
            {
                role = "user",
                parts = new object[] { new { text = currentPrompt } }
            });

            var requestBody = new
            {
                contents = contents,
                generationConfig = new
                {
                    temperature = 0.2,
                    topP = 0.9,
                    topK = 40,
                    maxOutputTokens = 1600
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{apiBase}/models/{model}:generateContent?key={apiKey}";
            var resp = await _http.PostAsync(url, content);
            var respJson = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini error {Status} Body: {Body}", resp.StatusCode, respJson);
                throw new Exception($"Gemini API error: {resp.StatusCode}");
            }

            return ExtractText(respJson);
        }

        private static string ExtractText(string responseJson)
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return "No response generated.";

            var content = candidates[0].GetProperty("content");

            if (!content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0)
                return "No response generated.";

            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
            }

            var final = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(final) ? "No response generated." : final;
        }
    }
}