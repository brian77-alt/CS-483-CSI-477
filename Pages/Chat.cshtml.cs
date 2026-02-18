using AdvisorDb;
using Google.Cloud.AIPlatform.V1;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.Json;

namespace CS_483_CSI_477.Pages
{
    public class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class ChatModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly DatabaseHelper _dbHelper;
        private readonly Services.IChatLogStore _chatLogStore;
        private readonly ILogger<ChatModel> _logger;

        public List<ChatMessage> Messages { get; set; } = new();
        public string ChatId { get; set; } = "";

        [BindProperty]
        public string UserMessage { get; set; } = "";

        public ChatModel(
            IConfiguration configuration,
            DatabaseHelper dbHelper,
            Services.IChatLogStore chatLogStore,
            ILogger<ChatModel> logger)
        {
            _configuration = configuration;
            _dbHelper = dbHelper;
            _chatLogStore = chatLogStore;
            _logger = logger;
        }

        public async Task OnGetAsync()
        {
            ChatId = HttpContext.Session.GetString("ChatId") ?? Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString("ChatId", ChatId);
            Messages = await _chatLogStore.LoadAsync(ChatId);
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ChatId = HttpContext.Session.GetString("ChatId") ?? Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString("ChatId", ChatId);
            Messages = await _chatLogStore.LoadAsync(ChatId);

            if (string.IsNullOrWhiteSpace(UserMessage))
            {
                return Page();
            }

            Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = UserMessage,
                Timestamp = DateTime.Now
            });

            try
            {
                var aiResponse = await GetGeminiResponseAsync(UserMessage);
                Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = aiResponse,
                    Timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting AI response");
                Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "I apologize, but I'm having trouble processing your request right now. Please try again later.",
                    Timestamp = DateTime.Now
                });
            }

            await _chatLogStore.SaveAsync(ChatId, Messages);
            UserMessage = "";
            return Page();
        }

        public async Task<IActionResult> OnPostClearAsync()
        {
            ChatId = HttpContext.Session.GetString("ChatId") ?? "";
            if (!string.IsNullOrEmpty(ChatId))
            {
                await _chatLogStore.ClearAsync(ChatId);
            }
            HttpContext.Session.Remove("ChatId");
            return RedirectToPage();
        }

        private async Task<string> GetGeminiResponseAsync(string userMessage)
        {
            var apiKey = _configuration["Gemini:ApiKey"];
            var model = _configuration["Gemini:Model"] ?? "gemini-2.0-flash-exp";

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException("Gemini API key not configured");
            }

            var systemPrompt = await BuildSystemPromptAsync();
            var conversationHistory = BuildConversationHistory();

            var client = new PredictionServiceClientBuilder
            {
                Endpoint = "us-central1-aiplatform.googleapis.com",
                JsonCredentials = JsonSerializer.Serialize(new
                {
                    type = "authorized_user",
                    client_id = "fake",
                    client_secret = "fake",
                    refresh_token = "fake"
                })
            }.Build();

            var request = new GenerateContentRequest
            {
                Model = $"projects/gemini-api-experimental/locations/us-central1/publishers/google/models/{model}",
                Contents =
                {
                    new Content
                    {
                        Role = "user",
                        Parts = { new Part { Text = systemPrompt + "\n\n" + conversationHistory + "\n\nUser: " + userMessage } }
                    }
                }
            };

            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

                var jsonRequest = JsonSerializer.Serialize(new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = systemPrompt + "\n\n" + conversationHistory + "\n\nUser: " + userMessage } }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        topP = 0.95,
                        topK = 40,
                        maxOutputTokens = 2048
                    }
                });

                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Gemini API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new Exception($"Gemini API error: {response.StatusCode}");
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var contentElement))
                    {
                        if (contentElement.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                        {
                            var firstPart = parts[0];
                            if (firstPart.TryGetProperty("text", out var text))
                            {
                                return text.GetString() ?? "I couldn't generate a response.";
                            }
                        }
                    }
                }

                return "I couldn't generate a response. Please try again.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API");
                throw;
            }
        }

        private async Task<string> BuildSystemPromptAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an academic advisor AI for the University of Southern Indiana (USI).");
            sb.AppendLine("You help students with degree planning, course selection, and academic questions.");
            sb.AppendLine();
            sb.AppendLine("Available degree programs and requirements:");
            sb.AppendLine();

            try
            {
                var degreeQuery = "SELECT DegreeName, DegreeCode, TotalCreditsRequired FROM DegreePrograms WHERE IsActive = 1";
                var degrees = _dbHelper.ExecuteQuery(degreeQuery, out _);

                if (degrees != null)
                {
                    foreach (System.Data.DataRow row in degrees.Rows)
                    {
                        sb.AppendLine($"- {row["DegreeName"]} ({row["DegreeCode"]}): {row["TotalCreditsRequired"]} credits");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Be helpful, friendly, and provide accurate academic guidance based on the data above.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load degree programs for system prompt");
            }

            return sb.ToString();
        }

        private string BuildConversationHistory()
        {
            var sb = new StringBuilder();
            foreach (var msg in Messages.TakeLast(10))
            {
                if (msg.Role == "user")
                    sb.AppendLine($"User: {msg.Content}");
                else if (msg.Role == "assistant")
                    sb.AppendLine($"Assistant: {msg.Content}");
            }
            return sb.ToString();
        }
    }
}