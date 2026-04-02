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

            var extract = _pdfService.ExtractWithLines(pdfBytes, "debug.pdf", maxPages: 10, maxCharsTotal: 50000);
            var fullText = string.Join("\n---PAGE BREAK---\n", extract.Pages.Select(p => p.Text));

            var idx = fullText.IndexOf("ECE 241", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return Content($"Found ECE 241 at position {idx}:\n\n{fullText.Substring(Math.Max(0, idx - 200), Math.Min(500, fullText.Length - Math.Max(0, idx - 200)))}", "text/plain");
            return Content($"ECE 241 NOT FOUND in {extract.Pages.Count} pages. Total chars: {fullText.Length}\n\nFirst 3000:\n{fullText.Substring(0, Math.Min(3000, fullText.Length))}", "text/plain");
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

            // Map Degree Works block/sub-label names to Progress page requirement categories
            var blockToCategoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Core 39"] = "Core 39 General Education",
                ["Bachelor of Science Skills Requirement"] = "Core 39 General Education",
                ["Bachelor of Arts Skills Requirement"] = "Core 39 General Education",
                ["Embedded Experience"] = "Core 39 General Education",
                ["Major in Computer Science"] = "CS Core Requirements",
                ["Major in Computer Information Systems"] = "CS Core Requirements",
                ["Directed Electives"] = "Directed Electives",
                ["General Electives"] = "General Electives",
                ["Advanced Programming"] = "Advanced CS Courses",
                ["Capstone Project"] = "CS Core Requirements",
                ["Information Systems"] = "CS Core Requirements",
                ["Hardware Foundation"] = "CS Core Requirements",
                ["Mathematics Foundation"] = "CS Core Requirements",
                ["BACHELOR OF SCIENCE REQUIREMENTS"] = "Core 39 General Education",
                ["FOUNDATION SKILLS"] = "Core 39 General Education",
                ["PHYSICAL ACTIVITY AND WELLNESS"] = "Core 39 General Education",
                ["WAYS OF KNOWING"] = "Core 39 General Education",
                ["BACHELOR OF ARTS REQUIREMENTS"] = "Core 39 General Education",
            };

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

            // Parse completed courses tracking current block header
            string currentCategory = "";
            bool inCompletedSection = false;
            foreach (var line in parsedContext.Split('\n'))
            {
                var trimmed = line.TrimEnd();

                // Only start tracking blocks after the completed courses header
                if (trimmed.Contains("--- Completed Courses ---"))
                {
                    inCompletedSection = true;
                    continue;
                }

                if (!inCompletedSection) continue;

                // Stop if we hit the end marker
                if (trimmed.Contains("=== END DEGREE WORKS TRANSCRIPT ===")) break;

                // Detect block headers in the summary (e.g. "Major in Computer Science:")
                if (trimmed.EndsWith(":") && !trimmed.StartsWith(" ") && !trimmed.StartsWith("-"))
                {
                    var blockName = trimmed.TrimEnd(':').Trim();
                    currentCategory = blockToCategoryMap.TryGetValue(blockName, out var cat) ? cat : blockName;
                    continue;
                }

                var m = completedRx.Match(trimmed);
                if (!m.Success) continue;

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
                    (StudentID, CourseID, Grade, Term, AcademicYear, Status, RequirementCategory)
                    VALUES
                    (@studentId, @courseId, @grade, @term, @academicYear, @status, @category)";

                _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
                    new MySqlParameter("@studentId",    MySqlDbType.Int32)   { Value = studentId },
                    new MySqlParameter("@courseId",     MySqlDbType.Int32)   { Value = courseId },
                    new MySqlParameter("@grade",        MySqlDbType.VarChar) { Value = dbGrade },
                    new MySqlParameter("@term",         MySqlDbType.VarChar) { Value = dbTerm },
                    new MySqlParameter("@academicYear", MySqlDbType.Int32)   { Value = academicYear },
                    new MySqlParameter("@status",       MySqlDbType.VarChar) { Value = isInProgress ? "In Progress" : "Completed" },
                    new MySqlParameter("@category",     MySqlDbType.VarChar) { Value = string.IsNullOrEmpty(currentCategory) ? (object)DBNull.Value : currentCategory }
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
                        (StudentID, CourseID, Grade, Term, AcademicYear, Status, RequirementCategory)
                        VALUES
                        (@studentId, @courseId, @grade, @term, @academicYear, 'In Progress', @category)";

                    _dbHelper.ExecuteNonQuery(insertSql, new[]
                    {
                        new MySqlParameter("@studentId",    MySqlDbType.Int32)   { Value = studentId },
                        new MySqlParameter("@courseId",     MySqlDbType.Int32)   { Value = courseId },
                        new MySqlParameter("@grade",        MySqlDbType.VarChar) { Value = "IP" },
                        new MySqlParameter("@term",         MySqlDbType.VarChar) { Value = dbTerm },
                        new MySqlParameter("@academicYear", MySqlDbType.Int32)   { Value = academicYear },
                        new MySqlParameter("@category",     MySqlDbType.VarChar) { Value = "CS Core Requirements" }
                    }, out _);

                    imported++;
                }
            }

            // Post-process: fix Directed Electives category
            // The Degree Works PDF lists "Directed Electives" as a sub-label within the Major block
            // We detect this from the raw summary by finding the Directed Electives section
            FixDirectedElectivesCategory(studentId, parsedContext);
            FixAdvancedProgrammingCategory(studentId);

            return imported;
        }


        private void FixDirectedElectivesCategory(int studentId, string parsedContext)
        {
            // Get the student's degree ID
            var degreeIdSql = @"
                SELECT dp.DegreeID 
                FROM Students s
                JOIN DegreePrograms dp ON s.Major = dp.DegreeName
                WHERE s.StudentID = @sid LIMIT 1";

            var degreeResult = _dbHelper.ExecuteQuery(degreeIdSql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (degreeResult == null || degreeResult.Rows.Count == 0) return;
            var degreeIdVal = degreeResult.Rows[0]["DegreeID"];
            if (degreeIdVal == DBNull.Value || degreeIdVal == null) return;
            int degreeId = Convert.ToInt32(degreeIdVal);

            // Get all CourseIDs that ARE in DegreeRequirements for this degree
            var requiredCoursesSql = @"
                SELECT CourseID, RequirementCategory FROM DegreeRequirements WHERE DegreeID = @degreeId";

            var requiredResult = _dbHelper.ExecuteQuery(requiredCoursesSql, new[]
            {
                new MySqlParameter("@degreeId", MySqlDbType.Int32) { Value = degreeId }
            }, out _);

            var requiredCourseIds = new HashSet<int>();
            var directedElectiveIds = new HashSet<int>();
            if (requiredResult != null)
            {
                foreach (System.Data.DataRow r in requiredResult.Rows)
                {
                    if (r["CourseID"] == DBNull.Value) continue;
                    int cid = Convert.ToInt32(r["CourseID"]);
                    var category = r["RequirementCategory"]?.ToString() ?? "";
                    requiredCourseIds.Add(cid);
                    if (category == "Directed Electives")
                        directedElectiveIds.Add(cid);
                }
            }

            if (!requiredCourseIds.Any()) return;

            // Get all courses tagged CS Core Requirements for this student
            var studentCoursesSql = @"
                SELECT HistoryID, CourseID 
                FROM StudentCourseHistory
                WHERE StudentID = @sid 
                AND RequirementCategory = 'CS Core Requirements'
                AND Status = 'Completed'";

            var studentCourses = _dbHelper.ExecuteQuery(studentCoursesSql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (studentCourses == null) return;

            // Update courses that are NOT in the required list to Directed Electives
            foreach (System.Data.DataRow r in studentCourses.Rows)
            {
                if (r["CourseID"] == DBNull.Value || r["HistoryID"] == DBNull.Value) continue;
                int courseId = Convert.ToInt32(r["CourseID"]);
                int historyId = Convert.ToInt32(r["HistoryID"]);

                string newCategory;
                if (directedElectiveIds.Contains(courseId))
                    newCategory = "Directed Electives";
                else if (requiredCourseIds.Contains(courseId))
                    continue; // keep as CS Core Requirements
                else
                    newCategory = "General Electives";

                _dbHelper.ExecuteNonQuery(
                    "UPDATE StudentCourseHistory SET RequirementCategory = @cat WHERE HistoryID = @id",
                    new[]
                    {
                        new MySqlParameter("@cat", MySqlDbType.VarChar) { Value = newCategory },
                        new MySqlParameter("@id",  MySqlDbType.Int32)   { Value = historyId }
                    }, out _);
            }

            // Fix courses that are in DegreeRequirements as CS Core but got tagged as Core 39
            // because they appear in both Core 39 and Major blocks in the transcript
            var core39OverrideSql = @"
                SELECT sch.HistoryID, sch.CourseID, dr.RequirementCategory as DrCategory
                FROM StudentCourseHistory sch
                JOIN DegreeRequirements dr ON sch.CourseID = dr.CourseID
                WHERE sch.StudentID = @sid
                  AND sch.RequirementCategory = 'Core 39 General Education'
                  AND dr.DegreeID = @degreeId
                  AND dr.RequirementCategory NOT IN ('Core 39 General Education', 'Directed Electives')
                  AND dr.RequirementCategory IS NOT NULL";

            var core39Override = _dbHelper.ExecuteQuery(core39OverrideSql, new[]
            {
                new MySqlParameter("@sid",      MySqlDbType.Int32) { Value = studentId },
                new MySqlParameter("@degreeId", MySqlDbType.Int32) { Value = degreeId }
            }, out _);

            if (core39Override != null)
            {
                foreach (System.Data.DataRow r in core39Override.Rows)
                {
                    if (r["HistoryID"] == DBNull.Value) continue;
                    int historyId = Convert.ToInt32(r["HistoryID"]);
                    string drCat = r["DrCategory"]?.ToString() ?? "";

                    // Map DegreeRequirements category to Progress category
                    string newCat = drCat switch
                    {
                        "Mathematics Foundation" => "CS Core Requirements",
                        "Communication" => "CS Core Requirements",
                        "Hardware Foundation" => "CS Core Requirements",
                        "Information Systems" => "CS Core Requirements",
                        "Professional Development" => "CS Core Requirements",
                        "Capstone Project" => "CS Core Requirements",
                        "Advanced Programming" => "Advanced CS Courses",
                        _ => "CS Core Requirements"
                    };

                    _dbHelper.ExecuteNonQuery(
                        "UPDATE StudentCourseHistory SET RequirementCategory = @cat WHERE HistoryID = @id",
                        new[]
                        {
                            new MySqlParameter("@cat", MySqlDbType.VarChar) { Value = newCat },
                            new MySqlParameter("@id",  MySqlDbType.Int32)   { Value = historyId }
                        }, out _);
                }
            }
        }

        private void FixAdvancedProgrammingCategory(int studentId)
        {
            // Courses in DegreeRequirements as "Advanced Programming" should be tagged
            // as "Advanced CS Courses" in StudentCourseHistory
            var advancedCoursesSql = @"
                SELECT dr.CourseID
                FROM DegreeRequirements dr
                WHERE dr.DegreeID = (
                    SELECT dp.DegreeID
                    FROM Students s
                    JOIN DegreePrograms dp ON s.Major = dp.DegreeName
                    WHERE s.StudentID = @sid
                    LIMIT 1
                )
                AND dr.RequirementCategory = 'Advanced Programming'";

            var advancedResult = _dbHelper.ExecuteQuery(advancedCoursesSql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (advancedResult == null || advancedResult.Rows.Count == 0) return;

            foreach (System.Data.DataRow r in advancedResult.Rows)
            {
                if (r["CourseID"] == DBNull.Value) continue;
                int courseId = Convert.ToInt32(r["CourseID"]);

                _dbHelper.ExecuteNonQuery(
                    @"UPDATE StudentCourseHistory 
                      SET RequirementCategory = 'Advanced CS Courses'
                      WHERE StudentID = @sid AND CourseID = @courseId
                      AND RequirementCategory IN ('CS Core Requirements', 'Directed Electives')",
                    new[]
                    {
                        new MySqlParameter("@sid",      MySqlDbType.Int32) { Value = studentId },
                        new MySqlParameter("@courseId", MySqlDbType.Int32) { Value = courseId }
                    }, out _);
            }
        }

    }
}