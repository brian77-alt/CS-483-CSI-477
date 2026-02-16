using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text;
using System.Text.Json;

namespace CS_483_CSI_477.Pages;

public class ChatModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatModel> _logger;
    private readonly IChatLogStore _chatLogStore;

    public ChatModel(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChatModel> logger,
        IChatLogStore chatLogStore)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _chatLogStore = chatLogStore;
    }

    [BindProperty] public string UserMessage { get; set; } = "";
    [BindProperty] public IFormFile UploadedPdf { get; set; }

    public string ErrorMessage { get; set; }
    public List<ChatMessage> ChatHistory { get; set; } = new();

    public string PdfFileName { get; set; }

    private const string PDF_FILENAME_KEY = "PdfFileName";
    private const string PDF_BYTES_KEY = "PdfBytesBase64";

    // identify a "user" without auth: stable per browser via session
    private string ChatId
    {
        get
        {
            const string key = "ChatId";
            var existing = HttpContext.Session.GetString(key);
            if (!string.IsNullOrEmpty(existing)) return existing;

            var id = Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString(key, id);
            return id;
        }
    }

    private static readonly string SystemPrompt = @"You are an AI Academic Advisor helping college students with their academic planning and course decisions.

Be friendly, encouraging, and provide actionable advice. Keep responses concise but helpful.

When a student provides a PDF academic plan:
- summarize completed vs remaining requirements
- identify prerequisite issues
- suggest a smart next-semester schedule
- warn about overload / sequencing problems

If the student asks general questions, answer normally.";

    public async Task OnGetAsync()
    {
        ChatHistory = await _chatLogStore.LoadAsync(ChatId);
        PdfFileName = HttpContext.Session.GetString(PDF_FILENAME_KEY);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ChatHistory = await _chatLogStore.LoadAsync(ChatId);
        PdfFileName = HttpContext.Session.GetString(PDF_FILENAME_KEY);

        // 1) If PDF uploaded, store its bytes in session (base64) so we can reuse it in later messages
        if (UploadedPdf != null && UploadedPdf.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream();
                await UploadedPdf.CopyToAsync(ms);
                var base64 = Convert.ToBase64String(ms.ToArray());

                HttpContext.Session.SetString(PDF_BYTES_KEY, base64);
                HttpContext.Session.SetString(PDF_FILENAME_KEY, UploadedPdf.FileName);
                PdfFileName = UploadedPdf.FileName;

                ChatHistory.Add(new ChatMessage
                {
                    Role = "system",
                    Content = $"?? Academic plan loaded: {UploadedPdf.FileName}"
                });

                await _chatLogStore.SaveAsync(ChatId, ChatHistory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading PDF");
                ErrorMessage = "Error processing PDF file. Please try another PDF.";
                return Page();
            }
        }

        if (string.IsNullOrWhiteSpace(UserMessage))
            return Page();

        var apiKey = _configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ErrorMessage = "Gemini API key is not configured.";
            return Page();
        }

        ChatHistory.Add(new ChatMessage { Role = "user", Content = UserMessage });

        try
        {
            var aiText = await CallGeminiAsync(apiKey, UserMessage);

            ChatHistory.Add(new ChatMessage { Role = "assistant", Content = aiText });
            await _chatLogStore.SaveAsync(ChatId, ChatHistory);

            UserMessage = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini call failed");
            ErrorMessage = ex.Message;

            // remove last user msg (optional)
            if (ChatHistory.Count > 0 && ChatHistory.Last().Role == "user")
                ChatHistory.RemoveAt(ChatHistory.Count - 1);

            await _chatLogStore.SaveAsync(ChatId, ChatHistory);
        }

        return Page();
    }

    private async Task<string> CallGeminiAsync(string apiKey, string userText)
    {
        // IMPORTANT: v1beta + a model that exists for your key
        // Docs show v1beta + gemini-2.5-flash. :contentReference[oaicite:3]{index=3}
        var model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);

        // Build "contents" as real chat turns (better than stuffing everything into one text prompt)
        var contents = new List<object>();

        // System instruction as first turn
        contents.Add(new
        {
            role = "user",
            parts = new[] { new { text = SystemPrompt } }
        });

        // If we have a PDF stored, attach it as inline_data (Gemini supports PDF input). :contentReference[oaicite:4]{index=4}
        var pdfBase64 = HttpContext.Session.GetString(PDF_BYTES_KEY);
        if (!string.IsNullOrEmpty(pdfBase64))
        {
            contents.Add(new
            {
                role = "user",
                parts = new object[]
                {
                    new { text = "Here is the student's academic plan PDF. Use it as context when answering." },
                    new
                    {
                        inline_data = new
                        {
                            mime_type = "application/pdf",
                            data = pdfBase64
                        }
                    }
                }
            });
        }

        // Add last N chat turns (skip system)
        foreach (var msg in ChatHistory.Where(m => m.Role != "system").TakeLast(10))
        {
            var role = msg.Role == "assistant" ? "model" : "user";
            contents.Add(new
            {
                role,
                parts = new[] { new { text = msg.Content } }
            });
        }

        // Add current user question
        contents.Add(new
        {
            role = "user",
            parts = new[] { new { text = userText } }
        });

        var requestBody = new
        {
            contents,
            generationConfig = new
            {
                temperature = 0.6,
                topP = 0.95,
                maxOutputTokens = 1024
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var resp = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var respText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            // include the API message if possible
            try
            {
                using var doc = JsonDocument.Parse(respText);
                var msg = doc.RootElement.GetProperty("error").GetProperty("message").GetString();
                throw new Exception($"Gemini API error: {msg}");
            }
            catch
            {
                throw new Exception($"Gemini API error ({resp.StatusCode}).");
            }
        }

        using (var doc = JsonDocument.Parse(respText))
        {
            // response: candidates[0].content.parts[0].text
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return "I couldn't generate a response. Try rephrasing your question.";

            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            if (parts.GetArrayLength() == 0) return "No response generated.";

            return parts[0].GetProperty("text").GetString() ?? "No response generated.";
        }
    }

    public async Task<IActionResult> OnPostClearHistoryAsync()
    {
        HttpContext.Session.Remove(PDF_BYTES_KEY);
        HttpContext.Session.Remove(PDF_FILENAME_KEY);

        await _chatLogStore.ClearAsync(ChatId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemovePdfAsync()
    {
        HttpContext.Session.Remove(PDF_BYTES_KEY);
        HttpContext.Session.Remove(PDF_FILENAME_KEY);

        ChatHistory = await _chatLogStore.LoadAsync(ChatId);
        ChatHistory.Add(new ChatMessage { Role = "system", Content = "?? PDF removed." });
        await _chatLogStore.SaveAsync(ChatId, ChatHistory);

        return RedirectToPage();
    }
}

public class ChatMessage
{
    public string Role { get; set; } = "";   // "user", "assistant", "system"
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
