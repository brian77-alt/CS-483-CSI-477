using AdvisorDb;
using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;

namespace CS_483_CSI_477.Pages
{
    public class ParseBulletinCoursesModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BulletinCourseParser _parser;

        public List<string> Results { get; set; } = new();
        public int TotalCoursesAdded { get; set; }
        public int BulletinsParsed { get; set; }

        public ParseBulletinCoursesModel(DatabaseHelper dbHelper, BulletinCourseParser parser)
        {
            _dbHelper = dbHelper;
            _parser = parser;
        }

        public IActionResult OnGet()
        {
            if (HttpContext.Session.GetString("Role") != "Admin")
                return RedirectToPage("/Login");

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (HttpContext.Session.GetString("Role") != "Admin")
                return RedirectToPage("/Login");

            var query = @"
                SELECT BulletinID, FileName, FilePath, BulletinCategory 
                FROM Bulletins 
                WHERE IsActive = 1 
                AND BulletinYear = '2026-2027'
                ORDER BY BulletinCategory, FileName";

            var bulletins = _dbHelper.ExecuteQuery(query, Array.Empty<MySqlParameter>(), out var err);

            if (!string.IsNullOrEmpty(err) || bulletins == null)
            {
                Results.Add($"Error loading bulletins: {err}");
                return Page();
            }

            Results.Add($"Found {bulletins.Rows.Count} bulletins to parse...");
            Results.Add("");

            // DEBUG: Show raw text of first bulletin only
            var firstRow = bulletins.Rows[0];
            var rawText = await _parser.GetRawPdfText(firstRow["FilePath"].ToString() ?? "");
            Results.Add("=== FIRST BULLETIN RAW TEXT ===");
            Results.Add(rawText.Length > 1500 ? rawText[..1500] : rawText);
            Results.Add("=== END ===");
            Results.Add($"FilePath was: {firstRow["FilePath"]}");
            Results.Add("");

            foreach (System.Data.DataRow row in bulletins.Rows)
            {
                var fileName = row["FileName"].ToString() ?? "";
                var filePath = row["FilePath"].ToString() ?? "";
                var category = row["BulletinCategory"].ToString() ?? "";

                Results.Add($"Parsing: {fileName} ({category})...");

                var courses = await _parser.ParseBulletinFromAzure(filePath);

                if (courses.Count > 0)
                {
                    var inserted = _parser.InsertCoursesIntoDatabase(courses, fileName);
                    TotalCoursesAdded += inserted;
                    BulletinsParsed++;
                    Results.Add($"   Found {courses.Count} courses, added {inserted} new courses");
                }
                else
                {
                    Results.Add($"   No courses found in this bulletin");
                }
            }

            Results.Add("");
            Results.Add($" Complete! Parsed {BulletinsParsed} bulletins, added {TotalCoursesAdded} new courses to database");

            return Page();
        }
    }
}