using AdvisorDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;
using System.Data;

namespace CS_483_CSI_477.Pages
{
    public class PlannerModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;

        public string StudentName { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;

        // This is the first year in the 4-year plan grid
        public int StartYear { get; set; } = DateTime.Now.Year;

        public List<SemesterPlan> Semesters { get; set; } = new();
        public DataTable? AvailableCourses { get; set; }

        public PlannerModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public IActionResult OnGet()
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            LoadStudentInfo();
            LoadPlannedCourses();
            LoadAvailableCourses();
            return Page();
        }

        private void LoadStudentInfo()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            // NOTE: Your Students table does NOT have ExpectedGraduationYear.
            // It has EnrollmentYear and ExpectedGraduationDate.
            string query = @"
SELECT 
    CONCAT(FirstName, ' ', LastName) AS FullName,
    Major,
    EnrollmentYear
FROM Students
WHERE StudentID = @sid
LIMIT 1;";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out _);

            if (result != null && result.Rows.Count > 0)
            {
                var row = result.Rows[0];
                StudentName = row["FullName"]?.ToString() ?? "Student";
                Major = row["Major"]?.ToString() ?? "Undeclared";

                // StartYear uses EnrollmentYear if available; else current year
                var enrollVal = row["EnrollmentYear"];
                if (enrollVal != DBNull.Value && int.TryParse(enrollVal.ToString(), out var enrollYear) && enrollYear > 1900)
                    StartYear = enrollYear;
                else
                    StartYear = DateTime.Now.Year;
            }
        }

        private void LoadPlannedCourses()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            // Build 8 semesters (Fall/Spring) starting from StartYear
            Semesters.Clear();
            for (int year = 0; year < 4; year++)
            {
                Semesters.Add(new SemesterPlan
                {
                    Term = "Fall",
                    Year = StartYear + year,
                    Courses = new List<PlannedCourse>()
                });

                Semesters.Add(new SemesterPlan
                {
                    Term = "Spring",
                    Year = StartYear + year + 1,
                    Courses = new List<PlannedCourse>()
                });
            }

            // ✅ Your real schema:
            // PlannedCourses: PlanID, PlannedTerm, PlannedYear
            // StudentDegreePlans: PlanID, StudentID
            string query = @"
SELECT 
    pc.PlannedCourseID,
    c.CourseCode,
    c.CourseName,
    c.CreditHours,
    pc.PlannedTerm,
    pc.PlannedYear,
    pc.IsCompleted
FROM PlannedCourses pc
JOIN Courses c ON pc.CourseID = c.CourseID
JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
WHERE sdp.StudentID = @sid
  AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
ORDER BY pc.PlannedYear, pc.PlannedTerm;";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out _);

            if (result == null) return;

            foreach (DataRow row in result.Rows)
            {
                // tinyint(1) -> safe bool conversion
                bool isCompleted = false;
                var ic = row["IsCompleted"];
                if (ic != DBNull.Value)
                {
                    // handles 0/1, true/false
                    if (ic is bool b) isCompleted = b;
                    else if (int.TryParse(ic.ToString(), out var n)) isCompleted = (n != 0);
                    else if (bool.TryParse(ic.ToString(), out var bb)) isCompleted = bb;
                }

                var course = new PlannedCourse
                {
                    PlannedCourseID = Convert.ToInt32(row["PlannedCourseID"]),
                    CourseCode = row["CourseCode"]?.ToString() ?? "",
                    CourseName = row["CourseName"]?.ToString() ?? "",
                    CreditHours = Convert.ToInt32(row["CreditHours"]),
                    IsCompleted = isCompleted
                };

                string term = row["PlannedTerm"]?.ToString() ?? "";
                int year = Convert.ToInt32(row["PlannedYear"]);

                var semester = Semesters.FirstOrDefault(s => s.Term == term && s.Year == year);
                semester?.Courses.Add(course);
            }
        }

        private void LoadAvailableCourses()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            // Keep your logic, but parameterize it.
            // (This lists courses not completed yet. It does not exclude planned courses—optional.)
            string query = @"
SELECT DISTINCT
    c.CourseID,
    c.CourseCode,
    c.CourseName,
    c.CreditHours,
    c.Department,
    dr.RequirementCategory
FROM Courses c
JOIN DegreeRequirements dr ON c.CourseID = dr.CourseID
JOIN DegreePrograms dp ON dr.DegreeID = dp.DegreeID
JOIN Students s ON dp.DegreeName = s.Major
WHERE s.StudentID = @sid
  AND c.IsActive = 1
  AND c.CourseID NOT IN (
      SELECT CourseID
      FROM StudentCourseHistory
      WHERE StudentID = @sid
        AND Status = 'Completed'
  )
ORDER BY c.CourseCode
LIMIT 30;";

            AvailableCourses = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out _);
        }
    }

    public class SemesterPlan
    {
        public string Term { get; set; } = string.Empty;
        public int Year { get; set; }
        public List<PlannedCourse> Courses { get; set; } = new();
        public int TotalCredits => Courses.Sum(c => c.CreditHours);
    }

    public class PlannedCourse
    {
        public int PlannedCourseID { get; set; }
        public string CourseCode { get; set; } = string.Empty;
        public string CourseName { get; set; } = string.Empty;
        public int CreditHours { get; set; }
        public bool IsCompleted { get; set; }
    }
}