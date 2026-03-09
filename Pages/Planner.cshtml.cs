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
        private readonly IConfiguration _configuration;

        public string StudentName { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;

        // This is the first year in the 4-year plan grid
        public int StartYear { get; set; } = DateTime.Now.Year;

        public List<SemesterPlan> Semesters { get; set; } = new();
        public DataTable? AvailableCourses { get; set; }

        [TempData]
        public string? StatusMessage { get; set; }

        [TempData]
        public string? StatusType { get; set; } // "success", "error", "warning"

        public PlannerModel(DatabaseHelper dbHelper, IConfiguration configuration)
        {
            _dbHelper = dbHelper;
            _configuration = configuration;
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

        public IActionResult OnPostAddCourse(int courseId, string term, int year)
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            if (courseId <= 0 || string.IsNullOrWhiteSpace(term) || year <= 0)
            {
                StatusMessage = "Invalid course data.";
                StatusType = "error";
                return RedirectToPage();
            }

            if (term != "Fall" && term != "Spring" && term != "Summer")
            {
                StatusMessage = "Invalid semester term.";
                StatusType = "error";
                return RedirectToPage();
            }

            LoadStudentInfo();

            try
            {
                using var conn = new MySqlConnection(GetConnectionString());
                conn.Open();

                // --- Term validation against TypicalTermsOffered ---
                string termCheckSql = @"
                    SELECT CourseCode, TypicalTermsOffered
                    FROM Courses
                    WHERE CourseID = @courseId AND IsActive = 1;";

                using (var termCmd = new MySqlCommand(termCheckSql, conn))
                {
                    termCmd.Parameters.AddWithValue("@courseId", courseId);
                    using var termReader = termCmd.ExecuteReader();
                    if (termReader.Read())
                    {
                        string courseCode = termReader["CourseCode"]?.ToString() ?? "";
                        string termsOffered = termReader["TypicalTermsOffered"]?.ToString() ?? "";

                        if (!string.IsNullOrWhiteSpace(termsOffered))
                        {
                            var offered = termsOffered
                                .Split(',')
                                .Select(t => t.Trim())
                                .ToList();

                            bool valid = offered.Any(t =>
                                t.Equals(term, StringComparison.OrdinalIgnoreCase));

                            if (!valid)
                            {
                                StatusMessage = $"{courseCode} is only offered in {termsOffered} and cannot be placed in a {term} semester.";
                                StatusType = "error";
                                return RedirectToPage();
                            }
                        }
                    }
                }

                // --- Duplicate check ---
                int planId = GetOrCreateActivePlanId(conn, studentId, term, year);

                string checkSql = @"
                    SELECT COUNT(*)
                    FROM PlannedCourses
                    WHERE PlanID = @planId
                      AND CourseID = @courseId;";

                using (var checkCmd = new MySqlCommand(checkSql, conn))
                {
                    checkCmd.Parameters.AddWithValue("@planId", planId);
                    checkCmd.Parameters.AddWithValue("@courseId", courseId);

                    int exists = Convert.ToInt32(checkCmd.ExecuteScalar());
                    if (exists > 0)
                    {
                        StatusMessage = "That course is already in your plan.";
                        StatusType = "warning";
                        return RedirectToPage();
                    }
                }

                int yearInPlan = GetYearInPlanForSemester(year);

                string insertSql = @"
                    INSERT INTO PlannedCourses
                    (PlanID, CourseID, PlannedTerm, PlannedYear, YearInPlan, IsCompleted)
                    VALUES
                    (@planId, @courseId, @term, @year, @yearInPlan, 0);";

                using (var insertCmd = new MySqlCommand(insertSql, conn))
                {
                    insertCmd.Parameters.AddWithValue("@planId", planId);
                    insertCmd.Parameters.AddWithValue("@courseId", courseId);
                    insertCmd.Parameters.AddWithValue("@term", term);
                    insertCmd.Parameters.AddWithValue("@year", year);
                    insertCmd.Parameters.AddWithValue("@yearInPlan", yearInPlan);

                    int rows = insertCmd.ExecuteNonQuery();

                    if (rows > 0)
                    {
                        StatusMessage = "Course added successfully.";
                        StatusType = "success";
                    }
                    else
                    {
                        StatusMessage = "Course was not added.";
                        StatusType = "error";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Add failed: {ex.Message}";
                StatusType = "error";
            }

            return RedirectToPage();
        }

        public IActionResult OnPostRemoveCourse(int plannedCourseId)
        {
            if (!HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/Login");

            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            if (plannedCourseId <= 0)
            {
                StatusMessage = "Invalid planned course id.";
                StatusType = "error";
                return RedirectToPage();
            }

            try
            {
                using var conn = new MySqlConnection(GetConnectionString());
                conn.Open();

                string deleteSql = @"
                    DELETE pc
                    FROM PlannedCourses pc
                    INNER JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
                    WHERE pc.PlannedCourseID = @plannedCourseId
                      AND sdp.StudentID = @studentId
                      AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
                      AND (pc.IsCompleted = 0 OR pc.IsCompleted IS NULL);";

                using var deleteCmd = new MySqlCommand(deleteSql, conn);
                deleteCmd.Parameters.AddWithValue("@plannedCourseId", plannedCourseId);
                deleteCmd.Parameters.AddWithValue("@studentId", studentId);

                int rows = deleteCmd.ExecuteNonQuery();

                StatusMessage = rows > 0 ? "Course removed." : "Course was not removed.";
                StatusType = rows > 0 ? "success" : "error";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Remove failed: {ex.Message}";
                StatusType = "error";
            }

            return RedirectToPage();
        }

        private string GetConnectionString()
        {
            var connStr =
                _configuration.GetConnectionString("DefaultConnection") ??
                _configuration["ConnectionStrings:DefaultConnection"];

            if (string.IsNullOrWhiteSpace(connStr))
                throw new Exception("DefaultConnection was not found.");

            return connStr;
        }

        private int GetOrCreateActivePlanId(MySqlConnection conn, int studentId, string term, int year)
        {
            string selectSql = @"
                SELECT PlanID
                FROM StudentDegreePlans
                WHERE StudentID = @studentId
                  AND (IsActive = 1 OR IsActive IS NULL)
                ORDER BY PlanID DESC
                LIMIT 1;";

            using (var selectCmd = new MySqlCommand(selectSql, conn))
            {
                selectCmd.Parameters.AddWithValue("@studentId", studentId);
                var existing = selectCmd.ExecuteScalar();

                if (existing != null && existing != DBNull.Value)
                    return Convert.ToInt32(existing);
            }

            var degreeInfo = GetStudentDegreeInfo(studentId);
            if (degreeInfo == null)
                throw new Exception("Could not determine DegreeID for this student.");

            string expectedGradTerm = "Spring";
            int expectedGradYear = degreeInfo.StartYear + 4;

            string insertSql = @"
                INSERT INTO StudentDegreePlans
                (StudentID, DegreeID, PlanName, StartTerm, StartYear, ExpectedGraduationTerm, ExpectedGraduationYear, IsActive, GeneratedByAI)
                VALUES
                (@studentId, @degreeId, @planName, @startTerm, @startYear, @expectedGradTerm, @expectedGradYear, 1, 1);";

            using (var insertCmd = new MySqlCommand(insertSql, conn))
            {
                insertCmd.Parameters.AddWithValue("@studentId", studentId);
                insertCmd.Parameters.AddWithValue("@degreeId", degreeInfo.DegreeID);
                insertCmd.Parameters.AddWithValue("@planName", "My Plan");
                insertCmd.Parameters.AddWithValue("@startTerm", degreeInfo.StartTerm);
                insertCmd.Parameters.AddWithValue("@startYear", degreeInfo.StartYear);
                insertCmd.Parameters.AddWithValue("@expectedGradTerm", expectedGradTerm);
                insertCmd.Parameters.AddWithValue("@expectedGradYear", expectedGradYear);

                int rows = insertCmd.ExecuteNonQuery();
                if (rows <= 0)
                    throw new Exception("Failed to create StudentDegreePlans row.");

                return Convert.ToInt32(insertCmd.LastInsertedId);
            }
        }

        private StudentDegreeInfo? GetStudentDegreeInfo(int studentId)
        {
            string query = @"
                SELECT 
                    dp.DegreeID,
                    s.EnrollmentYear
                FROM Students s
                JOIN DegreePrograms dp ON dp.DegreeName = s.Major
                WHERE s.StudentID = @sid
                LIMIT 1;";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out _);

            if (result == null || result.Rows.Count == 0)
                return null;

            var row = result.Rows[0];

            int degreeId = Convert.ToInt32(row["DegreeID"]);
            int startYear = DateTime.Now.Year;

            if (row["EnrollmentYear"] != DBNull.Value &&
                int.TryParse(row["EnrollmentYear"].ToString(), out int enrollmentYear) &&
                enrollmentYear > 1900)
            {
                startYear = enrollmentYear;
            }

            return new StudentDegreeInfo
            {
                DegreeID = degreeId,
                StartYear = startYear,
                StartTerm = "Fall"
            };
        }

        private int GetYearInPlanForSemester(int semesterYear)
        {
            int yearInPlan = semesterYear - StartYear + 1;

            if (yearInPlan < 1) yearInPlan = 1;
            if (yearInPlan > 4) yearInPlan = 4;

            return yearInPlan;
        }

        private void LoadStudentInfo()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

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

                var enrollVal = row["EnrollmentYear"];
                if (enrollVal != DBNull.Value &&
                    int.TryParse(enrollVal.ToString(), out var enrollYear) &&
                    enrollYear > 1900)
                {
                    StartYear = enrollYear;
                }
                else
                {
                    StartYear = DateTime.Now.Year;
                }
            }
        }

        private void LoadPlannedCourses()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

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

            string plannedQuery = @"
                SELECT 
                    pc.PlannedCourseID,
                    pc.CourseID,
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    c.TypicalTermsOffered,
                    pc.PlannedTerm,
                    pc.PlannedYear,
                    pc.IsCompleted
                FROM PlannedCourses pc
                JOIN Courses c ON pc.CourseID = c.CourseID
                JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
                WHERE sdp.StudentID = @sid
                  AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
                ORDER BY pc.PlannedYear,
                         CASE 
                            WHEN pc.PlannedTerm = 'Spring' THEN 1
                            WHEN pc.PlannedTerm = 'Summer' THEN 2
                            WHEN pc.PlannedTerm = 'Fall' THEN 3
                            ELSE 4
                         END,
                         c.CourseCode;";

            var plannedResult = _dbHelper.ExecuteQuery(plannedQuery, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out _);

            if (plannedResult != null)
            {
                foreach (DataRow row in plannedResult.Rows)
                {
                    bool isCompleted = false;
                    var ic = row["IsCompleted"];
                    if (ic != DBNull.Value)
                    {
                        if (ic is bool b) isCompleted = b;
                        else if (int.TryParse(ic.ToString(), out var n)) isCompleted = (n != 0);
                        else if (bool.TryParse(ic.ToString(), out var bb)) isCompleted = bb;
                    }

                    var course = new PlannedCourse
                    {
                        PlannedCourseID = Convert.ToInt32(row["PlannedCourseID"]),
                        CourseID = Convert.ToInt32(row["CourseID"]),
                        CourseCode = row["CourseCode"]?.ToString() ?? "",
                        CourseName = row["CourseName"]?.ToString() ?? "",
                        CreditHours = Convert.ToInt32(row["CreditHours"]),
                        TermsOffered = row["TypicalTermsOffered"]?.ToString() ?? "",
                        IsCompleted = isCompleted
                    };

                    string term = row["PlannedTerm"]?.ToString() ?? "";
                    int year = Convert.ToInt32(row["PlannedYear"]);

                    var semester = Semesters.FirstOrDefault(s => s.Term == term && s.Year == year);
                    semester?.Courses.Add(course);
                }
            }

            // Load completed courses from StudentCourseHistory
            string completedHistoryQuery = @"
                SELECT 
                    c.CourseID,
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    c.TypicalTermsOffered,
                    sch.Term AS PlannedTerm,
                    sch.AcademicYear AS PlannedYear
                FROM StudentCourseHistory sch
                JOIN Courses c ON sch.CourseID = c.CourseID
                WHERE sch.StudentID = @sid
                  AND sch.Status = 'Completed';";

            var historyResult = _dbHelper.ExecuteQuery(completedHistoryQuery, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out _);

            if (historyResult != null)
            {
                foreach (DataRow row in historyResult.Rows)
                {
                    int courseId = Convert.ToInt32(row["CourseID"]);
                    string term = row["PlannedTerm"]?.ToString() ?? "";
                    int year = Convert.ToInt32(row["PlannedYear"]);

                    var semester = Semesters.FirstOrDefault(s => s.Term == term && s.Year == year);
                    if (semester == null) continue;

                    bool alreadyExists = semester.Courses.Any(c => c.CourseID == courseId);
                    if (alreadyExists) continue;

                    semester.Courses.Add(new PlannedCourse
                    {
                        PlannedCourseID = 0,
                        CourseID = courseId,
                        CourseCode = row["CourseCode"]?.ToString() ?? "",
                        CourseName = row["CourseName"]?.ToString() ?? "",
                        CreditHours = Convert.ToInt32(row["CreditHours"]),
                        TermsOffered = row["TypicalTermsOffered"]?.ToString() ?? "",
                        IsCompleted = true
                    });
                }
            }
        }

        private void LoadAvailableCourses()
        {
            int studentId = HttpContext.Session.GetInt32("StudentID") ?? 0;

            string query = @"
                SELECT DISTINCT
                    c.CourseID,
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    c.Department,
                    c.TypicalTermsOffered,
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
                  AND c.CourseID NOT IN (
                      SELECT pc.CourseID
                      FROM PlannedCourses pc
                      JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
                      WHERE sdp.StudentID = @sid
                        AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
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
        public int CourseID { get; set; }
        public string CourseCode { get; set; } = string.Empty;
        public string CourseName { get; set; } = string.Empty;
        public int CreditHours { get; set; }
        public string TermsOffered { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
    }

    public class StudentDegreeInfo
    {
        public int DegreeID { get; set; }
        public string StartTerm { get; set; } = "Fall";
        public int StartYear { get; set; }
    }
}
