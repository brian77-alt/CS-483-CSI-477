using AdvisorDb;
using MySql.Data.MySqlClient;

namespace CS_483_CSI_477.Services
{
    public class ConflictResult
    {
        public bool HasConflicts { get; set; }
        public List<string> Conflicts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string Message { get; set; } = "";
    }

    public class ConflictDetectionService
    {
        private readonly DatabaseHelper _dbHelper;

        public ConflictDetectionService(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Check if adding a course would create conflicts
        public ConflictResult CheckCourseConflicts(int studentId, string courseCode, string term, int year)
        {
            var result = new ConflictResult();

            // 1. Check if already taken
            var takenCheck = CheckAlreadyTaken(studentId, courseCode);
            if (!string.IsNullOrEmpty(takenCheck))
            {
                result.HasConflicts = true;
                result.Conflicts.Add(takenCheck);
            }

            // 2. Check if already planned
            var plannedCheck = CheckAlreadyPlanned(studentId, courseCode, term, year);
            if (!string.IsNullOrEmpty(plannedCheck))
            {
                result.HasConflicts = true;
                result.Conflicts.Add(plannedCheck);
            }

            // 3. Check if course is offered in that term
            var offeringCheck = CheckTermOffering(courseCode, term);
            if (!string.IsNullOrEmpty(offeringCheck))
            {
                result.Warnings.Add(offeringCheck);
            }

            // Build message
            if (result.HasConflicts)
            {
                result.Message = $"Cannot add {courseCode}:\n" + string.Join("\n", result.Conflicts);
            }
            else if (result.Warnings.Count > 0)
            {
                result.Message = $"Warning for {courseCode}:\n" + string.Join("\n", result.Warnings);
            }
            else
            {
                result.Message = $"{courseCode} can be added to {term} {year}.";
            }

            return result;
        }

        // Check if student has already completed this course
        private string CheckAlreadyTaken(int studentId, string courseCode)
        {
            var query = @"
                SELECT sch.Status, sch.Grade, sch.Term, sch.AcademicYear
                FROM StudentCourseHistory sch
                JOIN Courses c ON sch.CourseID = c.CourseID
                WHERE sch.StudentID = @studentId
                AND c.CourseCode = @courseCode
                AND sch.Status = 'Completed'
                LIMIT 1";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId },
                new MySqlParameter("@courseCode", MySqlDbType.VarChar) { Value = courseCode }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || result == null || result.Rows.Count == 0)
                return "";

            var row = result.Rows[0];
            var grade = row["Grade"]?.ToString() ?? "";
            var term = row["Term"]?.ToString() ?? "";
            var year = row["AcademicYear"]?.ToString() ?? "";

            return $"Already completed {courseCode} with grade {grade} in {term} {year}";
        }

        // Check if student has already planned this course
        private string CheckAlreadyPlanned(int studentId, string courseCode, string term, int year)
        {
            var query = @"
                SELECT pc.PlannedTerm, pc.PlannedYear
                FROM PlannedCourses pc
                JOIN Courses c ON pc.CourseID = c.CourseID
                JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
                WHERE sdp.StudentID = @studentId
                AND c.CourseCode = @courseCode
                AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
                LIMIT 1";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId },
                new MySqlParameter("@courseCode", MySqlDbType.VarChar) { Value = courseCode }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || result == null || result.Rows.Count == 0)
                return "";

            var row = result.Rows[0];
            var plannedTerm = row["PlannedTerm"]?.ToString() ?? "";
            var plannedYear = row["PlannedYear"]?.ToString() ?? "";

            // If already planned for same term/year, it's a duplicate
            if (plannedTerm.Equals(term, StringComparison.OrdinalIgnoreCase) && plannedYear == year.ToString())
                return $"Already planned for {term} {year}";

            // If planned for different term, it's a warning
            return $"Already planned for {plannedTerm} {plannedYear}";
        }

        // Check if course is typically offered in the requested term
        private string CheckTermOffering(string courseCode, string term)
        {
            var query = @"
                SELECT TypicalTermsOffered
                FROM Courses
                WHERE CourseCode = @courseCode
                LIMIT 1";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@courseCode", MySqlDbType.VarChar) { Value = courseCode }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || result == null || result.Rows.Count == 0)
                return "";

            var offered = result.Rows[0]["TypicalTermsOffered"];
            if (offered == DBNull.Value || string.IsNullOrEmpty(offered?.ToString()))
                return ""; // No offering data available

            var offeredTerms = offered.ToString() ?? "";

            // Check if requested term is in the offered terms
            if (!offeredTerms.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return $"{courseCode} is typically offered in {offeredTerms}, not {term}";
            }

            return "";
        }

        // Get all conflicts for a student's current plan
        public List<ConflictResult> GetAllPlanConflicts(int studentId)
        {
            var conflicts = new List<ConflictResult>();

            // Get all planned courses
            var query = @"
                SELECT 
                    c.CourseCode,
                    pc.PlannedTerm,
                    pc.PlannedYear
                FROM PlannedCourses pc
                JOIN Courses c ON pc.CourseID = c.CourseID
                JOIN StudentDegreePlans sdp ON pc.PlanID = sdp.PlanID
                WHERE sdp.StudentID = @studentId
                AND (sdp.IsActive = 1 OR sdp.IsActive IS NULL)
                ORDER BY pc.PlannedYear, pc.PlannedTerm";

            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var err);

            if (string.IsNullOrEmpty(err) && result != null)
            {
                foreach (System.Data.DataRow row in result.Rows)
                {
                    var code = row["CourseCode"]?.ToString() ?? "";
                    var term = row["PlannedTerm"]?.ToString() ?? "";
                    var year = Convert.ToInt32(row["PlannedYear"]);

                    var conflict = CheckCourseConflicts(studentId, code, term, year);
                    if (conflict.HasConflicts || conflict.Warnings.Count > 0)
                    {
                        conflicts.Add(conflict);
                    }
                }
            }

            return conflicts;
        }
    }
}