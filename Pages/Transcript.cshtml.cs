using AdvisorDb;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;
using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Pages
{
    public class TranscriptModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly IConfiguration _configuration;
        private readonly PdfService _pdfService;
        private readonly DegreeWorksParserService _parser;
        private readonly ILogger<TranscriptModel> _logger;

        public string? CurrentTranscriptFileName { get; set; }
        public string? CurrentTranscriptUploadDate { get; set; }
        public string? CurrentTranscriptSemester { get; set; }
        public string? ParseSummary { get; set; }
        public bool ShowApplyButton { get; set; }

        [TempData] public string? StatusMessage { get; set; }
        [TempData] public string? StatusType { get; set; }

        // Session key constants — shared with Chat
        public const string TRANSCRIPT_CONTEXT_KEY = "DegreeWorksContext";
        public const string TRANSCRIPT_FILENAME_KEY = "DegreeWorksFileName";

        public TranscriptModel(
            DatabaseHelper dbHelper,
            IConfiguration configuration,
            PdfService pdfService,
            DegreeWorksParserService parser,
            ILogger<TranscriptModel> logger)
        {
            _dbHelper = dbHelper;
            _configuration = configuration;
            _pdfService = pdfService;
            _parser = parser;
            _logger = logger;
        }

        public IActionResult OnGet()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");
            if (HttpContext.Session.GetString("Role") == "Admin")
                return RedirectToPage("/AdminDashboard");

            LoadCurrentTranscript();
            ParseSummary = HttpContext.Session.GetString(TRANSCRIPT_CONTEXT_KEY);
            ShowApplyButton = !string.IsNullOrEmpty(ParseSummary);
            return Page();
        }

        public async Task<IActionResult> OnPostUploadAsync(IFormFile transcriptFile)
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");
            if (HttpContext.Session.GetString("Role") == "Admin")
                return RedirectToPage("/AdminDashboard");

            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            if (transcriptFile == null || transcriptFile.Length == 0)
            {
                StatusMessage = "Please select a PDF file to upload.";
                StatusType = "error";
                return RedirectToPage();
            }

            var ext = Path.GetExtension(transcriptFile.FileName).ToLowerInvariant();
            if (ext != ".pdf")
            {
                StatusMessage = "Only PDF files are accepted.";
                StatusType = "error";
                return RedirectToPage();
            }

            const long maxBytes = 50L * 1024L * 1024L;
            if (transcriptFile.Length > maxBytes)
            {
                StatusMessage = "File exceeds the 50MB limit.";
                StatusType = "error";
                return RedirectToPage();
            }

            try
            {
                // Read bytes
                using var ms = new MemoryStream();
                await transcriptFile.CopyToAsync(ms);
                var fileBytes = ms.ToArray();

                // Extract text via PdfService
                var extract = _pdfService.ExtractWithLines(fileBytes, transcriptFile.FileName, maxPages: 30, maxCharsTotal: 300_000);
                var fullText = string.Join("\n", extract.Pages.Select(p => p.Text));

                // Parse the Degree Works content
                var transcript = _parser.Parse(fullText);

                // Determine current semester for labeling
                var now = DateTime.Now;
                var currentMonth = now.Month;
                string semester = currentMonth >= 8 ? "Fall" :
                                     currentMonth >= 5 ? "Summer" : "Spring";
                int academicYear = now.Year;

                // Upload to Azure blob storage — named by studentId so it overwrites
                string fileUrl = await UploadToAzureAsync(fileBytes, studentId, transcriptFile.FileName);

                // Save/update DB record
                SaveTranscriptRecord(studentId, transcriptFile.FileName, fileUrl,
                    transcriptFile.Length, semester, academicYear, transcript.RawSummary);

                // Store parsed context in session for AI
                HttpContext.Session.SetString(TRANSCRIPT_CONTEXT_KEY, transcript.RawSummary);
                HttpContext.Session.SetString(TRANSCRIPT_FILENAME_KEY, transcriptFile.FileName);

                StatusMessage = $"Transcript uploaded and parsed successfully. Found {transcript.AllCourses.Count} courses, {transcript.TransferCourses.Count} transfer/AP credits.";
                StatusType = "success";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transcript upload failed for student {StudentId}", studentId);
                StatusMessage = $"Upload failed: {ex.Message}";
                StatusType = "error";
            }

            return RedirectToPage();
        }

        public IActionResult OnPostApplyToRecord()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            var context = HttpContext.Session.GetString(TRANSCRIPT_CONTEXT_KEY);
            if (string.IsNullOrEmpty(context))
            {
                StatusMessage = "No transcript loaded. Please upload your Degree Works PDF first.";
                StatusType = "error";
                return RedirectToPage();
            }

            try
            {
                var transcriptRecord = GetTranscriptRecord(studentId);
                if (transcriptRecord == null)
                {
                    StatusMessage = "No transcript record found. Please re-upload.";
                    StatusType = "error";
                    return RedirectToPage();
                }

                // Re-parse the transcript to get structured course data
                var filePath = transcriptRecord["FilePath"]?.ToString() ?? "";
                var parsedContext = transcriptRecord["ParsedContextText"]?.ToString() ?? "";

                // Parse courses from the stored context text
                int coursesImported = ImportCoursesFromContext(studentId, parsedContext);

                // Update GPA and credits
                UpdateStudentFromTranscript(studentId, parsedContext);

                StatusMessage = $"Your academic record has been updated. {coursesImported} course(s) imported into your history.";
                StatusType = "success";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply transcript failed for student {StudentId}", studentId);
                StatusMessage = $"Apply failed: {ex.Message}";
                StatusType = "error";
            }

            return RedirectToPage();
        }

        public IActionResult OnPostRemoveTranscript()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            HttpContext.Session.Remove(TRANSCRIPT_CONTEXT_KEY);
            HttpContext.Session.Remove(TRANSCRIPT_FILENAME_KEY);

            StatusMessage = "Transcript removed from session.";
            StatusType = "success";
            return RedirectToPage();
        }
        public IActionResult OnGetDebug()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            var sql = "SELECT FilePath FROM StudentTranscripts WHERE StudentID = @sid AND IsActive = 1 LIMIT 1";
            var result = _dbHelper.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (result == null || result.Rows.Count == 0)
                return Content("No transcript found in DB");

            var filePath = result.Rows[0][0]?.ToString() ?? "";

            // Download the PDF
            var azureConnStr = _configuration["AzureBlobStorage:ConnectionString"];
            byte[]? pdfBytes = null;

            if (filePath.StartsWith("http"))
            {
                var uri = new Uri(filePath);
                var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                var blobClient = new Azure.Storage.Blobs.BlobServiceClient(azureConnStr)
                    .GetBlobContainerClient(parts[0])
                    .GetBlobClient(parts[1]);
                using var ms = new MemoryStream();
                blobClient.DownloadTo(ms);
                pdfBytes = ms.ToArray();
            }
            else
            {
                var localPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath.TrimStart('/'));
                if (System.IO.File.Exists(localPath))
                    pdfBytes = System.IO.File.ReadAllBytes(localPath);
            }

            if (pdfBytes == null)
                return Content("Could not download PDF");

            var extract = _pdfService.ExtractWithLines(pdfBytes, "debug.pdf", maxPages: 2, maxCharsTotal: 5000);
            var fullText = string.Join("\n---PAGE BREAK---\n", extract.Pages.Select(p => p.Text));

            return Content($"Pages extracted: {extract.Pages.Count}\n\nFirst 3000 chars:\n{fullText.Substring(0, Math.Min(3000, fullText.Length))}",
                "text/plain");
        }

        private async Task<string> UploadToAzureAsync(byte[] fileBytes, int studentId, string originalFileName)
        {
            var azureConnStr = _configuration["AzureBlobStorage:ConnectionString"];

            if (string.IsNullOrEmpty(azureConnStr))
            {
                // Fall back to local storage
                var localFolder = Path.Combine(
                    Directory.GetCurrentDirectory(), "wwwroot", "uploads", "transcripts");
                Directory.CreateDirectory(localFolder);
                var localFileName = $"transcript_{studentId}.pdf";
                var localPath = Path.Combine(localFolder, localFileName);
                await System.IO.File.WriteAllBytesAsync(localPath, fileBytes);
                return $"/uploads/transcripts/{localFileName}";
            }

            var blobServiceClient = new BlobServiceClient(azureConnStr);
            var containerClient = blobServiceClient.GetBlobContainerClient("transcripts");
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            // Named by studentId so uploading always overwrites the previous
            var blobName = $"transcript_{studentId}.pdf";
            var blobClient = containerClient.GetBlobClient(blobName);

            using var stream = new MemoryStream(fileBytes);
            await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = "application/pdf" });

            return blobClient.Uri.ToString();
        }

        private void SaveTranscriptRecord(int studentId, string fileName, string filePath,
            long fileSize, string semester, int academicYear, string parsedContext)
        {
            // Check if record exists
            var checkSql = "SELECT TranscriptID FROM StudentTranscripts WHERE StudentID = @sid LIMIT 1";
            var existing = _dbHelper.ExecuteQuery(checkSql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (existing != null && existing.Rows.Count > 0)
            {
                // Update existing
                var updateSql = @"
                    UPDATE StudentTranscripts
                    SET FileName = @fileName,
                        FilePath = @filePath,
                        FileSize = @fileSize,
                        Semester = @semester,
                        AcademicYear = @academicYear,
                        ParsedContextText = @context,
                        UploadDate = NOW(),
                        IsActive = 1
                    WHERE StudentID = @sid";

                _dbHelper.ExecuteNonQuery(updateSql, new[]
                {
                    new MySqlParameter("@fileName",     MySqlDbType.VarChar)  { Value = fileName },
                    new MySqlParameter("@filePath",     MySqlDbType.VarChar)  { Value = filePath },
                    new MySqlParameter("@fileSize",     MySqlDbType.Int64)    { Value = fileSize },
                    new MySqlParameter("@semester",     MySqlDbType.VarChar)  { Value = semester },
                    new MySqlParameter("@academicYear", MySqlDbType.Int32)    { Value = academicYear },
                    new MySqlParameter("@context",      MySqlDbType.LongText) { Value = parsedContext },
                    new MySqlParameter("@sid",          MySqlDbType.Int32)    { Value = studentId }
                }, out _);
            }
            else
            {
                // Insert new
                var insertSql = @"
                    INSERT INTO StudentTranscripts
                    (StudentID, FileName, FilePath, FileSize, Semester, AcademicYear, ParsedContextText, IsActive)
                    VALUES
                    (@sid, @fileName, @filePath, @fileSize, @semester, @academicYear, @context, 1)";

                _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
                    new MySqlParameter("@sid",          MySqlDbType.Int32)    { Value = studentId },
                    new MySqlParameter("@fileName",     MySqlDbType.VarChar)  { Value = fileName },
                    new MySqlParameter("@filePath",     MySqlDbType.VarChar)  { Value = filePath },
                    new MySqlParameter("@fileSize",     MySqlDbType.Int64)    { Value = fileSize },
                    new MySqlParameter("@semester",     MySqlDbType.VarChar)  { Value = semester },
                    new MySqlParameter("@academicYear", MySqlDbType.Int32)    { Value = academicYear },
                    new MySqlParameter("@context",      MySqlDbType.LongText) { Value = parsedContext }
                }, out _);
            }
        }

        private void LoadCurrentTranscript()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            var sql = @"
                SELECT FileName, UploadDate, Semester, AcademicYear
                FROM StudentTranscripts
                WHERE StudentID = @sid AND IsActive = 1
                LIMIT 1";

            var result = _dbHelper.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (result != null && result.Rows.Count > 0)
            {
                var row = result.Rows[0];
                CurrentTranscriptFileName = row["FileName"]?.ToString();
                CurrentTranscriptUploadDate = row["UploadDate"] != System.DBNull.Value
                    ? Convert.ToDateTime(row["UploadDate"]).ToString("MMM d, yyyy h:mm tt")
                    : "";
                CurrentTranscriptSemester = $"{row["Semester"]} {row["AcademicYear"]}";
            }
        }

        private System.Data.DataRow? GetTranscriptRecord(int studentId)
        {
            var sql = "SELECT * FROM StudentTranscripts WHERE StudentID = @sid AND IsActive = 1 LIMIT 1";
            var result = _dbHelper.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (result != null && result.Rows.Count > 0)
                return result.Rows[0];
            return null;
        }

        private void UpdateStudentFromTranscript(int studentId, string parsedContext)
        {
            // Extract GPA from parsed context
            var gpaMatch = Regex.Match(parsedContext, @"Overall GPA:\s*([\d.]+)", RegexOptions.IgnoreCase);
            // Extract credits applied
            var creditsMatch = Regex.Match(parsedContext, @"Credits Applied:\s*(\d+)", RegexOptions.IgnoreCase);

            if (gpaMatch.Success && decimal.TryParse(gpaMatch.Groups[1].Value, out var gpa) &&
                creditsMatch.Success && int.TryParse(creditsMatch.Groups[1].Value, out var credits))
            {
                var sql = @"
                    UPDATE Students
                    SET CurrentGPA = @gpa,
                        TotalCreditsEarned = @credits
                    WHERE StudentID = @sid";

                _dbHelper.ExecuteNonQuery(sql, new[]
                {
                    new MySqlParameter("@gpa",     MySqlDbType.Decimal) { Value = gpa },
                    new MySqlParameter("@credits", MySqlDbType.Int32)   { Value = credits },
                    new MySqlParameter("@sid",     MySqlDbType.Int32)   { Value = studentId }
                }, out _);
            }
        }

        private int ImportCoursesFromContext(int studentId, string parsedContext)
        {
            int imported = 0;

            // Clear existing history first so re-importing is clean
            _dbHelper.ExecuteNonQuery(
                "DELETE FROM StudentCourseHistory WHERE StudentID = @sid",
                new[] { new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId } },
                out _);

            // Load all course codes from DB once to avoid per-course lookups
            var allCoursesSql = "SELECT CourseID, CourseCode FROM Courses WHERE IsActive = 1";
            var allCourses = _dbHelper.ExecuteQuery(allCoursesSql, Array.Empty<MySqlParameter>(), out _);
            var courseIdMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (allCourses != null)
            {
                foreach (System.Data.DataRow r in allCourses.Rows)
                    courseIdMap[r["CourseCode"].ToString() ?? ""] = Convert.ToInt32(r["CourseID"]);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Parse completed courses
            // Format: "  - COURSE_CODE | Title | Grade: X | N cr | Term"
            var completedRx = new Regex(
                @"^\s{2}-\s+([A-Z]{2,5}\s+\d+[A-Z]?)\s*\|\s*.+?\s*\|\s*Grade:\s*(\S+)\s*\|\s*(\d+)\s*cr\s*\|\s*((?:First |Second )?(?:Spring|Fall|Summer)\s+\d{4})",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match m in completedRx.Matches(parsedContext))
            {
                var courseCode = m.Groups[1].Value.Trim();
                var grade = m.Groups[2].Value.Trim();
                var termFull = m.Groups[4].Value.Trim();

                if (!seen.Add(courseCode)) continue;
                if (grade.Equals("W", StringComparison.OrdinalIgnoreCase)) continue;
                if (grade.Equals("WF", StringComparison.OrdinalIgnoreCase)) continue;

                bool isInProgress = grade.Equals("IP", StringComparison.OrdinalIgnoreCase);

                if (!courseIdMap.TryGetValue(courseCode, out int courseId)) continue;

                var termMatch = Regex.Match(termFull, @"((?:First |Second )?(?:Spring|Fall|Summer))\s+(\d{4})", RegexOptions.IgnoreCase);
                string rawTerm = termMatch.Success ? termMatch.Groups[1].Value.Trim() : "Fall";
                int academicYear = termMatch.Success ? int.Parse(termMatch.Groups[2].Value) : DateTime.Now.Year;

                string dbTerm = rawTerm.Contains("Fall", StringComparison.OrdinalIgnoreCase) ? "Fall" :
                                 rawTerm.Contains("Spring", StringComparison.OrdinalIgnoreCase) ? "Spring" : "Summer";
                string dbGrade = grade.Length > 2 ? grade.Substring(0, 2) : grade;

                var insertSql = @"
            INSERT INTO StudentCourseHistory
            (StudentID, CourseID, Grade, Term, AcademicYear, Status)
            VALUES
            (@studentId, @courseId, @grade, @term, @academicYear, @status)";

                _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
            new MySqlParameter("@studentId",    MySqlDbType.Int32)   { Value = studentId },
            new MySqlParameter("@courseId",     MySqlDbType.Int32)   { Value = courseId },
            new MySqlParameter("@grade",        MySqlDbType.VarChar) { Value = dbGrade },
            new MySqlParameter("@term",         MySqlDbType.VarChar) { Value = dbTerm },
            new MySqlParameter("@academicYear", MySqlDbType.Int32)   { Value = academicYear },
            new MySqlParameter("@status",       MySqlDbType.VarChar) { Value = isInProgress ? "In Progress" : "Completed" }
        }, out _);

                imported++;
            }

            // Parse in-progress courses from the "--- Currently In Progress ---" section
            // Format: "- CS 321 (Architec Digitl Comput) | 3 cr | Spring 2026 | Block: ..."
            var inProgressRx = new Regex(
                @"^-\s+([A-Z]{2,5}\s+\d+[A-Z]?)\s*\(.+?\)\s*\|\s*\d+\s*cr\s*\|\s*((?:First |Second )?(?:Spring|Fall|Summer)\s+\d{4})",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            // Only search within the in-progress section
            var ipStart = parsedContext.IndexOf("--- Currently In Progress ---", StringComparison.Ordinal);
            var ipEnd = parsedContext.IndexOf("---", ipStart + 35, StringComparison.Ordinal);
            if (ipStart >= 0)
            {
                var ipSection = ipEnd > ipStart
                    ? parsedContext.Substring(ipStart, ipEnd - ipStart)
                    : parsedContext.Substring(ipStart);

                foreach (Match m in inProgressRx.Matches(ipSection))
                {
                    var courseCode = m.Groups[1].Value.Trim();
                    var termFull = m.Groups[2].Value.Trim();

                    if (!seen.Add(courseCode)) continue;
                    if (!courseIdMap.TryGetValue(courseCode, out int courseId)) continue;

                    var termMatch = Regex.Match(termFull, @"((?:Spring|Fall|Summer))\s+(\d{4})", RegexOptions.IgnoreCase);
                    string dbTerm = termMatch.Success
                        ? (termMatch.Groups[1].Value.Contains("Fall", StringComparison.OrdinalIgnoreCase) ? "Fall" :
                           termMatch.Groups[1].Value.Contains("Spring", StringComparison.OrdinalIgnoreCase) ? "Spring" : "Summer")
                        : "Spring";
                    int academicYear = termMatch.Success ? int.Parse(termMatch.Groups[2].Value) : DateTime.Now.Year;

                    var insertSql = @"
                INSERT INTO StudentCourseHistory
                (StudentID, CourseID, Grade, Term, AcademicYear, Status)
                VALUES
                (@studentId, @courseId, @grade, @term, @academicYear, 'In Progress')";

                    _dbHelper.ExecuteNonQuery(insertSql, new[]
                    {
                new MySqlParameter("@studentId",    MySqlDbType.Int32)   { Value = studentId },
                new MySqlParameter("@courseId",     MySqlDbType.Int32)   { Value = courseId },
                new MySqlParameter("@grade",        MySqlDbType.VarChar) { Value = "IP" },
                new MySqlParameter("@term",         MySqlDbType.VarChar) { Value = dbTerm },
                new MySqlParameter("@academicYear", MySqlDbType.Int32)   { Value = academicYear }
            }, out _);

                    imported++;
                }
            }

            return imported;
        }

        // Used by Chat to load transcript context into session if not already loaded
        public string? LoadTranscriptContextFromDb(int studentId)
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

            if (result != null && result.Rows.Count > 0 && result.Rows[0][0] != System.DBNull.Value)
                return result.Rows[0][0]?.ToString();

            return null;
        }
    }
}