using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text;
using System.Text.Json;

namespace CS_483_CSI_477.Pages;

public class ChatModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public ChatModel(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    [BindProperty]
    public string UserMessage { get; set; }

    public string AiResponse { get; set; }

    public async Task OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(UserMessage))
            return;

        var apiKey = _configuration["Gemini:ApiKey"];
        var client = _httpClientFactory.CreateClient();

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new
                        {
                            text =
                                "You are an AI academic advisor. " +
                                "Give clear, concise, and helpful advice.\n\n" +
                                "Student question: " + UserMessage
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);

        var response = await client.PostAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent?key={apiKey}",
            new StringContent(json, Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            AiResponse = "The AI advisor is currently unavailable.";
            return;
        }

        var responseText = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseText);

        AiResponse = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
    }
}
