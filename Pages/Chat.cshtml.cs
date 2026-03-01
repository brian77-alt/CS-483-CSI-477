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

        public List<ChatMessage> Messages { get; set; } = new();
        public string ChatId { get; set; } = "";

        public string? ErrorMessage { get; set; }
        public string? PdfFileName { get; set; }
        public string? LoadedStudentSummary { get; set; }

        [BindProperty] public string UserMessage { get; set; } = "";
        [BindProperty] public IFormFile? UploadedPdf { get; set; }

        private const string PDF_FILENAME_KEY = "PdfFileName";
        private const string PDF_PAGES_JSON_KEY = "PdfPagesJson";
        private const string STUDENT_CONTEXT_KEY = "StudentContextText";
        private const string CATALOG_JSON_KEY = "ParsedCatalogJson";
        private const string BULLETIN_YEAR_KEY = "BulletinYear";
        private readonly PrerequisiteService _prereqService;

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
            PrerequisiteService prereqService)
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
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            EnsureChatId();

            // Auto load bulletin from Azure if not already loaded
            await TryAutoLoadBulletinAsync();  // ADD THIS LINE

            PdfFileName = HttpContext.Session.GetString(PDF_FILENAME_KEY);
            await EnsureStudentContextLoadedAsync();

            Messages = await _chatLogStore.LoadAsync(ChatId);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            EnsureChatId();

            PdfFileName = HttpContext.Session.GetString(PDF_FILENAME_KEY);
            await EnsureStudentContextLoadedAsync();
            Messages = await _chatLogStore.LoadAsync(ChatId);

            // ===== PDF upload =====
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

                    // Extract bulletin year from filename
                    var bulletinYear = ExtractBulletinYear(UploadedPdf.FileName);
                    HttpContext.Session.SetString(BULLETIN_YEAR_KEY, bulletinYear);

                    // Parse catalog from PDF and cache it
                    var plan = _catalogService.ParseDegreePlanFromPdfPages(extract.Pages);
                    HttpContext.Session.SetString(CATALOG_JSON_KEY, JsonSerializer.Serialize(plan));

                    // Check for year mismatch warning
                    var yearWarning = CheckBulletinYearMismatch(bulletinYear);

                    Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content =
                            $"✅ Loaded PDF: {UploadedPdf.FileName} (Bulletin Year: {bulletinYear}). " +
                            $"Extracted {extract.Pages.Count} page(s), {extract.TotalChars:N0} chars. " +
                            $"Parsed {plan.TotalCount} course item(s) (Required: {plan.Required.Count}, Electives: {plan.Electives.Count})." +
                            (string.IsNullOrEmpty(yearWarning) ? "" : $"\n\n⚠️ {yearWarning}"),
                        Timestamp = DateTime.Now
                    });

                    await _chatLogStore.SaveAsync(ChatId, Messages);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PDF processing failed");
                    ErrorMessage = "Could not read the PDF. If it is scanned (image-only), OCR is required.";
                    return Page();
                }
            }

            // ----- Send Message -----
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
            }

            return Page();
        }

        public async Task<IActionResult> OnPostClearAsync()
        {
            EnsureChatId();
            await _chatLogStore.ClearAsync(ChatId);

            HttpContext.Session.Remove("ChatId");
            HttpContext.Session.Remove(PDF_FILENAME_KEY);
            HttpContext.Session.Remove(PDF_PAGES_JSON_KEY);
            HttpContext.Session.Remove(CATALOG_JSON_KEY);
            HttpContext.Session.Remove(STUDENT_CONTEXT_KEY);
            HttpContext.Session.Remove(BULLETIN_YEAR_KEY);

            return RedirectToPage();
        }

        public IActionResult OnPostRemovePdf()
        {
            HttpContext.Session.Remove(PDF_FILENAME_KEY);
            HttpContext.Session.Remove(PDF_PAGES_JSON_KEY);
            HttpContext.Session.Remove(CATALOG_JSON_KEY);
            HttpContext.Session.Remove(BULLETIN_YEAR_KEY);
            return RedirectToPage();
        }

        private void EnsureChatId()
        {
            ChatId = HttpContext.Session.GetString("ChatId") ?? Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString("ChatId", ChatId);
        }

        // Extract bulletin year from filename (e.g., "CS_Bulletin_2024-2025.pdf" → "2024-2025")
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

        // Check if student is using the correct bulletin year
        private string CheckBulletinYearMismatch(string bulletinYear)
        {
            var sid = HttpContext.Session.GetInt32("StudentID");
            if (!sid.HasValue || bulletinYear == "Unknown") return "";

            // Get student's entry year from database
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

            // Warn if using a bulletin from before they enrolled
            if (bulletinStartYear < entryYear)
            {
                return $"WARNING: You enrolled in {entryYear}, but this bulletin is from {bulletinYear}. " +
                       $"You should follow the bulletin from your entry year ({entryYear}-{entryYear + 1}) unless advised otherwise.";
            }

            // Warn if using a bulletin from after they enrolled
            if (bulletinStartYear > entryYear + 1)
            {
                return $"NOTE: This bulletin ({bulletinYear}) is newer than your entry year ({entryYear}). " +
                       $"Consult your advisor before following newer requirements.";
            }

            return "";
        }

        // AUTO LOAD BULLETIN FROM AZURE
        // ------------------------------
        private async Task<bool> TryAutoLoadBulletinAsync()
        {
            // Check if bulletin already loaded
            if (!string.IsNullOrEmpty(HttpContext.Session.GetString(PDF_FILENAME_KEY)))
                return true;

            var sid = HttpContext.Session.GetInt32("StudentID");
            if (!sid.HasValue) return false;

            try
            {
                // Get student's major and enrollment year
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

                var entryYear = Convert.ToInt32(enrollmentYear);
                var bulletinYearNeeded = $"{entryYear}-{entryYear + 1}";

                // Map degree codes to full names in filenames
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
                    AND BulletinYear = @bulletinYear
                    AND FileName LIKE @majorKeyword
                    ORDER BY UploadDate DESC
                    LIMIT 1";

                var bulletin = _dbHelper.ExecuteQuery(bulletinQuery, new[]
                {
                    new MySqlParameter("@bulletinYear", MySqlDbType.VarChar) { Value = bulletinYearNeeded },
                    new MySqlParameter("@majorKeyword", MySqlDbType.VarChar) { Value = $"%{majorKeyword}%" }
                }, out var berr);

                if (!string.IsNullOrEmpty(berr) || bulletin == null || bulletin.Rows.Count == 0)
                {
                    _logger.LogWarning($"No bulletin found for {degreeCode}, year {bulletinYearNeeded}");
                    return false;
                }

                var bulletinRow = bulletin.Rows[0];
                var fileName = bulletinRow["FileName"].ToString() ?? "";
                var filePath = bulletinRow["FilePath"].ToString() ?? "";
                var bulletinYear = bulletinRow["BulletinYear"].ToString() ?? "";

                // Download from Azure/local and extract
                byte[] pdfBytes = await DownloadPdfFromPathAsync(filePath);

                if (pdfBytes == null || pdfBytes.Length == 0)
                    return false;

                var extract = _pdfService.Extract(pdfBytes, fileName, maxPages: 25, maxCharsTotal: 200_000);

                // Cache in session
                HttpContext.Session.SetString(PDF_FILENAME_KEY, fileName);
                HttpContext.Session.SetString(PDF_PAGES_JSON_KEY, JsonSerializer.Serialize(extract.Pages));
                HttpContext.Session.SetString(BULLETIN_YEAR_KEY, bulletinYear);

                var plan = _catalogService.ParseDegreePlanFromPdfPages(extract.Pages);
                HttpContext.Session.SetString(CATALOG_JSON_KEY, JsonSerializer.Serialize(plan));

                PdfFileName = fileName;

                _logger.LogInformation($"Auto-loaded bulletin: {fileName} ({bulletinYear})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-load bulletin");
                return false;
            }
        }

        private async Task<byte[]?> DownloadPdfFromPathAsync(string filePath)
        {
            try
            {
                // If it's a local path (starts with /uploads), read from filesystem
                if (filePath.StartsWith("/uploads"))
                {
                    var localPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath.TrimStart('/'));
                    if (System.IO.File.Exists(localPath))
                        return await System.IO.File.ReadAllBytesAsync(localPath);
                    return null;
                }

                // Download from Azure Blob Storage with authentication
                var azureConnStr = _configuration["AzureBlobStorage:ConnectionString"];
                if (string.IsNullOrEmpty(azureConnStr))
                {
                    _logger.LogWarning("Azure connection string not configured");
                    return null;
                }

                // Parse blob URL to get container and blob name
                var uri = new Uri(filePath);
                var pathParts = uri.AbsolutePath.TrimStart('/').Split('/', 2);

                if (pathParts.Length < 2)
                {
                    _logger.LogError($"Invalid blob path: {filePath}");
                    return null;
                }

                var containerName = pathParts[0];
                var blobName = pathParts[1];

                // Use Azure SDK to download with authentication
                var blobServiceClient = new Azure.Storage.Blobs.BlobServiceClient(azureConnStr);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                using var ms = new MemoryStream();
                await blobClient.DownloadToAsync(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to download PDF from {filePath}");
                return null;
            }
        }

        // DB CONTEXT
        private async Task EnsureStudentContextLoadedAsync()
        {
            LoadedStudentSummary = HttpContext.Session.GetString(STUDENT_CONTEXT_KEY);
            if (!string.IsNullOrWhiteSpace(LoadedStudentSummary))
                return;

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
                LIMIT 1;
                ";
            var summary = _dbHelper.ExecuteQuery(summarySql, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) throw new Exception(err);
            if (summary == null || summary.Rows.Count == 0) throw new Exception("Student not found.");

            var row = summary.Rows[0];

            var fullName = row["FullName"]?.ToString() ?? "Student";
            var major = row["Major"]?.ToString() ?? "Undeclared";
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
                ORDER BY sch.Status DESC, sch.AcademicYear DESC, sch.Term DESC;
                ";
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
            else sb.AppendLine("- (none found)");

            // Core 39 Progress
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
                    var category = r["CategoryName"].ToString();
                    var requiredCredits = r["CreditsRequired"].ToString();
                    var earnedCredits = r["CreditsEarned"].ToString();
                    sb.AppendLine($"- {category}: {earnedCredits}/{requiredCredits} credits");
                }
            }
            else
            {
                sb.AppendLine("- (Core 39 data not available)");
            }

            sb.AppendLine("=== END STUDENT DB CONTEXT ===");

            return Task.FromResult(sb.ToString());
        }

        // ------------------------
        // MAIN RESPONSE WITH RAG
        // ------------------------
        private async Task<string> GetAdvisorResponseAsync(string userMessage)
        {
            var studentContext = HttpContext.Session.GetString(STUDENT_CONTEXT_KEY) ?? "(No student DB context loaded.)";

            bool wantsProfile =
                Regex.IsMatch(userMessage, @"\b(who am i|show (my )?profile|my info)\b", RegexOptions.IgnoreCase);

            if (wantsProfile)
                return BuildProfileAnswer(studentContext);

            var plan = LoadPlanFromSession();
            var pdfPages = LoadPdfPagesFromSession();

            if (plan.TotalCount == 0 && pdfPages.Count == 0)
            {
                return "I didn't find any course listings or bulletin content yet. Please upload the bulletin PDF (must be text-based, not scanned).";
            }

            var completedSet = ExtractCompletedCourseCodes(studentContext);

            bool looksPlanning =
                Regex.IsMatch(userMessage, @"\b(next classes|what classes|what should i take|recommend|next semester|schedule)\b",
                    RegexOptions.IgnoreCase);

            if (looksPlanning)
            {
                var rec = _catalogService.RecommendNextCourses(plan, completedSet, count: 6);

                if (rec.Count == 0)
                {
                    return "I parsed the PDF, but I couldn't find any remaining required/elective courses to recommend.";
                }

                var sid = HttpContext.Session.GetInt32("StudentID");
                var prompt = BuildPlanningPrompt(userMessage, studentContext, PdfFileName ?? "Uploaded PDF", rec, sid);
                return await _gemini.GenerateWithHistoryAsync(Messages, prompt);
            }

            // General question - use RAG to find relevant bulletin content
            var ragHits = _ragService.FindTopRelevantSnippets(pdfPages, userMessage, topK: 3, snippetMaxChars: 600);

            // Also search supporting documents
            var bulletinYear = HttpContext.Session.GetString(BULLETIN_YEAR_KEY) ?? null;
            var supportingDocs = await _docsRagService.SearchSupportingDocsAsync(
                userMessage,
                courseCode: null,
                documentYear: bulletinYear,
                maxDocuments: 3);

            // Debug: Check if Core 39 is in student context
            _logger.LogInformation("=== STUDENT CONTEXT CHECK ===");
            _logger.LogInformation($"Contains 'Core 39': {studentContext.Contains("Core 39 General Education Progress:")}");
            _logger.LogInformation($"Contains 'Completed': {studentContext.Contains("Completed and In-Progress Courses:")}");
            _logger.LogInformation("=== END CHECK ===");

            var generalPrompt = BuildGeneralPromptWithRAG(
                userMessage,
                studentContext,
                PdfFileName ?? "Uploaded PDF",
                plan,
                ragHits,
                supportingDocs);

            // Debug: Log the prompt to see what AI receives
            _logger.LogInformation("=== PROMPT SENT TO AI ===");
            _logger.LogInformation(generalPrompt);
            _logger.LogInformation("=== END PROMPT ===");

            var response = await _gemini.GenerateWithHistoryAsync(Messages, generalPrompt);

            if (ragHits.Count > 0 || supportingDocs.Count > 0)
            {
                var yearForCitation = HttpContext.Session.GetString(BULLETIN_YEAR_KEY) ?? "Unknown";
                var citations = BuildCitationsWithDocs(ragHits, PdfFileName ?? "Bulletin", yearForCitation, supportingDocs);
                response += $"\n\n{citations}";
            }

            return response;
        }

        private static string BuildProfileAnswer(string studentContext)
        {
            return "## Answer\nHere's your profile from the database.\n\n" + studentContext;
        }

        private DegreePlanParseResult LoadPlanFromSession()
        {
            var json = HttpContext.Session.GetString(CATALOG_JSON_KEY);
            if (string.IsNullOrWhiteSpace(json)) return new DegreePlanParseResult();
            try
            {
                return JsonSerializer.Deserialize<DegreePlanParseResult>(json) ?? new DegreePlanParseResult();
            }
            catch
            {
                return new DegreePlanParseResult();
            }
        }

        private List<PdfPageText> LoadPdfPagesFromSession()
        {
            var json = HttpContext.Session.GetString(PDF_PAGES_JSON_KEY);
            if (string.IsNullOrWhiteSpace(json)) return new List<PdfPageText>();
            try
            {
                return JsonSerializer.Deserialize<List<PdfPageText>>(json) ?? new List<PdfPageText>();
            }
            catch
            {
                return new List<PdfPageText>();
            }
        }

        private static HashSet<string> ExtractCompletedCourseCodes(string studentContext)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in studentContext.Split('\n'))
            {
                var m = Regex.Match(line, @"-\s+([A-Z]{2,4})\s*(\d{3})\s*:", RegexOptions.IgnoreCase);
                if (m.Success)
                    set.Add($"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}");
            }
            return set;
        }

        private static string BuildCitations(List<RagHit> hits, string pdfName, string bulletinYear)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("**Sources:**");

            foreach (var hit in hits.OrderBy(h => h.Page))
            {
                sb.AppendLine($"- Page {hit.Page} of {pdfName} ({bulletinYear})");
            }

            return sb.ToString();
        }

        private string BuildPlanningPrompt(
            string userQuestion,
            string studentContext,
            string catalogName,
            List<CatalogCourse> recommended,
            int? studentId)
        {
            var snap = ShortSnapshot(studentContext);

            var sb = new StringBuilder();
            sb.AppendLine(@"
                You are an AI Academic Advisor.

                STRICT RULES:
                - You may ONLY recommend courses listed in PROVIDED RECOMMENDED COURSES below.
                - Do NOT invent course codes, names, or credits.
                - Keep it SHORT.
                - Do NOT repeat the full Student DB Context.
                - If asked for a course number, give 3–5 course codes.

                Output format:
                ## Answer
                ## Recommended Next Courses
                ## Suggested Schedule (12–15 credits)
                ## Notes
                ".Trim());

            sb.AppendLine();
            sb.AppendLine("Student Snapshot:");
            sb.AppendLine(snap);
            sb.AppendLine();

            // Add Core 39 and completed courses context
            if (studentContext.Contains("Core 39 General Education Progress:"))
            {
                var core39Section = ExtractSection(studentContext, "Core 39 General Education Progress:", "=== END");
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

            sb.AppendLine();

            sb.AppendLine($"Source PDF: {catalogName}");
            sb.AppendLine();

            sb.AppendLine("PROVIDED RECOMMENDED COURSES (use ONLY these):");
            var sid = HttpContext.Session.GetInt32("StudentID");
            foreach (var c in recommended.Take(6))
            {
                var cr = !string.IsNullOrWhiteSpace(c.CreditsText) ? $" | Credits: {c.CreditsText}" : "";

                // Check prerequisites
                string prereqInfo = "";
                if (sid.HasValue)
                {
                    var prereqCheck = _prereqService.CheckPrerequisites(sid.Value, c.Code);
                    if (prereqCheck.MissingPrerequisites.Count > 0)
                    {
                        prereqInfo = $" | ⚠️ Missing: {string.Join(", ", prereqCheck.MissingPrerequisites.Select(p => p.Split('-')[0].Trim()))}";
                    }
                    else
                    {
                        var prereqs = _prereqService.GetPrerequisitesDisplay(c.Code);
                        if (prereqs != "None")
                        {
                            prereqInfo = $" | Prerequisites met: {prereqs}";
                        }
                    }
                }

                sb.AppendLine($"- {c.Code} — {c.Title}{cr}{prereqInfo}");
            }

            sb.AppendLine();
            sb.AppendLine("Student Question:");
            sb.AppendLine(userQuestion);

            return sb.ToString();

        }

        private static string ExtractSection(string text, string startMarker, string endMarker)
        {
            var startIndex = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                Console.WriteLine($"ExtractSection: Start marker '{startMarker}' not found");
                return "";
            }

            startIndex += startMarker.Length;
            var endIndex = text.IndexOf(endMarker, startIndex, StringComparison.Ordinal);

            if (endIndex < 0)
                endIndex = text.Length;

            var extracted = text.Substring(startIndex, endIndex - startIndex);
            Console.WriteLine($"ExtractSection: Extracted {extracted.Length} chars from '{startMarker}' to '{endMarker}'");
            return extracted;
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
                - Use the short snapshot for identity/completed courses.
                - Use ONLY the course list and bulletin content provided below for codes/names.
                - Do NOT invent course codes/names.
                - Keep it short and do NOT repeat the full DB context.
                - When answering, prioritize information from the BULLETIN CONTENT sections.
                ".Trim());

            sb.AppendLine();
            sb.AppendLine("Student Snapshot:");
            sb.AppendLine(snap);
            sb.AppendLine();

            // Add Core 39 and completed courses context
            if (studentContext.Contains("Core 39 General Education Progress:"))
            {
                var core39Section = ExtractSection(studentContext, "Core 39 General Education Progress:", "=== END");
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

            sb.AppendLine();
            sb.AppendLine($"Catalog from PDF: {catalogName}");

            // ADD THIS SECTION:
            if (supportingDocs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("SUPPORTING DOCUMENTS (syllabi, guides, policies):");
                foreach (var doc in supportingDocs)
                {
                    sb.AppendLine($"[{doc.DocumentType}] {doc.DocumentName}:");
                    foreach (var hit in doc.RelevantHits)
                    {
                        sb.AppendLine($"  Page {hit.Page}: {hit.Snippet}");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("Course List (subset):");
            foreach (var c in sample)
            {
                var cr = !string.IsNullOrWhiteSpace(c.CreditsText) ? $" ({c.CreditsText} cr)" : "";
                sb.AppendLine($"- {c.Code} — {c.Title}{cr}");
            }

            sb.AppendLine();
            sb.AppendLine("Question:");
            sb.AppendLine(userQuestion);

            return sb.ToString();
        }

        private static string ShortSnapshot(string studentContext)
        {
            string FindLine(string prefix)
            {
                foreach (var line in studentContext.Split('\n'))
                {
                    var l = line.Trim();
                    if (l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return l.Substring(prefix.Length).Trim();
                }
                return "(not available)";
            }

            var name = FindLine("Name:");
            var major = FindLine("Major:");
            var gpa = FindLine("Current GPA:");
            var credits = FindLine("Credits Earned:");

            return $"- Name: {name}\n- Major: {major}\n- GPA: {gpa}\n- Credits: {credits}";
        }

        private static string BuildCitationsWithDocs(
            List<RagHit> bulletinHits,
            string pdfName,
            string bulletinYear,
            List<DocumentSearchResult> supportingDocs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("**Sources:**");

            // Bulletin citations
            if (bulletinHits.Count > 0)
            {
                foreach (var hit in bulletinHits.OrderBy(h => h.Page))
                {
                    sb.AppendLine($"- Page {hit.Page} of {pdfName} ({bulletinYear})");
                }
            }

            // Supporting document citations
            if (supportingDocs.Count > 0)
            {
                foreach (var doc in supportingDocs)
                {
                    var pages = string.Join(", ", doc.RelevantHits.Select(h => h.Page).Distinct().OrderBy(p => p));
                    sb.AppendLine($"- {doc.DocumentType}: {doc.DocumentName} (Pages {pages})");
                }
            }

            return sb.ToString();
        }
    }
}