using AdvisorDb;
using MySql.Data.MySqlClient;
using System.Data;

namespace CS_483_CSI_477.Services
{
    public class PrerequisiteCheckResult
    {
        public bool CanEnroll { get; set; }
        public List<string> MissingPrerequisites { get; set; } = new();
        public string Message { get; set; } = "";
    }

    public class PrerequisiteService
    {
        private readonly DatabaseHelper _dbHelper;

        public PrerequisiteService(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        /// Check if a student meets all prerequisites for a course
        public PrerequisiteCheckResult CheckPrerequisites(int studentId, string courseCode)
        {
            var result = new PrerequisiteCheckResult { CanEnroll = true };

            // Get the course ID
            var courseQuery = "SELECT CourseID FROM Courses WHERE CourseCode = @courseCode";
            var courseData = _dbHelper.ExecuteQuery(courseQuery, new[]
            {
                new MySqlParameter("@courseCode", MySqlDbType.VarChar) { Value = courseCode }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || courseData == null || courseData.Rows.Count == 0)
            {
                result.CanEnroll = false;
                result.Message = $"Course {courseCode} not found.";
                return result;
            }

            int courseId = Convert.ToInt32(courseData.Rows[0]["CourseID"]);

            // Get all prerequisites for this course
            var prereqQuery = @"
                SELECT 
                    c.CourseCode,
                    c.CourseName,
                    cp.PrerequisiteType,
                    cp.PrerequisiteGroup
                FROM CoursePrerequisites cp
                JOIN Courses c ON cp.PrerequisiteCourseID = c.CourseID
                WHERE cp.CourseID = @courseId
                ORDER BY cp.PrerequisiteGroup, c.CourseCode";

            var prereqs = _dbHelper.ExecuteQuery(prereqQuery, new[]
            {
                new MySqlParameter("@courseId", MySqlDbType.Int32) { Value = courseId }
            }, out var perr);

            // If no prerequisites, student can enroll
            if (string.IsNullOrEmpty(perr) && (prereqs == null || prereqs.Rows.Count == 0))
            {
                result.Message = $"No prerequisites required for {courseCode}.";
                return result;
            }

            // Get student's completed courses
            var completedQuery = @"
                SELECT c.CourseCode
                FROM StudentCourseHistory sch
                JOIN Courses c ON sch.CourseID = c.CourseID
                WHERE sch.StudentID = @studentId 
                AND sch.Status = 'Completed'
                AND sch.Grade NOT IN ('F', 'W', 'I')";

            var completed = _dbHelper.ExecuteQuery(completedQuery, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var cerr);

            var completedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(cerr) || completed != null)
            {
                foreach (DataRow row in completed.Rows)
                {
                    completedCodes.Add(row["CourseCode"].ToString() ?? "");
                }
            }

            // Check each prerequisite
            foreach (DataRow row in prereqs.Rows)
            {
                string prereqCode = row["CourseCode"].ToString() ?? "";
                string prereqName = row["CourseName"].ToString() ?? "";

                if (!completedCodes.Contains(prereqCode))
                {
                    result.MissingPrerequisites.Add($"{prereqCode} - {prereqName}");
                }
            }

            if (result.MissingPrerequisites.Count > 0)
            {
                result.CanEnroll = false;
                result.Message = $"Cannot enroll in {courseCode}. Missing prerequisites: {string.Join(", ", result.MissingPrerequisites)}";
            }
            else
            {
                result.Message = $"✓ All prerequisites met for {courseCode}.";
            }

            return result;
        }

        /// Get all prerequisites for a course (formatted for display)
        public string GetPrerequisitesDisplay(string courseCode)
        {
            var courseQuery = "SELECT CourseID FROM Courses WHERE CourseCode = @courseCode";
            var courseData = _dbHelper.ExecuteQuery(courseQuery, new[]
            {
                new MySqlParameter("@courseCode", MySqlDbType.VarChar) { Value = courseCode }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || courseData == null || courseData.Rows.Count == 0)
                return "None";

            int courseId = Convert.ToInt32(courseData.Rows[0]["CourseID"]);

            var prereqQuery = @"
                SELECT c.CourseCode
                FROM CoursePrerequisites cp
                JOIN Courses c ON cp.PrerequisiteCourseID = c.CourseID
                WHERE cp.CourseID = @courseId
                ORDER BY c.CourseCode";

            var prereqs = _dbHelper.ExecuteQuery(prereqQuery, new[]
            {
                new MySqlParameter("@courseId", MySqlDbType.Int32) { Value = courseId }
            }, out var perr);

            if (string.IsNullOrEmpty(perr) && prereqs != null && prereqs.Rows.Count > 0)
            {
                var codes = new List<string>();
                foreach (DataRow row in prereqs.Rows)
                {
                    codes.Add(row["CourseCode"].ToString() ?? "");
                }
                return string.Join(", ", codes);
            }

            return "None";
        }
    }
}
