using CS_483_CSI_477.Pages;
using System.Runtime.Intrinsics.Arm;
using System.Text.Json;

namespace CS_483_CSI_477.Services
{
    public interface IChatLogStore
    {
        Task<List<ChatMessage>> LoadAsync(string chatId);
        Task SaveAsync(string chatId, List<ChatMessage> messages);
        Task ClearAsync(string chatId);
    }

    public class FileChatLogStore : IChatLogStore
    {
        private readonly string _baseDir;

        public FileChatLogStore(IWebHostEnvironment env)
        {
            _baseDir = Path.Combine(env.ContentRootPath, "App_Data", "ChatLogs");
            Directory.CreateDirectory(_baseDir);
        }

        private string PathFor(string chatId) => Path.Combine(_baseDir, $"{chatId}.json");

        public async Task<List<ChatMessage>> LoadAsync(string chatId)
        {
            var path = PathFor(chatId);
            if (!File.Exists(path)) return new List<ChatMessage>();

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
            }
            catch
            {
                return new List<ChatMessage>();
            }
        }

        public async Task SaveAsync(string chatId, List<ChatMessage> messages)
        {
            var path = PathFor(chatId);
            var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        public Task ClearAsync(string chatId)
        {
            var path = PathFor(chatId);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }
    }
}