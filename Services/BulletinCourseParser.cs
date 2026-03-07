using AdvisorDb;
using Azure.Storage.Blobs;
using CS_483_CSI_477.Services;
using MySql.Data.MySqlClient;
using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Services
{
    public class CourseParseResult
    {
        public string CourseCode { get; set; } = "";
        public string CourseName { get; set; } = "";
        public int CreditHours { get; set; }
        public string Department { get; set; } = "";
        public string Description { get; set; } = "";
        public string Prerequisites { get; set; } = "";
        public string TermsOffered { get; set; } = "";
    }

    public class BulletinCourseParser
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly PdfService _pdfService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BulletinCourseParser> _logger;

        public BulletinCourseParser(
            DatabaseHelper dbHelper,
            PdfService pdfService,
            IConfiguration configuration,
            ILogger<BulletinCourseParser> logger)
        {
            _dbHelper = dbHelper;
            _pdfService = pdfService;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<string> GetRawPdfText(string filePath)
        {
            var pdfBytes = await DownloadPdfFromAzure(filePath);
            if (pdfBytes == null) return $"DOWNLOAD FAILED for: {filePath}";

            var fileName = Path.GetFileName(filePath);
            var extract = _pdfService.Extract(pdfBytes, fileName, maxPages: 2, maxCharsTotal: 3000);
            return string.Join("\n--- PAGE BREAK ---\n", extract.Pages.Select(p => p.Text));
        }

        public static List<string> GetDepartmentsForMajor(string majorName)
        {
            // Map major name keywords to department codes
            var mappings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Computer Science",         new() { "CS", "CIS", "MAT", "MATH", "ECE" } },
                { "Computer Information",     new() { "CIS", "CS", "MAT" } },
                { "Nursing",                  new() { "NURS", "BIOL", "CHEM" } },
                { "Biology",                  new() { "BIOL", "CHEM", "MATH" } },
                { "Chemistry",                new() { "CHEM", "BIOL", "MATH" } },
                { "Mathematics",              new() { "MATH", "MAT", "CS", "STAT" } },
                { "Accounting",               new() { "ACC", "FIN", "BUA", "MNG" } },
                { "Business Administration",  new() { "BUA", "MNG", "FIN", "ACC", "MKT", "ECO" } },
                { "Finance",                  new() { "FIN", "ACC", "ECO", "BUA" } },
                { "Marketing",                new() { "MKT", "BUA", "MNG" } },
                { "Management",               new() { "MNG", "BUA", "HRM" } },
                { "Economics",                new() { "ECO", "ECON", "FIN", "MAT" } },
                { "Psychology",               new() { "PSY", "SOC" } },
                { "Sociology",                new() { "SOC", "PSY", "ANTH" } },
                { "Anthropology",             new() { "ANTH", "SOC" } },
                { "Criminal Justice",         new() { "CRIM", "SOC", "PSY" } },
                { "Communication",            new() { "COMM", "CMST", "JRN" } },
                { "Journalism",               new() { "JRN", "COMM", "CMST" } },
                { "English",                  new() { "ENG", "ENGL" } },
                { "History",                  new() { "HIST", "HI" } },
                { "Political Science",        new() { "POLS", "SOC", "HIST" } },
                { "Philosophy",               new() { "PHIL" } },
                { "Art",                      new() { "ART" } },
                { "Theatre",                  new() { "THTR" } },
                { "Music",                    new() { "MUS" } },
                { "Physics",                  new() { "PHY", "PHYS", "MATH" } },
                { "Engineering",              new() { "ENGR", "EE", "ME", "CE", "MATH", "PHY" } },
                { "Civil Engineering",        new() { "CE", "ENGR", "MATH" } },
                { "Electrical Engineering",   new() { "EE", "ENGR", "MATH", "PHY" } },
                { "Mechanical Engineering",   new() { "ME", "ENGR", "MATH", "PHY" } },
                { "Social Work",              new() { "SOCW", "SOC", "PSY" } },
                { "Education",                new() { "EDUC", "PSY" } },
                { "Kinesiology",              new() { "KIN", "BIOL", "CHEM" } },
                { "Exercise Science",         new() { "EXSC", "KIN", "BIOL" } },
                { "Geology",                  new() { "GEOL", "CHEM", "MATH" } },
                { "Health",                   new() { "HIM", "HIIM", "BIOL" } },
                { "Radiologic",               new() { "RADT", "BIOL" } },
                { "Respiratory",              new() { "REST", "BIOL" } },
                { "Dental",                   new() { "DH", "BIOL", "CHEM" } },
                { "Statistics",               new() { "STAT", "MATH", "CS" } },
                { "Environmental",            new() { "ENVS", "BIOL", "CHEM", "GEOL" } },
                { "Global Studies",           new() { "GLST", "HIST", "POLS" } },
                { "World Languages",          new() { "SPAN", "FREN", "GERM", "WLC" } },
            };

            foreach (var kvp in mappings)
            {
                if (majorName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }

            // Fallback: return empty (AI uses all courses)
            return new List<string>();
        }

        public async Task<List<CourseParseResult>> ParseBulletinFromAzure(string filePath)
        {
            try
            {
                var pdfBytes = await DownloadPdfFromAzure(filePath);
                if (pdfBytes == null) return new List<CourseParseResult>();

                var fileName = Path.GetFileName(filePath);
                var extract = _pdfService.Extract(pdfBytes, fileName, maxPages: 50, maxCharsTotal: 500_000);

                return ParseCoursesFromPdfPages(extract.Pages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to parse bulletin: {filePath}");
                return new List<CourseParseResult>();
            }
        }

        private List<CourseParseResult> ParseCoursesFromPdfPages(List<PdfPageText> pages)
        {
            var courses = new List<CourseParseResult>();
            var fullText = string.Join("\n", pages.Select(p => p.Text));

            var courseHeaderPattern = new Regex(@"([A-Z]{2,5})\s+(\d{3})\s*-\s*([^\n]{5,80})", RegexOptions.Multiline);
            var creditsPattern = new Regex(@"Credits?:\s*(\d+)");
            var termsPattern = new Regex(@"Term\(s\)\s*Offered:\s*([^\n<]{3,50})", RegexOptions.IgnoreCase);

            var matches = courseHeaderPattern.Matches(fullText);

            foreach (Match match in matches)
            {
                var dept = match.Groups[1].Value.Trim();
                var number = match.Groups[2].Value.Trim();
                var name = match.Groups[3].Value.Trim();

                name = Regex.Replace(name, @"\s*(USI Core|Term\(s\)|Credits?:).*", "", RegexOptions.IgnoreCase).Trim();
                if (name.Length < 4) continue;

                var searchWindow = fullText.Substring(match.Index, Math.Min(300, fullText.Length - match.Index));

                // Extract credits
                int credits = 3;
                var creditsMatch = creditsPattern.Match(searchWindow);
                if (creditsMatch.Success) int.TryParse(creditsMatch.Groups[1].Value, out credits);

                // Extract terms offered
                var termsMatch = termsPattern.Match(searchWindow);
                var terms = termsMatch.Success ? termsMatch.Groups[1].Value.Trim() : "";
                terms = Regex.Replace(terms, @"Credits?:.*", "", RegexOptions.IgnoreCase).Trim();
                if (terms.Length > 50) terms = terms[..50];

                var courseCode = $"{dept} {number}";
                if (courses.Any(c => c.CourseCode == courseCode)) continue;

                courses.Add(new CourseParseResult
                {
                    CourseCode = courseCode,
                    CourseName = name.Length > 250 ? name[..250] : name,
                    CreditHours = credits > 0 && credits <= 12 ? credits : 3,
                    Department = dept,
                    Description = "",
                    Prerequisites = "",
                    TermsOffered = terms
                });
            }

            return courses;
        }

        private string CleanCourseName(string name)
        {
            // Remove "Credits:" or "Term(s) Offered:" etc
            name = Regex.Replace(name, @"(Credits?:|Term\(s\)\s*Offered:).*", "", RegexOptions.IgnoreCase);
            return name.Trim();
        }

        public int InsertCoursesIntoDatabase(List<CourseParseResult> courses, string source)
        {
            int inserted = 0;

            foreach (var course in courses)
            {
                var checkSql = "SELECT CourseID FROM Courses WHERE CourseCode = @code";
                var exists = _dbHelper.ExecuteQuery(checkSql, new[]
                {
            new MySqlParameter("@code", MySqlDbType.VarChar) { Value = course.CourseCode }
        }, out _);

                if (exists != null && exists.Rows.Count > 0)
                {
                    if (!string.IsNullOrEmpty(course.TermsOffered))
                    {
                        var updateSql = @"UPDATE Courses SET TypicalTermsOffered = @terms 
                                 WHERE CourseCode = @code 
                                 AND (TypicalTermsOffered IS NULL OR TypicalTermsOffered = '')";
                        _dbHelper.ExecuteNonQuery(updateSql, new[]
                        {
                    new MySqlParameter("@terms", MySqlDbType.VarChar) { Value = course.TermsOffered },
                    new MySqlParameter("@code", MySqlDbType.VarChar)  { Value = course.CourseCode }
                }, out _);
                    }
                    continue;
                }

                var levelStr = course.CourseCode.Split(' ').LastOrDefault() ?? "100";
                var levelPrefix = levelStr.Length >= 3 ? levelStr[0] + "00" : "100";
                var validLevels = new[] { "100", "200", "300", "400", "500" };
                var courseLevel = validLevels.Contains(levelPrefix) ? levelPrefix : "100";

                var insertSql = @"
                    INSERT INTO Courses 
                    (CourseCode, CourseName, CreditHours, Department, CourseDescription, CourseLevel, TypicalTermsOffered, IsActive)
                    VALUES 
                    (@code, @name, @credits, @dept, @desc, @level, @terms, 1)";

                var rows = _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
                    new MySqlParameter("@code", MySqlDbType.VarChar)  { Value = course.CourseCode },
                    new MySqlParameter("@name", MySqlDbType.VarChar)  { Value = course.CourseName },
                    new MySqlParameter("@credits", MySqlDbType.Int32) { Value = course.CreditHours },
                    new MySqlParameter("@dept", MySqlDbType.VarChar)  { Value = course.Department },
                    new MySqlParameter("@desc", MySqlDbType.Text)     { Value = "" },
                    new MySqlParameter("@level", MySqlDbType.VarChar) { Value = courseLevel },
                    new MySqlParameter("@terms", MySqlDbType.VarChar) { Value = course.TermsOffered }
                }, out var err);

                if (string.IsNullOrEmpty(err) && rows > 0) inserted++;
            }

            return inserted;
        }

        private async Task<byte[]?> DownloadPdfFromAzure(string filePath)
        {
            try
            {
                var connStr = _configuration["AzureBlobStorage:ConnectionString"];
                if (string.IsNullOrEmpty(connStr))
                {
                    _logger.LogError("Azure connection string is null or empty");
                    return null;
                }

                string containerName;
                string blobName;

                if (filePath.StartsWith("http"))
                {
                    // Full URL: https://account.blob.core.windows.net/container/blob
                    var uri = new Uri(filePath);
                    var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                    if (segments.Length < 2) { _logger.LogError($"Bad URL format: {filePath}"); return null; }
                    containerName = segments[0];
                    blobName = Uri.UnescapeDataString(segments[1]);
                }
                else
                {
                    // Relative path: container/folder/file.pdf
                    var parts = filePath.Split('/', 2);
                    if (parts.Length < 2) { _logger.LogError($"Bad path format: {filePath}"); return null; }
                    containerName = parts[0];
                    blobName = parts[1];
                }

                _logger.LogInformation($"Downloading: container={containerName}, blob={blobName}");

                var blobServiceClient = new BlobServiceClient(connStr);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                using var ms = new MemoryStream();
                await blobClient.DownloadToAsync(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to download: {filePath}");
                return null;
            }
        }
    }
}