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

        public ChatModel(
            DatabaseHelper dbHelper,
            IChatLogStore chatLogStore,
            ILogger<ChatModel> logger,
            PdfService pdfService,
            CourseCatalogService catalogService,
            GeminiService gemini)
        {
            _dbHelper = dbHelper;
            _chatLogStore = chatLogStore;
            _logger = logger;
            _pdfService = pdfService;
            _catalogService = catalogService;
            _gemini = gemini;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            EnsureChatId();

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

                    // ✅ Parse catalog from PDF and cache it
                    var plan = _catalogService.ParseDegreePlanFromPdfPages(extract.Pages);
                    HttpContext.Session.SetString(CATALOG_JSON_KEY, JsonSerializer.Serialize(plan));

                    Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content =
                            $"✅ Loaded PDF: {UploadedPdf.FileName}. Extracted {extract.Pages.Count} page(s), {extract.TotalChars:N0} chars. " +
                            $"Parsed {plan.TotalCount} course item(s) (Required: {plan.Required.Count}, Electives: {plan.Electives.Count}).",
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

            // ===== Send message =====
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
                    Content = "I’m having trouble processing that right now. Please try again.",
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

            return RedirectToPage();
        }

        public IActionResult OnPostRemovePdf()
        {
            HttpContext.Session.Remove(PDF_FILENAME_KEY);
            HttpContext.Session.Remove(PDF_PAGES_JSON_KEY);
            HttpContext.Session.Remove(CATALOG_JSON_KEY);
            return RedirectToPage();
        }

        private void EnsureChatId()
        {
            ChatId = HttpContext.Session.GetString("ChatId") ?? Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString("ChatId", ChatId);
        }

        // =========================
        // DB CONTEXT
        // =========================
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

            var completedSql = @"
SELECT 
    c.CourseCode,
    c.CourseName,
    c.CreditHours,
    sch.Grade,
    sch.Term,
    sch.AcademicYear
FROM StudentCourseHistory sch
JOIN Courses c ON sch.CourseID = c.CourseID
WHERE sch.StudentID = @studentId
  AND sch.Status = 'Completed'
ORDER BY sch.AcademicYear DESC, sch.Term DESC;
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
            sb.AppendLine($"Enrollment Status: {status}");
            sb.AppendLine($"Current GPA: {gpa}");
            sb.AppendLine($"Credits Earned: {earned} / {required}");
            sb.AppendLine($"Degree Code: {degreeCode}");
            sb.AppendLine();
            sb.AppendLine("Completed Courses (from StudentCourseHistory):");
            if (completed != null && completed.Rows.Count > 0)
            {
                foreach (DataRow r in completed.Rows)
                {
                    sb.AppendLine($"- {r["CourseCode"]}: {r["CourseName"]} ({r["CreditHours"]} hrs) Grade {r["Grade"]} — {r["Term"]} {r["AcademicYear"]}");
                }
            }
            else sb.AppendLine("- (none found)");
            sb.AppendLine("=== END STUDENT DB CONTEXT ===");

            return Task.FromResult(sb.ToString());
        }

        // =========================
        // MAIN RESPONSE
        // =========================
        private async Task<string> GetAdvisorResponseAsync(string userMessage)
        {
            var studentContext = HttpContext.Session.GetString(STUDENT_CONTEXT_KEY) ?? "(No student DB context loaded.)";

            bool wantsProfile =
                Regex.IsMatch(userMessage, @"\b(who am i|show (my )?profile|my info)\b", RegexOptions.IgnoreCase);

            if (wantsProfile)
                return BuildProfileAnswer(studentContext);

            var plan = LoadPlanFromSession();
            if (plan.TotalCount == 0)
            {
                return "I didn’t find any course listings parsed from the PDF yet. Please upload the bulletin PDF again (must be text-based, not scanned).";
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
                    return "I parsed the PDF, but I couldn’t find any remaining required/elective courses to recommend.";
                }

                var prompt = BuildPlanningPrompt(userMessage, studentContext, PdfFileName ?? "Uploaded PDF", rec);
                return await _gemini.GenerateAsync(prompt);
            }

            // General
            var generalPrompt = BuildGeneralPrompt(userMessage, studentContext, PdfFileName ?? "Uploaded PDF", plan);
            return await _gemini.GenerateAsync(generalPrompt);
        }

        private static string BuildProfileAnswer(string studentContext)
        {
            return "## Answer\nHere’s your profile from the database.\n\n" + studentContext;
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

        private static HashSet<string> ExtractCompletedCourseCodes(string studentContext)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in studentContext.Split('\n'))
            {
                // matches "- CS 215:" or "- CS215:"
                var m = Regex.Match(line, @"-\s+([A-Z]{2,4})\s*(\d{3})\s*:", RegexOptions.IgnoreCase);
                if (m.Success)
                    set.Add($"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}");
            }
            return set;
        }

        private static string BuildPlanningPrompt(
            string userQuestion,
            string studentContext,
            string catalogName,
            List<CatalogCourse> recommended)
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
            sb.AppendLine("Student Snapshot (short):");
            sb.AppendLine(snap);
            sb.AppendLine();

            sb.AppendLine($"Source PDF: {catalogName}");
            sb.AppendLine();

            sb.AppendLine("PROVIDED RECOMMENDED COURSES (use ONLY these):");
            foreach (var c in recommended.Take(6))
            {
                var cr = !string.IsNullOrWhiteSpace(c.CreditsText) ? $" | Credits: {c.CreditsText}" : "";
                sb.AppendLine($"- {c.Code} — {c.Title}{cr}");
            }

            sb.AppendLine();
            sb.AppendLine("Student Question:");
            sb.AppendLine(userQuestion);

            return sb.ToString();
        }

        private static string BuildGeneralPrompt(
            string userQuestion,
            string studentContext,
            string catalogName,
            DegreePlanParseResult plan)
        {
            var snap = ShortSnapshot(studentContext);

            // Small slice so prompt isn't huge
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
- Use ONLY the course list provided below for codes/names.
- Do NOT invent course codes/names.
- Keep it short and do NOT repeat the full DB context.
".Trim());

            sb.AppendLine();
            sb.AppendLine("Student Snapshot (short):");
            sb.AppendLine(snap);
            sb.AppendLine();

            sb.AppendLine($"Catalog from PDF: {catalogName} (subset)");
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
    }
}