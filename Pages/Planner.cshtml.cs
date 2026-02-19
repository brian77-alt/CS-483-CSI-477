using AdvisorDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Data;

namespace CS_483_CSI_477.Pages
{
    public class PlannerModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;

        public string StudentName { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
        public int StartYear { get; set; } = 2024;

        // Planned courses organized by semester
        public List<SemesterPlan> Semesters { get; set; } = new();

        // Available courses to add
        public DataTable? AvailableCourses { get; set; }

        public PlannerModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public IActionResult OnGet()
        {
            // Redirect to login if not authenticated
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
            {
                return RedirectToPage("/Login");
            }

            LoadStudentInfo();
            LoadPlannedCourses();
            LoadAvailableCourses();
            return Page();
        }

        private void LoadStudentInfo()
        {
            // Get logged-in student ID from session
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            string query = $@"
                SELECT 
                    CONCAT(s.FirstName, ' ', s.LastName) as FullName,
                    s.Major,
                    s.ExpectedGraduationYear
                FROM Students s
                WHERE s.StudentID = {studentId}";

            var result = _dbHelper.ExecuteQuery(query, out _);

            if (result != null && result.Rows.Count > 0)
            {
                var row = result.Rows[0];
                StudentName = row["FullName"].ToString() ?? "Student";
                Major = row["Major"].ToString() ?? "Undeclared";

                if (row["ExpectedGraduationYear"] != DBNull.Value)
                    StartYear = int.Parse(row["ExpectedGraduationYear"].ToString()!) - 4;
                else
                    StartYear = DateTime.Now.Year;
            }
        }

        private void LoadPlannedCourses()
        {
            // Get logged-in student ID from session
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            // Initialize 8 semesters (4 years)
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

            // Load planned courses from database
            string query = $@"
                SELECT 
                    pc.PlannedCourseID,
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    pc.Term,
                    pc.AcademicYear,
                    pc.IsCompleted
                FROM PlannedCourses pc
                JOIN Courses c ON pc.CourseID = c.CourseID
                JOIN StudentDegreePlans sdp ON pc.DegreePlanID = sdp.DegreePlanID
                WHERE sdp.StudentID = {studentId}
                ORDER BY pc.AcademicYear, pc.Term";

            var result = _dbHelper.ExecuteQuery(query, out _);

            if (result != null)
            {
                foreach (DataRow row in result.Rows)
                {
                    var course = new PlannedCourse
                    {
                        PlannedCourseID = int.Parse(row["PlannedCourseID"].ToString()!),
                        CourseCode = row["CourseCode"].ToString()!,
                        CourseName = row["CourseName"].ToString()!,
                        CreditHours = int.Parse(row["CreditHours"].ToString()!),
                        IsCompleted = bool.Parse(row["IsCompleted"].ToString()!)
                    };

                    string term = row["Term"].ToString()!;
                    int year = int.Parse(row["AcademicYear"].ToString()!);

                    var semester = Semesters.FirstOrDefault(s =>
                        s.Term == term && s.Year == year);

                    semester?.Courses.Add(course);
                }
            }
        }

        private void LoadAvailableCourses()
        {
            // Get logged-in student ID from session
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            // Get major-specific required courses
            string query = $@"
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
                WHERE s.StudentID = {studentId}
                  AND c.IsActive = 1
                  AND c.CourseID NOT IN (
                      SELECT CourseID FROM StudentCourseHistory 
                      WHERE StudentID = {studentId} 
                      AND Status = 'Completed'
                  )
                ORDER BY c.CourseCode
                LIMIT 30";

            AvailableCourses = _dbHelper.ExecuteQuery(query, out _);
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