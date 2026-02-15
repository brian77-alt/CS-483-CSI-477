using AdvisorDb;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Data;

namespace CS_483_CSI_477.Pages;

public class ProgressModel : PageModel
{
    private readonly DatabaseHelper _dbHelper;

    // Overall progress
    public string StudentName { get; set; } = string.Empty;
    public string Major { get; set; } = string.Empty;
    public decimal CurrentGPA { get; set; }
    public int TotalCreditsEarned { get; set; }
    public int TotalCreditsRequired { get; set; } = 120;
    public int CompletionPercentage { get; set; }

    // Course history
    public DataTable? CompletedCourses { get; set; }

    // Requirement breakdown
    public int BusinessCoreCredits { get; set; }
    public int CISCoreCredits { get; set; }
    public int ElectiveCredits { get; set; }
    public int Core39Credits { get; set; }

    // Dynamic labels for different majors
    public string CoreRequirement1Label { get; set; } = "Business Core";
    public int CoreRequirement1Credits { get; set; }
    public int CoreRequirement1Required { get; set; } = 34;

    public string CoreRequirement2Label { get; set; } = "CIS Core";
    public int CoreRequirement2Credits { get; set; }
    public int CoreRequirement2Required { get; set; } = 12;

    // For demo - hardcoded student ID
    private const int DEMO_STUDENT_ID = 1;

    public ProgressModel(DatabaseHelper dbHelper)
    {
        _dbHelper = dbHelper;
    }

    public void OnGet()
    {
        LoadStudentProgress();
        LoadCompletedCourses();
        LoadRequirementBreakdown();
    }

    private void LoadStudentProgress()
    {
        string query = $@"
            SELECT 
                CONCAT(s.FirstName, ' ', s.LastName) as FullName,
                s.Major,
                s.CurrentGPA,
                s.TotalCreditsEarned,
                dp.TotalCreditsRequired
            FROM Students s
            LEFT JOIN DegreePrograms dp ON s.Major = dp.DegreeName
            WHERE s.StudentID = {DEMO_STUDENT_ID}";

        var result = _dbHelper.ExecuteQuery(query, out _);

        if (result != null && result.Rows.Count > 0)
        {
            var row = result.Rows[0];
            StudentName = row["FullName"].ToString() ?? "Student";
            Major = row["Major"].ToString() ?? "Undeclared";
            CurrentGPA = decimal.Parse(row["CurrentGPA"].ToString() ?? "0");
            TotalCreditsEarned = int.Parse(row["TotalCreditsEarned"].ToString() ?? "0");

            if (row["TotalCreditsRequired"] != DBNull.Value)
                TotalCreditsRequired = int.Parse(row["TotalCreditsRequired"].ToString()!);

            CompletionPercentage = TotalCreditsRequired > 0
                ? (TotalCreditsEarned * 100 / TotalCreditsRequired)
                : 0;
        }
    }

    private void LoadCompletedCourses()
    {
        string query = $@"
            SELECT 
                c.CourseCode,
                c.CourseName,
                c.CreditHours,
                sch.Grade,
                sch.Term,
                sch.AcademicYear
            FROM StudentCourseHistory sch
            JOIN Courses c ON sch.CourseID = c.CourseID
            WHERE sch.StudentID = {DEMO_STUDENT_ID}
              AND sch.Status = 'Completed'
            ORDER BY sch.AcademicYear DESC, sch.Term DESC";

        CompletedCourses = _dbHelper.ExecuteQuery(query, out _);
    }

    private void LoadRequirementBreakdown()
    {
        // Get the student's degree program
        string degreeQuery = $@"
            SELECT dp.DegreeID, dp.DegreeCode
            FROM Students s
            JOIN DegreePrograms dp ON s.Major = dp.DegreeName
            WHERE s.StudentID = {DEMO_STUDENT_ID}";

        var degreeResult = _dbHelper.ExecuteQuery(degreeQuery, out _);

        if (degreeResult == null || degreeResult.Rows.Count == 0)
            return;

        int degreeId = int.Parse(degreeResult.Rows[0]["DegreeID"].ToString()!);
        string degreeCode = degreeResult.Rows[0]["DegreeCode"].ToString()!;

        // Calculate credits by category
        string breakdownQuery = $@"
            SELECT 
                dr.RequirementCategory,
                SUM(c.CreditHours) as EarnedCredits
            FROM StudentCourseHistory sch
            JOIN Courses c ON sch.CourseID = c.CourseID
            JOIN DegreeRequirements dr ON c.CourseID = dr.CourseID
            WHERE sch.StudentID = {DEMO_STUDENT_ID}
              AND dr.DegreeID = {degreeId}
              AND sch.Status = 'Completed'
            GROUP BY dr.RequirementCategory";

        var breakdown = _dbHelper.ExecuteQuery(breakdownQuery, out _);

        if (breakdown != null)
        {
            foreach (DataRow row in breakdown.Rows)
            {
                string category = row["RequirementCategory"].ToString() ?? "";
                int credits = int.Parse(row["EarnedCredits"].ToString() ?? "0");

                // For CIS majors
                if (degreeCode == "CIS-BS")
                {
                    if (category.Contains("Business Core"))
                        BusinessCoreCredits += credits;
                    else if (category.Contains("CIS Core"))
                        CISCoreCredits += credits;
                    else if (category.Contains("Track") || category.Contains("Elective"))
                        ElectiveCredits += credits;
                }
                // For CS majors - use same variables but different labels
                else if (degreeCode == "CS-BS")
                {
                    if (category.Contains("CS Core") || category.Contains("Programming") ||
                        category.Contains("Information Systems") || category.Contains("Hardware"))
                        BusinessCoreCredits += credits;
                    else if (category.Contains("Elective"))
                        ElectiveCredits += credits;
                }
            }
        }

        // Core 39 credits
        string core39Query = $@"
            SELECT SUM(c.CreditHours) as Core39Credits
            FROM StudentCourseHistory sch
            JOIN Courses c ON sch.CourseID = c.CourseID
            WHERE sch.StudentID = {DEMO_STUDENT_ID}
              AND c.Department IN ('English', 'Communication', 'Mathematics', 
                                    'Economics', 'Philosophy', 'Psychology')
              AND sch.Status = 'Completed'";

        var core39Result = _dbHelper.ExecuteQuery(core39Query, out _);
        if (core39Result != null && core39Result.Rows.Count > 0
            && core39Result.Rows[0]["Core39Credits"] != DBNull.Value)
        {
            Core39Credits = int.Parse(core39Result.Rows[0]["Core39Credits"].ToString()!);
        }

        // Set labels and requirements based on degree
        if (degreeCode == "CS-BS")
        {
            CoreRequirement1Label = "CS Core Requirements";
            CoreRequirement1Credits = BusinessCoreCredits;
            CoreRequirement1Required = 40;

            CoreRequirement2Label = "Advanced CS Courses";
            CoreRequirement2Credits = CISCoreCredits;
            CoreRequirement2Required = 12;
        }
        else // CIS-BS
        {
            CoreRequirement1Label = "Business Core";
            CoreRequirement1Credits = BusinessCoreCredits;
            CoreRequirement1Required = 34;

            CoreRequirement2Label = "CIS Core";
            CoreRequirement2Credits = CISCoreCredits;
            CoreRequirement2Required = 12;
        }
    }
}