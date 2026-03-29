using AdvisorDb;
using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        private readonly DatabaseHelper _dbHelper;
        private readonly IChatLogStore _chatLogStore;
        private readonly ILogger<ChatModel> _logger;
        private readonly PdfService _pdfService;
        private readonly CourseCatalogService _catalogService;
        private readonly GeminiService _gemini;
        private readonly PdfRagService _ragService;
        private readonly SupportingDocsRagService _docsRagService;
        private readonly IConfiguration _configuration;
        private readonly PrerequisiteService _prereqService;
        private readonly PlannerCommandService _plannerCommands;
        private readonly GpaCalculatorService _gpaCalc;
        private readonly AccountHoldService _holdService;
        private readonly ChatMemoryService _chatMemory;

        public List<ChatMessage> Messages { get; set; } = new();
        public List<ChatThreadInfo> Chats { get; set; } = new();
        public string ChatId { get; set; } = "";
        public string? CurrentChatTitle { get; set; }

        public string? ErrorMessage { get; set; }
        public string? PdfFileName { get; set; }
        public string? TranscriptFileName { get; set; }
        public string? LoadedStudentSummary { get; set; }

        [BindProperty] public string UserMessage { get; set; } = "";
        [BindProperty] public IFormFile? UploadedPdf { get; set; }

        private const string PDF_FILENAME_KEY = "PdfFileName";
        private const string PDF_PAGES_JSON_KEY = "PdfPagesJson";
        private const string STUDENT_CONTEXT_KEY = "StudentContextText";
        private const string CATALOG_JSON_KEY = "ParsedCatalogJson";
        private const string BULLETIN_YEAR_KEY = "BulletinYear";
        private const string ALT_PDF_PAGES_JSON_KEY = "AltPdfPagesJson";
        private const string ALT_PDF_FILENAME_KEY = "AltPdfFileName";
        private const string ALT_BULLETIN_YEAR_KEY = "AltBulletinYear";
        private const string TRANSCRIPT_CONTEXT_KEY = "DegreeWorksContext";
        private const string TRANSCRIPT_FILENAME_KEY = "DegreeWorksFileName";

        public ChatModel(
            DatabaseHelper dbHelper,
            IChatLogStore chatLogStore,
            ILogger<ChatModel> logger,
            PdfService pdfService,
            CourseCatalogService catalogService,
            GeminiService gemini,
            PdfRagService ragService,
            SupportingDocsRagService docsRagService,
            IConfiguration configuration,
            PrerequisiteService prereqService,
            PlannerCommandService plannerCommands,
            GpaCalculatorService gpaCalc,
            AccountHoldService holdService,
            ChatMemoryService chatMemory)
        {
            _dbHelper = dbHelper;
            _chatLogStore = chatLogStore;
            _logger = logger;
            _pdfService = pdfService;
            _catalogService = catalogService;
            _gemini = gemini;
            _ragService = ragService;
            _docsRagService = docsRagService;
            _configuration = configuration;
            _prereqService = prereqService;
            _plannerCommands = plannerCommands;
            _gpaCalc = gpaCalc;
            _holdService = holdService;
            _chatMemory = chatMemory;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");
            if (HttpContext.Session.GetString("Role") == "Admin")
                return RedirectToPage("/AdminDashboard");

            int studentId = HttpContext.Session.GetInt32("StudentID")!.Value;

            await EnsureChatIdAsync(studentId);
            await TryAutoLoadBulletinAsync();

            PdfFileName = HttpContext.Session.GetString(PDF_FILENAME_KEY);

            // Auto-load transcript context from DB BEFORE building student context
            // so the transcript gets injected into the AI prompt
            if (string.IsNullOrEmpty(HttpContext.Session.GetString(TRANSCRIPT_CONTEXT_KEY)))
            {
                var saved = LoadTranscriptFromDb(studentId);
                if (!string.IsNullOrEmpty(saved))
                {
                    HttpContext.Session.SetString(TRANSCRIPT_CONTEXT_KEY, saved);
                    HttpContext.Session.SetString(TRANSCRIPT_FILENAME_KEY, "Degree Works (saved)");
                }
            }

            TranscriptFileName = HttpContext.Session.GetString(TRANSCRIPT_FILENAME_KEY);
            await EnsureStudentContextLoadedAsync();

            Messages = await _chatLogStore.LoadAsync(ChatId);
            await LoadSidebarAsync(studentId);

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");
            if (HttpContext.Session.GetString("Role") == "Admin")
                return RedirectToPage("/AdminDashboard");

            int studentId = HttpContext.Session.GetInt32("StudentID")!.Value;

            await EnsureChatIdAsync(studentId);

            PdfFileName = HttpContext.Session.GetString(PDF_FILENAME_KEY);

            if (string.IsNullOrEmpty(HttpContext.Session.GetString(TRANSCRIPT_CONTEXT_KEY)))
            {
                var saved = LoadTranscriptFromDb(studentId);
                if (!string.IsNullOrEmpty(saved))
                {
                    HttpContext.Session.SetString(TRANSCRIPT_CONTEXT_KEY, saved);
                    HttpContext.Session.SetString(TRANSCRIPT_FILENAME_KEY, "Degree Works (saved)");
                }
            }

            TranscriptFileName = HttpContext.Session.GetString(TRANSCRIPT_FILENAME_KEY);
            await EnsureStudentContextLoadedAsync();
            Messages = await _chatLogStore.LoadAsync(ChatId);
            await LoadSidebarAsync(studentId);

            // PDF upload
            if (UploadedPdf != null && UploadedPdf.Length > 0)
            {
                var ext = Path.GetExtension(UploadedPdf.FileName).ToLowerInvariant();
                if (ext != ".pdf")
                {
                    ErrorMessage = "Only PDF files are allowed.";
                    return Page();
                }

                const long maxBytes = 50L * 1024L * 1024L;
                if (UploadedPdf.Length > maxBytes)
                {
                    ErrorMessage = "PDF is too large (max 50MB).";
                    return Page();
                }

                try
                {
                    using var ms = new MemoryStream();
                    await UploadedPdf.CopyToAsync(ms);

                    var extract = _pdfService.Extract(ms.ToArray(), UploadedPdf.FileName, maxPages: 25, maxCharsTotal: 200_000);

                    HttpContext.Session.SetString(PDF_FILENAME_KEY, UploadedPdf.FileName);
                    PdfFileName = UploadedPdf.FileName;

                    HttpContext.Session.SetString(PDF_PAGES_JSON_KEY, JsonSerializer.Serialize(extract.Pages));

                    var bulletinYear = ExtractBulletinYear(UploadedPdf.FileName);
                    HttpContext.Session.SetString(BULLETIN_YEAR_KEY, bulletinYear);

                    var plan = _catalogService.ParseDegreePlanFromPdfPages(extract.Pages);
                    HttpContext.Session.SetString(CATALOG_JSON_KEY, JsonSerializer.Serialize(plan));

                    var yearWarning = CheckBulletinYearMismatch(bulletinYear);

                    Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content =
                            $"Loaded PDF: {UploadedPdf.FileName} (Bulletin Year: {bulletinYear}). " +
                            $"Extracted {extract.Pages.Count} page(s), {extract.TotalChars:N0} chars. " +
                            $"Parsed {plan.TotalCount} course item(s) (Required: {plan.Required.Count}, Electives: {plan.Electives.Count})." +
                            (string.IsNullOrEmpty(yearWarning) ? "" : $"\n\n⚠️ {yearWarning}"),
                        Timestamp = DateTime.Now
                    });

                    await _chatLogStore.SaveAsync(ChatId, Messages);
                    await _chatMemory.UpdateSummaryAsync(studentId, ChatId, Messages, _chatLogStore);
                    await LoadSidebarAsync(studentId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PDF processing failed");
                    ErrorMessage = "Could not read the PDF. If it is scanned (image-only), OCR is required.";
                    return Page();
                }
            }

            UserMessage = InputSanitizer.SanitizeChat(UserMessage);

            if (string.IsNullOrWhiteSpace(UserMessage))
            {
                await _chatLogStore.SaveAsync(ChatId, Messages);
                return Page();
            }

            var userText = UserMessage.Trim();

            Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = userText,
                Timestamp = DateTime.Now
            });

            LogChatUsage();

            try
            {
                var response = await GetAdvisorResponseAsync(userText);

                Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = response,
                    Timestamp = DateTime.Now
                });

                await _chatLogStore.SaveAsync(ChatId, Messages);
                await _chatMemory.UpdateSummaryAsync(studentId, ChatId, Messages, _chatLogStore);
                await LoadSidebarAsync(studentId);

                UserMessage = "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advisor response failed");
                ErrorMessage = "AI error occurred. Please try again.";

                Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "I'm having trouble processing that right now. Please try again.",
                    Timestamp = DateTime.Now
                });

                await _chatLogStore.SaveAsync(ChatId, Messages);
                await _chatMemory.UpdateSummaryAsync(studentId, ChatId, Messages, _chatLogStore);
                await LoadSidebarAsync(studentId);
            }

            return Page();
        }

        public async Task<IActionResult> OnPostClearAsync()
        {
            var studentId = HttpContext.Session.GetInt32("StudentID");
            if (!studentId.HasValue)
                return RedirectToPage("/Login");

            await EnsureChatIdAsync(studentId.Value);
            await _chatLogStore.ClearAsync(ChatId);
            await _chatMemory.UpdateSummaryAsync(studentId.Value, ChatId, new List<ChatMessage>(), _chatLogStore);

            return RedirectToPage();
        }

        public IActionResult OnPostRemovePdf()
        {
            HttpContext.Session.Remove(PDF_FILENAME_KEY);
            HttpContext.Session.Remove(PDF_PAGES_JSON_KEY);
            HttpContext.Session.Remove(CATALOG_JSON_KEY);
            HttpContext.Session.Remove(BULLETIN_YEAR_KEY);
            HttpContext.Session.Remove(ALT_PDF_PAGES_JSON_KEY);
            HttpContext.Session.Remove(ALT_PDF_FILENAME_KEY);
            HttpContext.Session.Remove(ALT_BULLETIN_YEAR_KEY);
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostNewChatAsync()
        {
            var studentId = HttpContext.Session.GetInt32("StudentID");
            if (!studentId.HasValue)
                return RedirectToPage("/Login");

            var chatId = await _chatLogStore.CreateChatAsync(studentId.Value);
            HttpContext.Session.SetString("ChatId", chatId);

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostOpenChatAsync(string id)
        {
            var studentId = HttpContext.Session.GetInt32("StudentID");
            if (!studentId.HasValue)
                return RedirectToPage("/Login");

            if (string.IsNullOrWhiteSpace(id) || !await _chatLogStore.ExistsAsync(studentId.Value, id))
                return RedirectToPage();

            HttpContext.Session.SetString("ChatId", id);
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteChatAsync(string id)
        {
            var studentId = HttpContext.Session.GetInt32("StudentID");
            if (!studentId.HasValue)
                return RedirectToPage("/Login");

            if (!string.IsNullOrWhiteSpace(id))
                await _chatLogStore.DeleteChatAsync(studentId.Value, id);

            var chats = await _chatLogStore.GetChatsForStudentAsync(studentId.Value);
            if (chats.Count > 0)
                HttpContext.Session.SetString("ChatId", chats[0].ChatId);
            else
                HttpContext.Session.Remove("ChatId");

            return RedirectToPage();
        }

        private async Task LoadSidebarAsync(int studentId)
        {
            Chats = await _chatLogStore.GetChatsForStudentAsync(studentId);
            CurrentChatTitle = Chats.FirstOrDefault(c => c.ChatId == ChatId)?.Title ?? "New Chat";
        }

        private async Task EnsureChatIdAsync(int studentId)
        {
            var existing = HttpContext.Session.GetString("ChatId");

            if (!string.IsNullOrWhiteSpace(existing) && await _chatLogStore.ExistsAsync(studentId, existing))
            {
                ChatId = existing;
                return;
            }

            var chats = await _chatLogStore.GetChatsForStudentAsync(studentId);
            if (chats.Count > 0)
            {
                ChatId = chats[0].ChatId;
            }
            else
            {
                ChatId = await _chatLogStore.CreateChatAsync(studentId);
            }

            HttpContext.Session.SetString("ChatId", ChatId);
        }

        private async Task<string> AddConversationMemoryAsync(string basePrompt, string userQuestion)
        {
            var sid = HttpContext.Session.GetInt32("StudentID");
            if (!sid.HasValue) return basePrompt;

            var memory = await _chatMemory.BuildMemoryBlockAsync(
                sid.Value,
                ChatId,
                userQuestion,
                Messages,
                _chatLogStore);

            if (string.IsNullOrWhiteSpace(memory))
                return basePrompt;

            return basePrompt + @"

CONVERSATION MEMORY:
Use the following memory only if it is relevant to the student's current question.
Prefer the current database context, planner data, and bulletin/supporting-document context as the source of truth.

" + memory;
        }

        private static string ExtractBulletinYear(string filename)
        {
            var match = Regex.Match(filename, @"(\d{4})-?(\d{4})", RegexOptions.IgnoreCase);
            if (match.Success)
                return $"{match.Groups[1].Value}-{match.Groups[2].Value}";

            match = Regex.Match(filename, @"(\d{4})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var year = int.Parse(match.Groups[1].Value);
                return $"{year}-{year + 1}";
            }

            return "Unknown";
        }

        private string CheckBulletinYearMismatch(string bulletinYear)
        {
            var sid = HttpContext.Session.GetInt32("StudentID");
            if (!sid.HasValue || bulletinYear == "Unknown") return "";

            var query = @"
                SELECT EnrollmentYear
                FROM Students
                WHERE StudentID = @studentId";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = sid.Value }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || result == null || result.Rows.Count == 0)
                return "";

            var enrollmentYear = result.Rows[0]["EnrollmentYear"];
            if (enrollmentYear == DBNull.Value) return "";

            var entryYear = Convert.ToInt32(enrollmentYear);
            var bulletinStartYear = int.Parse(bulletinYear.Split('-')[0]);

            if (bulletinStartYear < entryYear)
            {
                return $"You enrolled in {entryYear}, but this bulletin is from {bulletinYear}. You should normally follow the bulletin from your entry year ({entryYear}-{entryYear + 1}) unless advised otherwise.";
            }

            if (bulletinStartYear > entryYear + 1)
            {
                return $"This bulletin ({bulletinYear}) is newer than your entry year ({entryYear}). Check with your advisor before following newer requirements.";
            }

            return "";
        }

        private async Task<bool> TryAutoLoadBulletinAsync()
        {
            if (!string.IsNullOrEmpty(HttpContext.Session.GetString(PDF_FILENAME_KEY)))
                return true;

            var sid = HttpContext.Session.GetInt32("StudentID");
            if (!sid.HasValue) return false;

            try
            {
                var studentQuery = @"
                    SELECT s.Major, s.EnrollmentYear, dp.DegreeCode
                    FROM Students s
                    LEFT JOIN DegreePrograms dp ON s.Major = dp.DegreeName
                    WHERE s.StudentID = @studentId";

                var studentData = _dbHelper.ExecuteQuery(studentQuery, new[]
                {
                    new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = sid.Value }
                }, out var err);

                if (!string.IsNullOrEmpty(err) || studentData == null || studentData.Rows.Count == 0)
                    return false;

                var degreeCode = studentData.Rows[0]["DegreeCode"]?.ToString() ?? "";
                var enrollmentYear = studentData.Rows[0]["EnrollmentYear"];

                if (string.IsNullOrEmpty(degreeCode) || enrollmentYear == DBNull.Value)
                    return false;

                var currentMonth = DateTime.Now.Month;
                var currentCalendarYear = DateTime.Now.Year;
                string bulletinYearNeeded = currentMonth >= 8
                    ? $"{currentCalendarYear}-{currentCalendarYear + 1}"
                    : $"{currentCalendarYear - 1}-{currentCalendarYear}";

                string majorKeyword = degreeCode switch
                {
                    "CS-BS" => "Computer Science",
                    "CIS-BS" => "Computer Information Systems",
                    _ => degreeCode
                };

                var bulletinQuery = @"
                    SELECT BulletinID, FileName, FilePath, BulletinYear
                    FROM Bulletins
                    WHERE IsActive = 1
                    AND BulletinCategory = 'Major'
                    AND FileName LIKE @majorKeyword
                    AND FileName NOT LIKE '%Minor%'
                    ORDER BY
                        CASE WHEN BulletinYear = @bulletinYear THEN 0 ELSE 1 END,
                        AcademicYear DESC
                    LIMIT 1";

                var bulletin = _dbHelper.ExecuteQuery(bulletinQuery, new[]
                {
                    new MySqlParameter("@bulletinYear", MySqlDbType.VarChar) { Value = bulletinYearNeeded },
                    new MySqlParameter("@majorKeyword", MySqlDbType.VarChar) { Value = $"%{majorKeyword}%" }
                }, out var berr);

                if (!string.IsNullOrEmpty(berr) || bulletin == null || bulletin.Rows.Count == 0)
                {
                    _logger.LogWarning("No bulletin found for {DegreeCode}, year {BulletinYearNeeded}", degreeCode, bulletinYearNeeded);
                    return false;
                }

                var bulletinRow = bulletin.Rows[0];
                var fileName = bulletinRow["FileName"].ToString() ?? "";
                var filePath = bulletinRow["FilePath"].ToString() ?? "";
                var bulletinYear = bulletinRow["BulletinYear"].ToString() ?? "";

                byte[]? pdfBytes = await DownloadPdfFromPathAsync(filePath);
                if (pdfBytes == null || pdfBytes.Length == 0)
                    return false;

                var extract = _pdfService.Extract(pdfBytes, fileName, maxPages: 25, maxCharsTotal: 200_000);

                HttpContext.Session.SetString(PDF_FILENAME_KEY, fileName);
                HttpContext.Session.SetString(PDF_PAGES_JSON_KEY, JsonSerializer.Serialize(extract.Pages));
                HttpContext.Session.SetString(BULLETIN_YEAR_KEY, bulletinYear);

                var plan = _catalogService.ParseDegreePlanFromPdfPages(extract.Pages);
                HttpContext.Session.SetString(CATALOG_JSON_KEY, JsonSerializer.Serialize(plan));

                PdfFileName = fileName;
                _logger.LogInformation("Auto-loaded bulletin: {FileName} ({BulletinYear})", fileName, bulletinYear);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-load bulletin");
                return false;
            }
        }

        private async Task<(List<PdfPageText> Pages, string FileName, string BulletinYear)> TryLoadBulletinForMajorAsync(string degreeCode)
        {
            var cachedJson = HttpContext.Session.GetString(ALT_PDF_PAGES_JSON_KEY);
            var cachedFileName = HttpContext.Session.GetString(ALT_PDF_FILENAME_KEY) ?? "";
            var cachedYear = HttpContext.Session.GetString(ALT_BULLETIN_YEAR_KEY) ?? "";

            var expectedPhrase = degreeCode == "CIS-BS" ? "Computer Information" : "Computer Science";
            if (!string.IsNullOrEmpty(cachedJson) &&
                cachedFileName.Contains(expectedPhrase, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var cachedPages = JsonSerializer.Deserialize<List<PdfPageText>>(cachedJson) ?? new();
                    return (cachedPages, cachedFileName, cachedYear);
                }
                catch
                {
                }
            }

            var currentMonth = DateTime.Now.Month;
            var currentCalendarYear = DateTime.Now.Year;
            string bulletinYearNeeded = currentMonth >= 8
                ? $"{currentCalendarYear}-{currentCalendarYear + 1}"
                : $"{currentCalendarYear - 1}-{currentCalendarYear}";

            string majorKeyword = degreeCode switch
            {
                "CS-BS" => "Computer Science",
                "CIS-BS" => "Computer Information Systems",
                _ => degreeCode
            };

            var bulletinQuery = @"
                SELECT BulletinID, FileName, FilePath, BulletinYear
                FROM Bulletins
                WHERE IsActive = 1
                AND BulletinCategory = 'Major'
                AND FileName LIKE @majorKeyword
                AND FileName NOT LIKE '%Minor%'
                ORDER BY
                    CASE WHEN BulletinYear = @bulletinYear THEN 0 ELSE 1 END,
                    AcademicYear DESC
                LIMIT 1";

            var bulletin = _dbHelper.ExecuteQuery(bulletinQuery, new[]
            {
                new MySqlParameter("@bulletinYear", MySqlDbType.VarChar) { Value = bulletinYearNeeded },
                new MySqlParameter("@majorKeyword", MySqlDbType.VarChar) { Value = $"%{majorKeyword}%" }
            }, out var berr);

            if (!string.IsNullOrEmpty(berr) || bulletin == null || bulletin.Rows.Count == 0)
            {
                _logger.LogWarning("No bulletin found for {DegreeCode}, year {BulletinYearNeeded}", degreeCode, bulletinYearNeeded);
                return (new List<PdfPageText>(), "", "");
            }

            var row = bulletin.Rows[0];
            var fileName = row["FileName"].ToString() ?? "";
            var filePath = row["FilePath"].ToString() ?? "";
            var yearStr = row["BulletinYear"].ToString() ?? "";

            var pdfBytes = await DownloadPdfFromPathAsync(filePath);
            if (pdfBytes == null || pdfBytes.Length == 0)
                return (new List<PdfPageText>(), "", "");

            var extract = _pdfService.Extract(pdfBytes, fileName, maxPages: 25, maxCharsTotal: 200_000);

            HttpContext.Session.SetString(ALT_PDF_PAGES_JSON_KEY, JsonSerializer.Serialize(extract.Pages));
            HttpContext.Session.SetString(ALT_PDF_FILENAME_KEY, fileName);
            HttpContext.Session.SetString(ALT_BULLETIN_YEAR_KEY, yearStr);

            _logger.LogInformation("On-demand loaded bulletin: {FileName} ({YearStr})", fileName, yearStr);
            return (extract.Pages, fileName, yearStr);
        }

        private async Task<byte[]?> DownloadPdfFromPathAsync(string filePath)
        {
            try
            {
                if (filePath.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase))
                {
                    var localPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath.TrimStart('/'));
                    if (System.IO.File.Exists(localPath))
                        return await System.IO.File.ReadAllBytesAsync(localPath);
                    return null;
                }

                var azureConnStr = _configuration["AzureBlobStorage:ConnectionString"];
                if (string.IsNullOrEmpty(azureConnStr))
                {
                    _logger.LogWarning("Azure connection string not configured");
                    return null;
                }

                var uri = new Uri(filePath);
                var pathParts = uri.AbsolutePath.TrimStart('/').Split('/', 2);

                if (pathParts.Length < 2)
                {
                    _logger.LogError("Invalid blob path: {FilePath}", filePath);
                    return null;
                }

                var containerName = pathParts[0];
                var blobName = pathParts[1];

                var blobServiceClient = new Azure.Storage.Blobs.BlobServiceClient(azureConnStr);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                using var ms = new MemoryStream();
                await blobClient.DownloadToAsync(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download PDF from {FilePath}", filePath);
                return null;
            }
        }

        private async Task EnsureStudentContextLoadedAsync()
        {
            LoadedStudentSummary = null;
            HttpContext.Session.Remove(STUDENT_CONTEXT_KEY);

            var sid = HttpContext.Session.GetInt32("StudentID");
            if (!sid.HasValue) return;

            var ctx = await BuildStudentDbContextAsync(sid.Value);
            HttpContext.Session.SetString(STUDENT_CONTEXT_KEY, ctx);
            LoadedStudentSummary = ctx;
        }

        private Task<string> BuildStudentDbContextAsync(int studentId)
        {
            var sb = new StringBuilder();

            var summarySql = @"
                SELECT
                    CONCAT(s.FirstName, ' ', s.LastName) as FullName,
                    s.StudentID,
                    s.Major,
                    s.CurrentGPA,
                    s.TotalCreditsEarned,
                    s.EnrollmentStatus,
                    s.EnrollmentYear,
                    COALESCE(dp.TotalCreditsRequired, 120) as TotalCreditsRequired,
                    COALESCE(dp.DegreeCode, '') as DegreeCode
                FROM Students s
                LEFT JOIN DegreePrograms dp ON s.Major = dp.DegreeName
                WHERE s.StudentID = @studentId
                LIMIT 1;";

            var summary = _dbHelper.ExecuteQuery(summarySql, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) throw new Exception(err);
            if (summary == null || summary.Rows.Count == 0) throw new Exception("Student not found.");

            var row = summary.Rows[0];

            var fullName = row["FullName"]?.ToString() ?? "Student";
            var major = row["Major"]?.ToString() ?? "Undeclared";
            HttpContext.Session.SetString("StudentMajor", major);
            var gpa = row["CurrentGPA"]?.ToString() ?? "0";
            var earned = row["TotalCreditsEarned"]?.ToString() ?? "0";
            var required = row["TotalCreditsRequired"]?.ToString() ?? "120";
            var status = row["EnrollmentStatus"]?.ToString() ?? "Active";
            var degreeCode = row["DegreeCode"]?.ToString() ?? "";
            var enrollmentYear = row["EnrollmentYear"] != DBNull.Value ? row["EnrollmentYear"].ToString() : "N/A";

            var completedSql = @"
                SELECT
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    sch.Grade,
                    sch.Term,
                    sch.AcademicYear,
                    sch.Status
                FROM StudentCourseHistory sch
                JOIN Courses c ON sch.CourseID = c.CourseID
                WHERE sch.StudentID = @studentId
                AND sch.Status IN ('Completed', 'In Progress')
                ORDER BY sch.Status DESC, sch.AcademicYear DESC, sch.Term DESC;";

            var completed = _dbHelper.ExecuteQuery(completedSql, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var cerr);

            if (!string.IsNullOrEmpty(cerr)) throw new Exception(cerr);

            sb.AppendLine("=== STUDENT DB CONTEXT (authoritative) ===");
            sb.AppendLine($"Name: {fullName}");
            sb.AppendLine($"StudentID: {studentId}");
            sb.AppendLine($"Major: {major}");
            sb.AppendLine($"Enrollment Year: {enrollmentYear}");
            sb.AppendLine($"Enrollment Status: {status}");
            sb.AppendLine($"Current GPA: {gpa}");
            sb.AppendLine($"Credits Earned: {earned} / {required}");
            sb.AppendLine($"Degree Code: {degreeCode}");

            var currentMonth = DateTime.Now.Month;
            var currentSemester = currentMonth >= 8 ? "Fall" : currentMonth >= 5 ? "Summer" : "Spring";
            var currentYear = DateTime.Now.Year;
            var nextSemester = currentSemester == "Fall"
                ? $"Spring {currentYear + 1}"
                : currentSemester == "Spring"
                    ? $"Fall {currentYear}"
                    : $"Fall {currentYear}";

            sb.AppendLine($"Current Semester: {currentSemester} {currentYear}");
            sb.AppendLine($"Next Semester: {nextSemester}");
            sb.AppendLine();
            sb.AppendLine("Completed and In-Progress Courses:");

            if (completed != null && completed.Rows.Count > 0)
            {
                foreach (DataRow r in completed.Rows)
                {
                    var courseStatus = r["Status"].ToString();
                    var statusTag = courseStatus == "In Progress" ? " [IN PROGRESS]" : "";
                    sb.AppendLine($"- {r["CourseCode"]}: {r["CourseName"]} ({r["CreditHours"]} hrs) Grade {r["Grade"]} — {r["Term"]} {r["AcademicYear"]}{statusTag}");
                }
            }
            else
            {
                sb.AppendLine("- (none found)");
            }

            sb.AppendLine();
            sb.AppendLine("Core 39 General Education Progress:");

            var core39Sql = @"
                SELECT
                    c39.CategoryName,
                    c39.CreditsRequired,
                    COALESCE(SUM(c.CreditHours), 0) as CreditsEarned
                FROM Core39Requirements c39
                LEFT JOIN Courses c ON (
                    (c39.CategoryCode IS NOT NULL AND c.CourseCode IN (
                        SELECT TRIM(SUBSTRING_INDEX(SUBSTRING_INDEX(c39.Description, ',', numbers.n), ',', -1))
                        FROM (SELECT 1 n UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5) numbers
                        WHERE CHAR_LENGTH(c39.Description) - CHAR_LENGTH(REPLACE(c39.Description, ',', '')) >= numbers.n - 1
                    ))
                    OR c39.CategoryCode IS NULL
                )
                LEFT JOIN StudentCourseHistory sch ON c.CourseID = sch.CourseID
                    AND sch.StudentID = @studentId
                    AND sch.Status = 'Completed'
                WHERE c39.IsActive = 1
                    AND c39.Core39ID BETWEEN 22 AND 33
                GROUP BY c39.CategoryName, c39.CreditsRequired
                ORDER BY c39.Core39ID";

            var core39 = _dbHelper.ExecuteQuery(core39Sql, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var c39err);

            if (core39 != null && core39.Rows.Count > 0)
            {
                foreach (DataRow r in core39.Rows)
                {
                    sb.AppendLine($"- {r["CategoryName"]}: {r["CreditsEarned"]}/{r["CreditsRequired"]} credits");
                }
            }
            else
            {
                sb.AppendLine("- (Core 39 data not available)");
            }

            sb.AppendLine();
            var gpaCalc = _gpaCalc.CalculateCurrentGpa(studentId);
            sb.AppendLine("GPA Calculation Details:");
            sb.AppendLine($"- Current GPA: {gpaCalc.CurrentGpa} (calculated from {gpaCalc.CompletedCredits} completed credits)");
            sb.AppendLine($"- Total Quality Points: {gpaCalc.CumulativePoints}");
            sb.AppendLine($"- Courses included in GPA: {gpaCalc.Courses.Count}");

            var holdMessage = _holdService.GetActiveHoldsMessage(studentId);
            if (!string.IsNullOrEmpty(holdMessage))
            {
                sb.AppendLine();
                sb.AppendLine(holdMessage);
            }

            sb.AppendLine();
            sb.AppendLine("Current Degree Plan (PlannedCourses):");

            var plannedSql = @"
                SELECT
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    pc.PlannedTerm,
                    pc.PlannedYear,
                    pc.IsCompleted
                FROM PlannedCourses pc
                JOIN Courses c ON pc.CourseID = c.CourseID
                JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
                WHERE sdp.StudentID = @studentId
                  AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
                ORDER BY pc.PlannedYear,
                 CASE WHEN pc.PlannedTerm = 'Spring' THEN 1
                      WHEN pc.PlannedTerm = 'Summer' THEN 2
                      WHEN pc.PlannedTerm = 'Fall'   THEN 3
                      ELSE 4 END,
                 c.CourseCode;";

            var planned = _dbHelper.ExecuteQuery(plannedSql, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var perr);

            if (planned != null && planned.Rows.Count > 0)
            {
                foreach (DataRow r in planned.Rows)
                {
                    var completedTag = Convert.ToInt32(r["IsCompleted"]) == 1 ? " [COMPLETED]" : " [PLANNED]";
                    sb.AppendLine($"- {r["CourseCode"]}: {r["CourseName"]} ({r["CreditHours"]} cr) — {r["PlannedTerm"]} {r["PlannedYear"]}{completedTag}");
                }
            }
            else
            {
                sb.AppendLine("- (No planned courses yet)");
            }

            // Inject Degree Works transcript if available
            var transcriptContext = HttpContext.Session.GetString(TRANSCRIPT_CONTEXT_KEY);
            _logger.LogInformation("=== TRANSCRIPT CONTEXT CHECK === Length: {Length}",
                transcriptContext?.Length ?? 0);
            if (!string.IsNullOrEmpty(transcriptContext))
            {
                sb.AppendLine();
                sb.AppendLine(transcriptContext);
            }

            sb.AppendLine();
            sb.AppendLine("=== END STUDENT DB CONTEXT ===");

            return Task.FromResult(sb.ToString());
        }

        private static bool TryParsePlannerCommand(
            string userMessage,
            out string action,
            out string courseCode,
            out string term1,
            out int year1,
            out string term2,
            out int year2)
        {
            action = "";
            courseCode = "";
            term1 = "";
            year1 = 0;
            term2 = "";
            year2 = 0;

            var msg = userMessage?.Trim() ?? "";
            if (msg.Length == 0) return false;

            var addRx = new Regex(
                @"\b(?<action>add|plan)\b\s+(?<code>[A-Z]{2,4}\s*\d{3})\s+(?:to|in)\s+(?<term>fall|spring|summer)\s+(?<year>\d{4})\b",
                RegexOptions.IgnoreCase);

            var remRx = new Regex(
                @"\b(?<action>remove|delete|unplan)\b\s+(?<code>[A-Z]{2,4}\s*\d{3})\s+from\s+(?<term>fall|spring|summer)\s+(?<year>\d{4})\b",
                RegexOptions.IgnoreCase);

            var moveRx = new Regex(
                @"\b(?<action>move)\b\s+(?<code>[A-Z]{2,4}\s*\d{3})\s+from\s+(?<term1>fall|spring|summer)\s+(?<year1>\d{4})\s+to\s+(?<term2>fall|spring|summer)\s+(?<year2>\d{4})\b",
                RegexOptions.IgnoreCase);

            Match m = moveRx.Match(msg);
            if (m.Success)
            {
                action = "move";
                courseCode = m.Groups["code"].Value;
                term1 = m.Groups["term1"].Value;
                year1 = int.Parse(m.Groups["year1"].Value);
                term2 = m.Groups["term2"].Value;
                year2 = int.Parse(m.Groups["year2"].Value);
                return true;
            }

            m = addRx.Match(msg);
            if (m.Success)
            {
                action = "add";
                courseCode = m.Groups["code"].Value;
                term1 = m.Groups["term"].Value;
                year1 = int.Parse(m.Groups["year"].Value);
                return true;
            }

            m = remRx.Match(msg);
            if (m.Success)
            {
                action = "remove";
                courseCode = m.Groups["code"].Value;
                term1 = m.Groups["term"].Value;
                year1 = int.Parse(m.Groups["year"].Value);
                return true;
            }

            return false;
        }

        private async Task<string> GetAdvisorResponseAsync(string userMessage)
        {
            var sid = HttpContext.Session.GetInt32("StudentID");

            if (sid.HasValue)
            {
                if (TryParsePlannerCommand(userMessage,
                    out var action,
                    out var code,
                    out var term1,
                    out var year1,
                    out var term2,
                    out var year2))
                {
                    if (action == "add" || action == "move")
                    {
                        var termToCheck = action == "move" ? term2 : term1;
                        var termValidationQuery = @"
                            SELECT TypicalTermsOffered
                            FROM Courses
                            WHERE CourseCode = @code AND IsActive = 1 LIMIT 1;";

                        var termResult = _dbHelper.ExecuteQuery(termValidationQuery, new[]
                        {
                            new MySqlParameter("@code", MySqlDbType.VarChar) { Value = code.Trim().ToUpper() }
                        }, out _);

                        if (termResult != null && termResult.Rows.Count > 0)
                        {
                            var termsOffered = termResult.Rows[0]["TypicalTermsOffered"]?.ToString() ?? "";
                            if (!string.IsNullOrWhiteSpace(termsOffered))
                            {
                                var offered = termsOffered.Split(',').Select(t => t.Trim()).ToList();
                                bool valid = offered.Any(t => t.Equals(termToCheck, StringComparison.OrdinalIgnoreCase));
                                if (!valid)
                                    return $"❌ {code.Trim().ToUpper()} is only offered in {termsOffered} and cannot be placed in a {termToCheck} semester.";
                            }
                        }
                    }

                    PlannerCommandResult result = action switch
                    {
                        "add" => _plannerCommands.AddPlannedCourse(sid.Value, code, term1, year1),
                        "remove" => _plannerCommands.RemovePlannedCourse(sid.Value, code, term1, year1),
                        "move" => _plannerCommands.MovePlannedCourse(sid.Value, code, term1, year1, term2, year2),
                        _ => new PlannerCommandResult { Success = false, Message = "❌ Unknown planner action." }
                    };

                    if (result.Success)
                        return result.Message + "\n\n(Refresh the Planner page to see the update.)";

                    return result.Message;
                }
            }

            var studentContext = HttpContext.Session.GetString(STUDENT_CONTEXT_KEY) ?? "(No student DB context loaded.)";

            bool wantsProfile =
                Regex.IsMatch(userMessage, @"\b(who am i|my profile|my gpa|my credits|my info|what is my gpa|how many credits do i have)\b", RegexOptions.IgnoreCase);

            if (wantsProfile)
            {
                return ShortSnapshot(studentContext);
            }

            bool asksGraduation =
                Regex.IsMatch(userMessage, @"\b(graduate|graduation|can i graduate|am i on track|finish my degree)\b", RegexOptions.IgnoreCase);

            bool asksPlanning =
                Regex.IsMatch(userMessage, @"\b(next semester|what should i take|recommend classes|recommend courses|plan my classes|course plan|what classes should i take)\b", RegexOptions.IgnoreCase);

            DegreePlanParseResult plan = new();
            List<PdfPageText> pdfPages = new();
            string catalogName = HttpContext.Session.GetString(PDF_FILENAME_KEY) ?? "";
            string bulletinYear = HttpContext.Session.GetString(BULLETIN_YEAR_KEY) ?? "";

            var catalogJson = HttpContext.Session.GetString(CATALOG_JSON_KEY);
            var pagesJson = HttpContext.Session.GetString(PDF_PAGES_JSON_KEY);

            if (!string.IsNullOrWhiteSpace(catalogJson))
            {
                try { plan = JsonSerializer.Deserialize<DegreePlanParseResult>(catalogJson) ?? new(); } catch { }
            }

            if (!string.IsNullOrWhiteSpace(pagesJson))
            {
                try { pdfPages = JsonSerializer.Deserialize<List<PdfPageText>>(pagesJson) ?? new(); } catch { }
            }

            if (plan.TotalCount == 0 && sid.HasValue)
            {
                var degreeCode = ExtractDegreeCode(studentContext);
                if (!string.IsNullOrWhiteSpace(degreeCode))
                {
                    var alt = await TryLoadBulletinForMajorAsync(degreeCode);
                    pdfPages = alt.Pages;
                    catalogName = alt.FileName;
                    bulletinYear = alt.BulletinYear;
                    if (pdfPages.Count > 0)
                        plan = _catalogService.ParseDegreePlanFromPdfPages(pdfPages);
                }
            }

            string courseCodeFromQuestion = ExtractCourseCode(userMessage);
            var supportingDocs = await _docsRagService.SearchSupportingDocsAsync(
                userMessage,
                courseCodeFromQuestion,
                bulletinYear,
                maxDocuments: 4);

            var ragHits = pdfPages.Count > 0
                ? _ragService.FindTopRelevantSnippets(pdfPages, userMessage, topK: 5, snippetMaxChars: 800)
                : new List<RagHit>();

            if (asksGraduation && sid.HasValue)
            {
                var prompt = BuildGraduationCheckPrompt(userMessage, studentContext, sid.Value);
                prompt = await AddConversationMemoryAsync(prompt, userMessage);
                return await _gemini.GenerateWithHistoryAsync(Messages, prompt);
            }

            if (asksPlanning)
            {
                var prompt = BuildPlanningPromptWithRAG(userMessage, studentContext, plan, ragHits, supportingDocs);
                prompt = await AddConversationMemoryAsync(prompt, userMessage);
                return await _gemini.GenerateWithHistoryAsync(Messages, prompt);
            }

            var generalPrompt = BuildGeneralPromptWithRAG(
                userMessage,
                studentContext,
                catalogName,
                plan,
                ragHits,
                supportingDocs);

            generalPrompt = await AddConversationMemoryAsync(generalPrompt, userMessage);
            return await _gemini.GenerateWithHistoryAsync(Messages, generalPrompt);
        }

        private void LogChatUsage()
        {
            try
            {
                var sid = HttpContext.Session.GetInt32("StudentID");
                if (!sid.HasValue) return;

                var query = @"
                    INSERT INTO ChatUsageLogs (StudentID, ChatId, MessageDate, MessageType)
                    VALUES (@studentId, @chatId, NOW(), 'user')";

                _dbHelper.ExecuteNonQuery(query, new[]
                {
                    new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = sid.Value },
                    new MySqlParameter("@chatId", MySqlDbType.VarChar) { Value = ChatId }
                }, out _);
            }
            catch
            {
                // ignore if table does not exist
            }
        }

        private static string ShortSnapshot(string studentContext)
        {
            if (string.IsNullOrWhiteSpace(studentContext))
                return "(No student context available.)";

            var wantedPrefixes = new[]
            {
                "Name:",
                "StudentID:",
                "Major:",
                "Enrollment Year:",
                "Enrollment Status:",
                "Current GPA:",
                "Credits Earned:",
                "Degree Code:",
                "Current Semester:",
                "Next Semester:"
            };

            var lines = studentContext
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => wantedPrefixes.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return lines.Count == 0 ? studentContext : string.Join('\n', lines);
        }

        private static string ExtractSection(string text, string startMarker, string endMarker)
        {
            var startIndex = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (startIndex < 0) return "";

            startIndex += startMarker.Length;
            var endIndex = text.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
            if (endIndex < 0) endIndex = text.Length;

            return text.Substring(startIndex, endIndex - startIndex);
        }

        private string BuildGraduationCheckPrompt(string userQuestion, string studentContext, int studentId)
        {
            var sb = new StringBuilder();

            sb.AppendLine(@"
                You are an AI Academic Advisor performing a graduation eligibility check.

               STRICT RULES:
                - Give a clear YES or NO on whether the student can graduate by their target date.
                - List every remaining required course by name and code.
                - List remaining credits needed.
                - If they have account holds, warn them holds must be cleared before graduation.
                - Be precise and use only the data provided below.
                - Keep it concise and well formatted.
                - If a DEGREE WORKS TRANSCRIPT section is present in the student context, use it as the authoritative source for completed courses and transfer credits.

                Output format:
                ## Graduation Check
                ## Remaining Required Courses
                ## Credits Summary
                ## Verdict
                ## Notes
                ".Trim());

            sb.AppendLine();
            sb.AppendLine("Student Snapshot:");
            sb.AppendLine(ShortSnapshot(studentContext));
            sb.AppendLine();

            var transcriptStartGrad = studentContext.IndexOf("=== DEGREE WORKS TRANSCRIPT", StringComparison.Ordinal);
            if (transcriptStartGrad >= 0)
            {
                sb.AppendLine("Degree Works Transcript (authoritative source for courses, transfers, AP credits):");
                sb.AppendLine(studentContext.Substring(transcriptStartGrad));
                sb.AppendLine();
            }

            if (studentContext.Contains("Completed and In-Progress Courses:"))

                if (studentContext.Contains("Completed and In-Progress Courses:"))
            {
                var coursesSection = ExtractSection(studentContext, "Completed and In-Progress Courses:", "Core 39");
                if (!string.IsNullOrEmpty(coursesSection))
                {
                    sb.AppendLine("Completed/In-Progress Courses:");
                    sb.AppendLine(coursesSection.Trim());
                    sb.AppendLine();
                }
            }

            if (studentContext.Contains("Current Degree Plan"))
            {
                var planSection = ExtractSection(studentContext, "Current Degree Plan (PlannedCourses):", "=== END");
                if (!string.IsNullOrEmpty(planSection))
                {
                    sb.AppendLine("Currently Planned Courses:");
                    sb.AppendLine(planSection.Trim());
                    sb.AppendLine();
                }
            }

            if (studentContext.Contains("Core 39 General Education Progress:"))
            {
                var core39Section = ExtractSection(studentContext, "Core 39 General Education Progress:", "GPA Calculation");
                if (!string.IsNullOrEmpty(core39Section))
                {
                    sb.AppendLine("Core 39 Progress:");
                    sb.AppendLine(core39Section.Trim());
                    sb.AppendLine();
                }
            }

            var remaining = GetRemainingRequiredCourses(studentId);
            sb.AppendLine("Remaining Required Courses (not yet completed or in progress):");
            if (remaining.Count == 0)
            {
                sb.AppendLine("- NONE — all required courses completed.");
            }
            else
            {
                foreach (var (code, name, credits, category) in remaining)
                    sb.AppendLine($"- {code}: {name} ({credits} cr) [{category}]");
            }

            sb.AppendLine();

            var creditsLine = studentContext.Split('\n')
                .FirstOrDefault(l => l.Contains("Credits Earned:", StringComparison.OrdinalIgnoreCase));
            if (creditsLine != null)
                sb.AppendLine(creditsLine.Trim());

            sb.AppendLine();
            sb.AppendLine("Student Question:");
            sb.AppendLine(userQuestion);

            return sb.ToString();
        }

        private string BuildPlanningPromptWithRAG(
            string userQuestion,
            string studentContext,
            DegreePlanParseResult plan,
            List<RagHit> ragHits,
            List<DocumentSearchResult> supportingDocs)
        {
            var completedCodes = ExtractCompletedCourseCodes(studentContext);
            var recommended = _catalogService.RecommendNextCourses(plan, completedCodes, 8);

            var sb = new StringBuilder();

            sb.AppendLine(@"
                You are an AI Academic Advisor helping a student decide what courses to take next.

                STRICT RULES:
                - Use the student snapshot and completed/in-progress courses below.
                - Use only course codes and names that appear in the provided bulletin/course data.
                - Do not invent courses.
                - Prefer required courses before electives when reasonable.
                - Mention prerequisite issues if shown.
                - Keep the answer practical and concise.
                - If the student has account holds, remind them holds may affect registration.
                - ONLY recommend courses relevant to the student's major or Core 39 requirements. Do NOT recommend or invent minor requirements unless a minor is explicitly declared in the student's profile.
                - If a DEGREE WORKS TRANSCRIPT section is present in the student context, use it as the authoritative source for completed courses and transfer credits when recommending next courses.

                Recommended format:
                ## Suggested Courses
                ## Why These Courses
                ## Notes
                ".Trim());

            sb.AppendLine();
            sb.AppendLine("Student Snapshot:");
            sb.AppendLine(ShortSnapshot(studentContext));
            sb.AppendLine();

            var transcriptStartPlan = studentContext.IndexOf("=== DEGREE WORKS TRANSCRIPT", StringComparison.Ordinal);
            if (transcriptStartPlan >= 0)
            {
                sb.AppendLine("Degree Works Transcript (authoritative source for courses, transfers, AP credits):");
                sb.AppendLine(studentContext.Substring(transcriptStartPlan));
                sb.AppendLine();
            }

            if (studentContext.Contains("Completed and In-Progress Courses:"))
            {
                var coursesSection = ExtractSection(studentContext, "Completed and In-Progress Courses:", "Core 39");
                if (!string.IsNullOrEmpty(coursesSection))
                {
                    sb.AppendLine("Completed/In-Progress Courses:");
                    sb.AppendLine(coursesSection.Trim());
                    sb.AppendLine();
                }
            }

            if (studentContext.Contains("Current Degree Plan"))
            {
                var planSection = ExtractSection(studentContext, "Current Degree Plan (PlannedCourses):", "=== END");
                if (!string.IsNullOrEmpty(planSection))
                {
                    sb.AppendLine("Current Planned Courses:");
                    sb.AppendLine(planSection.Trim());
                    sb.AppendLine();
                }
            }

            if (recommended.Count > 0)

                if (recommended.Count > 0)
            {
                sb.AppendLine("Recommended Next Courses From Bulletin:");
                foreach (var c in recommended)
                {
                    var cr = string.IsNullOrWhiteSpace(c.CreditsText) ? "" : $" ({c.CreditsText} cr)";
                    string prereqInfo = "";

                    var sid = HttpContext.Session.GetInt32("StudentID");
                    if (sid.HasValue)
                    {
                        var prereqCheck = _prereqService.CheckPrerequisites(sid.Value, c.Code);
                        if (prereqCheck.MissingPrerequisites.Count > 0)
                        {
                            prereqInfo = $" | Missing prerequisites: {string.Join(", ", prereqCheck.MissingPrerequisites.Select(p => p.Split('-')[0].Trim()))}";
                        }
                        else
                        {
                            var prereqs = _prereqService.GetPrerequisitesDisplay(c.Code);
                            if (!string.Equals(prereqs, "None", StringComparison.OrdinalIgnoreCase))
                                prereqInfo = $" | Prerequisites met: {prereqs}";
                        }
                    }

                    sb.AppendLine($"- {c.Code} — {c.Title}{cr}{prereqInfo}");
                }
                sb.AppendLine();
            }

            if (ragHits.Count > 0)
            {
                sb.AppendLine("Relevant Bulletin Content:");
                foreach (var hit in ragHits)
                {
                    sb.AppendLine($"[Page {hit.Page}] {hit.Snippet}");
                    sb.AppendLine();
                }
            }

            if (supportingDocs.Count > 0)
            {
                sb.AppendLine("Relevant Supporting Documents:");
                foreach (var doc in supportingDocs)
                {
                    sb.AppendLine($"Document: {doc.DocumentName} ({doc.DocumentType}, {doc.DocumentYear})");
                    foreach (var hit in doc.RelevantHits.Take(2))
                        sb.AppendLine($"- [Page {hit.Page}] {hit.Snippet}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("Student Question:");
            sb.AppendLine(userQuestion);

            return sb.ToString();
        }

        private static string BuildGeneralPromptWithRAG(
            string userQuestion,
            string studentContext,
            string catalogName,
            DegreePlanParseResult plan,
            List<RagHit> ragHits,
            List<DocumentSearchResult> supportingDocs)
        {
            var snap = ShortSnapshot(studentContext);

            var sample = plan.Required
                .OrderBy(c => c.Number)
                .Take(15)
                .Concat(plan.Electives.OrderBy(c => c.Code).Take(10))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine(@"
                You are an AI Academic Advisor.

                STRICT RULES:
                - Use the short snapshot for identity and academic status.
                - Use only the course list and bulletin/supporting-document content provided below for course names and codes.
                - Do not invent course codes or requirements.
                - Keep the answer clear, short, and directly relevant.
                - When possible, prioritize the bulletin content over general assumptions.
                - ONLY reference courses relevant to the student's major or Core 39 requirements. Do NOT recommend or invent minor requirements unless a minor is explicitly declared in the student's profile.
                - If a DEGREE WORKS TRANSCRIPT section is present in the student context, treat it as the authoritative source for completed courses, transfer credits, AP credits, and in-progress courses. It overrides any empty course history from the database.
                - Never say there are no transfer credits if the Degree Works transcript lists transfer or AP credits.
                ".Trim());

            sb.AppendLine();
            sb.AppendLine("Student Snapshot:");
            sb.AppendLine(snap);
            sb.AppendLine();

            var transcriptStartGen = studentContext.IndexOf("=== DEGREE WORKS TRANSCRIPT", StringComparison.Ordinal);
            if (transcriptStartGen >= 0)
            {
                sb.AppendLine("Degree Works Transcript (authoritative source for courses, transfers, AP credits):");
                sb.AppendLine(studentContext.Substring(transcriptStartGen));
                sb.AppendLine();
            }

            if (studentContext.Contains("Core 39 General Education Progress:"))
            {
                var core39Section = ExtractSection(studentContext, "Core 39 General Education Progress:", "GPA Calculation");
                if (!string.IsNullOrEmpty(core39Section))
                {
                    sb.AppendLine("Core 39 Requirements Status:");
                    sb.AppendLine(core39Section.Trim());
                    sb.AppendLine();
                }
            }

            if (studentContext.Contains("Completed and In-Progress Courses:"))
            {
                var coursesSection = ExtractSection(studentContext, "Completed and In-Progress Courses:", "Core 39");
                if (!string.IsNullOrEmpty(coursesSection))
                {
                    sb.AppendLine("Completed/In-Progress Courses:");
                    sb.AppendLine(coursesSection.Trim());
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrWhiteSpace(catalogName))
            {
                sb.AppendLine($"Catalog from PDF: {catalogName}");
                sb.AppendLine();
            }

            if (sample.Count > 0)
            {
                sb.AppendLine("Known Courses From Bulletin:");
                foreach (var c in sample)
                {
                    var cr = string.IsNullOrWhiteSpace(c.CreditsText) ? "" : $" ({c.CreditsText} cr)";
                    sb.AppendLine($"- {c.Code} — {c.Title}{cr}");
                }
                sb.AppendLine();
            }

            if (ragHits.Count > 0)
            {
                sb.AppendLine("Relevant Bulletin Content:");
                foreach (var hit in ragHits)
                {
                    sb.AppendLine($"[Page {hit.Page}] {hit.Snippet}");
                    sb.AppendLine();
                }
            }

            if (supportingDocs.Count > 0)
            {
                sb.AppendLine("Relevant Supporting Documents:");
                foreach (var doc in supportingDocs)
                {
                    sb.AppendLine($"Document: {doc.DocumentName} ({doc.DocumentType}, {doc.DocumentYear})");
                    foreach (var hit in doc.RelevantHits.Take(2))
                        sb.AppendLine($"- [Page {hit.Page}] {hit.Snippet}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("Student Question:");
            sb.AppendLine(userQuestion);

            return sb.ToString();
        }

        private List<(string code, string name, int credits, string category)> GetRemainingRequiredCourses(int studentId)
        {
            var list = new List<(string code, string name, int credits, string category)>();

            var sql = @"
                SELECT
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    dr.RequirementCategory
                FROM Students s
                JOIN DegreePrograms dp ON s.Major = dp.DegreeName
                JOIN DegreeRequirements dr ON dr.DegreeID = dp.DegreeID
                JOIN Courses c ON dr.CourseID = c.CourseID
                WHERE s.StudentID = @studentId
                  AND c.CourseID NOT IN (
                      SELECT sch.CourseID
                      FROM StudentCourseHistory sch
                      WHERE sch.StudentID = @studentId
                        AND sch.Status IN ('Completed', 'In Progress')
                  )
                ORDER BY dr.RequirementCategory, c.CourseCode;";

            var dt = _dbHelper.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (dt == null) return list;

            foreach (DataRow row in dt.Rows)
            {
                list.Add((
                    row["CourseCode"]?.ToString() ?? "",
                    row["CourseName"]?.ToString() ?? "",
                    row["CreditHours"] != DBNull.Value ? Convert.ToInt32(row["CreditHours"]) : 0,
                    row["RequirementCategory"]?.ToString() ?? "Required"
                ));
            }

            return list;
        }

        private static HashSet<string> ExtractCompletedCourseCodes(string studentContext)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(studentContext ?? "", @"\b[A-Z]{2,5}\s\d{3}\b"))
                set.Add(m.Value.Trim());
            return set;
        }

        private static string ExtractCourseCode(string userMessage)
        {
            var m = Regex.Match(userMessage ?? "", @"\b[A-Z]{2,5}\s?\d{3}\b", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            var code = m.Value.Trim().ToUpperInvariant();
            code = Regex.Replace(code, @"^([A-Z]{2,5})(\d{3})$", "$1 $2");
            return code;
        }

        private static string ExtractDegreeCode(string studentContext)
        {
            var line = (studentContext ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith("Degree Code:", StringComparison.OrdinalIgnoreCase));

            if (line == null) return "";
            return line.Replace("Degree Code:", "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        private string? LoadTranscriptFromDb(int studentId)
        {
            try
            {
                var sql = @"
                    SELECT ParsedContextText
                    FROM StudentTranscripts
                    WHERE StudentID = @sid AND IsActive = 1
                    LIMIT 1";

                var result = _dbHelper.ExecuteQuery(sql, new[]
                {
                    new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
                }, out _);

                if (result != null && result.Rows.Count > 0 && result.Rows[0][0] != DBNull.Value)
                    return result.Rows[0][0]?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load transcript from DB for student {StudentId}", studentId);
            }

            return null;
        }
    }
}