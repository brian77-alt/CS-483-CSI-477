using AdvisorDb;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Data;

namespace CS_483_CSI_477.Pages;

public class StudentDashboardModel : PageModel
{
    private readonly DatabaseHelper _dbHelper;

    // Student info
    public string StudentName { get; set; } = string.Empty;
    public string Major { get; set; } = string.Empty;
    public decimal CurrentGPA { get; set; }
    public int TotalCreditsEarned { get; set; }
    public int CoursesCompleted { get; set; }
    public string EnrollmentStatus { get; set; } = string.Empty;

    // For demo purposes - hardcoded student ID
    // TODO: Replace with real authentication
    private const int DEMO_STUDENT_ID = 1;

    public StudentDashboardModel(DatabaseHelper dbHelper)
    {
        _dbHelper = dbHelper;
    }

    public void OnGet()
    {
        LoadStudentInfo();
    }

    private void LoadStudentInfo()
    {
        // Get student basic info
        string query = $@"
            SELECT 
                CONCAT(FirstName, ' ', LastName) as FullName,
                Major,
                CurrentGPA,
                TotalCreditsEarned,
                EnrollmentStatus
            FROM Students
            WHERE StudentID = {DEMO_STUDENT_ID}";

        var result = _dbHelper.ExecuteQuery(query, out _);

        if (result != null && result.Rows.Count > 0)
        {
            var row = result.Rows[0];
            StudentName = row["FullName"].ToString() ?? "Student";
            Major = row["Major"].ToString() ?? "Undeclared";
            CurrentGPA = decimal.Parse(row["CurrentGPA"].ToString() ?? "0");
            TotalCreditsEarned = int.Parse(row["TotalCreditsEarned"].ToString() ?? "0");
            EnrollmentStatus = row["EnrollmentStatus"].ToString() ?? "Active";
        }

        // Count completed courses
        string courseQuery = $@"
            SELECT COUNT(*) as CourseCount
            FROM StudentCourseHistory
            WHERE StudentID = {DEMO_STUDENT_ID}
                AND Status = 'Completed'";

        var courseResult = _dbHelper.ExecuteQuery(courseQuery, out _);
        if (courseResult != null && courseResult.Rows.Count > 0)
        {
            CoursesCompleted = int.Parse(courseResult.Rows[0]["CourseCount"].ToString() ?? "0");
        }
    }
}