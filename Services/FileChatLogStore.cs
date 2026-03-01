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
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

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

            await _semaphore.WaitAsync();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                return JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
            }
            catch
            {
                return new List<ChatMessage>();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SaveAsync(string chatId, List<ChatMessage> messages)
        {
            var path = PathFor(chatId);
            var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });

            await _semaphore.WaitAsync();
            try
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task ClearAsync(string chatId)
        {
            var path = PathFor(chatId);

            await _semaphore.WaitAsync();
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            finally
            {
                _semaphore.Release();
            }

            await Task.CompletedTask;
        }
    }
}