using CS_483_CSI_477.Pages;
<<<<<<< HEAD
using Microsoft.AspNetCore.Http;
=======
using System.Runtime.Intrinsics.Arm;
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
using System.Text.Json;

namespace CS_483_CSI_477.Services
{
<<<<<<< HEAD
    public class ChatThreadInfo
    {
        public string ChatId { get; set; } = "";
        public string Title { get; set; } = "New Chat";
        public string Preview { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int MessageCount { get; set; }
        public string Summary { get; set; } = "";
    }

    internal class ChatMeta
    {
        public string ChatId { get; set; } = "";
        public int StudentId { get; set; }
        public string Title { get; set; } = "New Chat";
        public string Preview { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int MessageCount { get; set; }
        public string Summary { get; set; } = "";
    }

=======
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
    public interface IChatLogStore
    {
        Task<List<ChatMessage>> LoadAsync(string chatId);
        Task SaveAsync(string chatId, List<ChatMessage> messages);
        Task ClearAsync(string chatId);
<<<<<<< HEAD

        Task<string> CreateChatAsync(int studentId, string? title = null);
        Task<List<ChatThreadInfo>> GetChatsForStudentAsync(int studentId);
        Task<bool> ExistsAsync(int studentId, string chatId);
        Task DeleteChatAsync(int studentId, string chatId);

        Task<string?> GetSummaryAsync(int studentId, string chatId);
        Task SaveSummaryAsync(int studentId, string chatId, string summary);
=======
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
    }

    public class FileChatLogStore : IChatLogStore
    {
        private readonly string _baseDir;
<<<<<<< HEAD
        private readonly IHttpContextAccessor _httpContextAccessor;
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public FileChatLogStore(IWebHostEnvironment env, IHttpContextAccessor httpContextAccessor)
        {
            _baseDir = Path.Combine(env.ContentRootPath, "App_Data", "ChatLogs", "students");
            _httpContextAccessor = httpContextAccessor;
            Directory.CreateDirectory(_baseDir);
        }

        private int GetCurrentStudentId()
        {
            var sid = _httpContextAccessor.HttpContext?.Session.GetInt32("StudentID");
            if (!sid.HasValue || sid.Value <= 0)
                throw new InvalidOperationException("Student session not found for chat storage.");

            return sid.Value;
        }

        private string StudentDir(int studentId)
        {
            var dir = Path.Combine(_baseDir, studentId.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string ChatDir(int studentId, string chatId)
        {
            var dir = Path.Combine(StudentDir(studentId), chatId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string MetaPath(int studentId, string chatId) => Path.Combine(ChatDir(studentId, chatId), "meta.json");
        private string MessagesPath(int studentId, string chatId) => Path.Combine(ChatDir(studentId, chatId), "messages.json");

        public async Task<string> CreateChatAsync(int studentId, string? title = null)
        {
            var chatId = Guid.NewGuid().ToString("N");
            var meta = new ChatMeta
            {
                ChatId = chatId,
                StudentId = studentId,
                Title = string.IsNullOrWhiteSpace(title) ? "New Chat" : title.Trim(),
                Preview = "",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                MessageCount = 0,
                Summary = ""
            };

            await _semaphore.WaitAsync();
            try
            {
                Directory.CreateDirectory(ChatDir(studentId, chatId));
                await File.WriteAllTextAsync(MetaPath(studentId, chatId), JsonSerializer.Serialize(meta, JsonOptions));
                await File.WriteAllTextAsync(MessagesPath(studentId, chatId), "[]");
            }
            finally
            {
                _semaphore.Release();
            }

            return chatId;
        }

        public async Task<List<ChatMessage>> LoadAsync(string chatId)
        {
            var studentId = GetCurrentStudentId();
            var path = MessagesPath(studentId, chatId);

            if (!File.Exists(path))
                return new List<ChatMessage>();
=======
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
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21

            await _semaphore.WaitAsync();
            try
            {
<<<<<<< HEAD
                var json = await File.ReadAllTextAsync(path);
=======
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
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
<<<<<<< HEAD
            var studentId = GetCurrentStudentId();
            var metaPath = MetaPath(studentId, chatId);
            var messagesPath = MessagesPath(studentId, chatId);
=======
            var path = PathFor(chatId);
            var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21

            await _semaphore.WaitAsync();
            try
            {
<<<<<<< HEAD
                Directory.CreateDirectory(ChatDir(studentId, chatId));

                var json = JsonSerializer.Serialize(messages, JsonOptions);
                await File.WriteAllTextAsync(messagesPath, json);

                var meta = await ReadMetaInternalAsync(studentId, chatId) ?? new ChatMeta
                {
                    ChatId = chatId,
                    StudentId = studentId,
                    CreatedAt = DateTime.UtcNow
                };

                meta.MessageCount = messages.Count;
                meta.UpdatedAt = DateTime.UtcNow;
                meta.Preview = BuildPreview(messages);
                if (meta.Title == "New Chat" || string.IsNullOrWhiteSpace(meta.Title))
                    meta.Title = BuildTitle(messages);

                await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOptions));
=======
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json);
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task ClearAsync(string chatId)
        {
<<<<<<< HEAD
            var studentId = GetCurrentStudentId();
=======
            var path = PathFor(chatId);
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21

            await _semaphore.WaitAsync();
            try
            {
<<<<<<< HEAD
                var meta = await ReadMetaInternalAsync(studentId, chatId);
                if (meta == null)
                    return;

                meta.MessageCount = 0;
                meta.Preview = "";
                meta.Summary = "";
                meta.UpdatedAt = DateTime.UtcNow;

                await File.WriteAllTextAsync(MessagesPath(studentId, chatId), "[]");
                await File.WriteAllTextAsync(MetaPath(studentId, chatId), JsonSerializer.Serialize(meta, JsonOptions));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<List<ChatThreadInfo>> GetChatsForStudentAsync(int studentId)
        {
            var result = new List<ChatThreadInfo>();
            var dir = StudentDir(studentId);

            if (!Directory.Exists(dir))
                return result;

            await _semaphore.WaitAsync();
            try
            {
                foreach (var chatDir in Directory.GetDirectories(dir))
                {
                    var chatId = Path.GetFileName(chatDir);
                    var meta = await ReadMetaInternalAsync(studentId, chatId);
                    if (meta == null) continue;

                    result.Add(new ChatThreadInfo
                    {
                        ChatId = meta.ChatId,
                        Title = meta.Title,
                        Preview = meta.Preview,
                        CreatedAt = meta.CreatedAt.ToLocalTime(),
                        UpdatedAt = meta.UpdatedAt.ToLocalTime(),
                        MessageCount = meta.MessageCount,
                        Summary = meta.Summary
                    });
=======
                if (File.Exists(path))
                {
                    File.Delete(path);
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
                }
            }
            finally
            {
                _semaphore.Release();
            }

<<<<<<< HEAD
            return result
                .OrderByDescending(c => c.UpdatedAt)
                .ToList();
        }

        public Task<bool> ExistsAsync(int studentId, string chatId)
        {
            var exists = File.Exists(MetaPath(studentId, chatId)) || File.Exists(MessagesPath(studentId, chatId));
            return Task.FromResult(exists);
        }

        public async Task DeleteChatAsync(int studentId, string chatId)
        {
            var dir = ChatDir(studentId, chatId);

            await _semaphore.WaitAsync();
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string?> GetSummaryAsync(int studentId, string chatId)
        {
            await _semaphore.WaitAsync();
            try
            {
                var meta = await ReadMetaInternalAsync(studentId, chatId);
                return meta?.Summary;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SaveSummaryAsync(int studentId, string chatId, string summary)
        {
            await _semaphore.WaitAsync();
            try
            {
                var meta = await ReadMetaInternalAsync(studentId, chatId) ?? new ChatMeta
                {
                    ChatId = chatId,
                    StudentId = studentId,
                    CreatedAt = DateTime.UtcNow
                };

                meta.Summary = summary ?? "";
                meta.UpdatedAt = DateTime.UtcNow;

                await File.WriteAllTextAsync(MetaPath(studentId, chatId), JsonSerializer.Serialize(meta, JsonOptions));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<ChatMeta?> ReadMetaInternalAsync(int studentId, string chatId)
        {
            var path = MetaPath(studentId, chatId);
            if (!File.Exists(path))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<ChatMeta>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildPreview(List<ChatMessage> messages)
        {
            var last = messages.LastOrDefault();
            if (last == null || string.IsNullOrWhiteSpace(last.Content))
                return "";

            var text = last.Content.Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 90 ? text : text[..90] + "...";
        }

        private static string BuildTitle(List<ChatMessage> messages)
        {
            var firstUser = messages.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
            if (firstUser == null)
                return "New Chat";

            var text = firstUser.Content.Trim().Replace("\r", " ").Replace("\n", " ");
            text = text.Length <= 40 ? text : text[..40].Trim() + "...";
            return string.IsNullOrWhiteSpace(text) ? "New Chat" : text;
=======
            await Task.CompletedTask;
>>>>>>> e316d47c9015c6fb5708110f0b70919944db7e21
        }
    }
}