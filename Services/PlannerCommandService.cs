using AdvisorDb;
using MySql.Data.MySqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Services
{
    public sealed class PlannerCommandResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public sealed class PlannerCommandService
    {
        private readonly DatabaseHelper _db;

        public PlannerCommandService(DatabaseHelper db)
        {
            _db = db;
        }

        // =========================
        // PUBLIC COMMANDS
        // =========================
        public PlannerCommandResult AddPlannedCourse(int studentId, string courseCode, string term, int year)
        {
            courseCode = NormalizeCourseCode(courseCode);
            term = NormalizeTerm(term);

            var courseId = GetCourseId(courseCode, out var err1);
            if (!string.IsNullOrEmpty(err1)) return Fail(err1);
            if (courseId == null) return Fail($"Course code '{courseCode}' not found in Courses table.");

            var planId = GetOrCreatePlanId(studentId, out var err2);
            if (!string.IsNullOrEmpty(err2)) return Fail(err2);
            if (planId == null) return Fail("Could not find or create a plan for this student.");

            // Prevent duplicates
            var existsSql = @"
SELECT 1
FROM PlannedCourses
WHERE PlanID = @pid
  AND CourseID = @cid
  AND PlannedTerm = @term
  AND PlannedYear = @year
LIMIT 1;";

            var exists = _db.ExecuteQuery(existsSql, new[]
            {
                new MySqlParameter("@pid", MySqlDbType.Int32){ Value = planId.Value },
                new MySqlParameter("@cid", MySqlDbType.Int32){ Value = courseId.Value },
                new MySqlParameter("@term", MySqlDbType.VarChar){ Value = term },
                new MySqlParameter("@year", MySqlDbType.Int32){ Value = year },
            }, out var err3);

            if (!string.IsNullOrEmpty(err3)) return Fail(err3);
            if (exists != null && exists.Rows.Count > 0)
                return Fail($"'{courseCode}' is already planned for {term} {year}.");

            // YearInPlan is NOT NULL in your schema -> we must provide it.
            // We'll compute a reasonable number from StartYear if possible; otherwise default to 1.
            int yearInPlan = GetYearInPlan(studentId, year, out var errYip);
            if (!string.IsNullOrEmpty(errYip)) return Fail(errYip);

            var insertSql = @"
INSERT INTO PlannedCourses (PlanID, CourseID, PlannedTerm, PlannedYear, YearInPlan, IsCompleted)
VALUES (@pid, @cid, @term, @year, @yip, 0);";

            var rows = _db.ExecuteNonQuery(insertSql, new[]
            {
                new MySqlParameter("@pid", MySqlDbType.Int32){ Value = planId.Value },
                new MySqlParameter("@cid", MySqlDbType.Int32){ Value = courseId.Value },
                new MySqlParameter("@term", MySqlDbType.VarChar){ Value = term },
                new MySqlParameter("@year", MySqlDbType.Int32){ Value = year },
                new MySqlParameter("@yip", MySqlDbType.Int32){ Value = yearInPlan },
            }, out var err4);

            if (!string.IsNullOrEmpty(err4)) return Fail(err4);
            if (rows <= 0) return Fail("Insert failed (no rows affected).");

            return Ok($"✅ Added {courseCode} to your course planner for {term} {year}.");
        }

        public PlannerCommandResult RemovePlannedCourse(int studentId, string courseCode, string term, int year)
        {
            courseCode = NormalizeCourseCode(courseCode);
            term = NormalizeTerm(term);

            var courseId = GetCourseId(courseCode, out var err1);
            if (!string.IsNullOrEmpty(err1)) return Fail(err1);
            if (courseId == null) return Fail($"Course code '{courseCode}' not found.");

            var planId = GetOrCreatePlanId(studentId, out var err2);
            if (!string.IsNullOrEmpty(err2)) return Fail(err2);
            if (planId == null) return Fail("Could not find plan for this student.");

            var deleteSql = @"
DELETE FROM PlannedCourses
WHERE PlanID = @pid
  AND CourseID = @cid
  AND PlannedTerm = @term
  AND PlannedYear = @year;";

            var rows = _db.ExecuteNonQuery(deleteSql, new[]
            {
                new MySqlParameter("@pid", MySqlDbType.Int32){ Value = planId.Value },
                new MySqlParameter("@cid", MySqlDbType.Int32){ Value = courseId.Value },
                new MySqlParameter("@term", MySqlDbType.VarChar){ Value = term },
                new MySqlParameter("@year", MySqlDbType.Int32){ Value = year },
            }, out var err3);

            if (!string.IsNullOrEmpty(err3)) return Fail(err3);
            if (rows <= 0) return Fail($"Nothing removed. I couldn’t find {courseCode} in {term} {year}.");

            return Ok($"✅ Removed {courseCode} from {term} {year}.");
        }

        public PlannerCommandResult MovePlannedCourse(int studentId, string courseCode, string fromTerm, int fromYear, string toTerm, int toYear)
        {
            var rem = RemovePlannedCourse(studentId, courseCode, fromTerm, fromYear);
            if (!rem.Success) return rem;

            var add = AddPlannedCourse(studentId, courseCode, toTerm, toYear);
            if (!add.Success)
            {
                // restore original if add failed
                AddPlannedCourse(studentId, courseCode, fromTerm, fromYear);
                return Fail($"Move failed: {add.Message}");
            }

            return Ok($"✅ Moved {NormalizeCourseCode(courseCode)} from {NormalizeTerm(fromTerm)} {fromYear} to {NormalizeTerm(toTerm)} {toYear}.");
        }

        // =========================
        // PLAN LOOKUP / CREATE
        // =========================
        private int? GetOrCreatePlanId(int studentId, out string error)
        {
            error = "";

            // 1) Existing plan?
            var sel = @"
SELECT PlanID
FROM StudentDegreePlans
WHERE StudentID = @sid
  AND (IsActive = 1 OR IsActive IS NULL)
LIMIT 1;";

            var dt = _db.ExecuteQuery(sel, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out var err1);

            if (!string.IsNullOrEmpty(err1)) { error = err1; return null; }
            if (dt != null && dt.Rows.Count > 0)
                return Convert.ToInt32(dt.Rows[0]["PlanID"]);

            // 2) Need to create a plan -> required fields:
            // StudentID, DegreeID, StartTerm, StartYear, ExpectedGraduationTerm, ExpectedGraduationYear

            var degreeId = GetStudentDegreeId(studentId, out var err2);
            if (!string.IsNullOrEmpty(err2)) { error = err2; return null; }
            if (degreeId == null)
            {
                error = "Could not determine DegreeID for this student. Ensure Students.Major matches DegreePrograms.DegreeName.";
                return null;
            }

            // Choose a simple start term/year.
            // If EnrollmentYear exists, use it; otherwise current year.
            var startYear = GetStudentEnrollmentYearOrCurrent(studentId, out var err3);
            if (!string.IsNullOrEmpty(err3)) { error = err3; return null; }

            var startTerm = "Fall"; // safe default

            // ExpectedGrad: use Students.ExpectedGraduationDate if present; otherwise startYear+4 Spring
            var (gradTerm, gradYear) = GetExpectedGraduationFromStudent(studentId, startYear, out var err4);
            if (!string.IsNullOrEmpty(err4)) { error = err4; return null; }

            var planName = "My Plan";

            var ins = @"
INSERT INTO StudentDegreePlans
(StudentID, DegreeID, PlanName, StartTerm, StartYear, ExpectedGraduationTerm, ExpectedGraduationYear, IsActive, GeneratedByAI)
VALUES
(@sid, @did, @pname, @sterm, @syear, @gterm, @gyear, 1, 1);";

            var rows = _db.ExecuteNonQuery(ins, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId },
                new MySqlParameter("@did", MySqlDbType.Int32){ Value = degreeId.Value },
                new MySqlParameter("@pname", MySqlDbType.VarChar){ Value = planName },
                new MySqlParameter("@sterm", MySqlDbType.VarChar){ Value = startTerm },
                new MySqlParameter("@syear", MySqlDbType.Int32){ Value = startYear },
                new MySqlParameter("@gterm", MySqlDbType.VarChar){ Value = gradTerm },
                new MySqlParameter("@gyear", MySqlDbType.Int32){ Value = gradYear },
            }, out var err5);

            if (!string.IsNullOrEmpty(err5)) { error = err5; return null; }
            if (rows <= 0) { error = "Could not create StudentDegreePlans row."; return null; }

            // 3) Re-select PlanID
            var dt2 = _db.ExecuteQuery(sel, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out var err6);

            if (!string.IsNullOrEmpty(err6)) { error = err6; return null; }
            if (dt2 == null || dt2.Rows.Count == 0) { error = "Plan created but could not be reloaded."; return null; }

            return Convert.ToInt32(dt2.Rows[0]["PlanID"]);
        }

        private int? GetStudentDegreeId(int studentId, out string error)
        {
            error = "";
            var sql = @"
SELECT dp.DegreeID
FROM Students s
JOIN DegreePrograms dp ON s.Major = dp.DegreeName
WHERE s.StudentID = @sid
LIMIT 1;";

            var dt = _db.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) { error = err; return null; }
            if (dt == null || dt.Rows.Count == 0) return null;

            return Convert.ToInt32(dt.Rows[0]["DegreeID"]);
        }

        private int GetStudentEnrollmentYearOrCurrent(int studentId, out string error)
        {
            error = "";
            var sql = @"SELECT EnrollmentYear FROM Students WHERE StudentID = @sid LIMIT 1;";
            var dt = _db.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) { error = err; return DateTime.Now.Year; }
            if (dt == null || dt.Rows.Count == 0) return DateTime.Now.Year;

            var v = dt.Rows[0]["EnrollmentYear"];
            if (v == DBNull.Value) return DateTime.Now.Year;

            return int.TryParse(v.ToString(), out var y) && y > 1900 ? y : DateTime.Now.Year;
        }

        private (string term, int year) GetExpectedGraduationFromStudent(int studentId, int startYear, out string error)
        {
            error = "";
            var sql = @"SELECT ExpectedGraduationDate FROM Students WHERE StudentID = @sid LIMIT 1;";
            var dt = _db.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) { error = err; return ("Spring", startYear + 4); }
            if (dt == null || dt.Rows.Count == 0) return ("Spring", startYear + 4);

            var v = dt.Rows[0]["ExpectedGraduationDate"];
            if (v == DBNull.Value) return ("Spring", startYear + 4);

            if (DateTime.TryParse(v.ToString(), out var d))
            {
                // Map month to term (simple)
                var term = d.Month switch
                {
                    >= 1 and <= 5 => "Spring",
                    >= 6 and <= 8 => "Summer",
                    _ => "Fall"
                };
                return (term, d.Year);
            }

            return ("Spring", startYear + 4);
        }

        private int GetYearInPlan(int studentId, int plannedYear, out string error)
        {
            error = "";

            // Try to compute based on StartYear from StudentDegreePlans if it exists
            var sql = @"
SELECT StartYear
FROM StudentDegreePlans
WHERE StudentID = @sid
ORDER BY PlanID DESC
LIMIT 1;";

            var dt = _db.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@sid", MySqlDbType.Int32){ Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) { error = err; return 1; }
            if (dt == null || dt.Rows.Count == 0) return 1;

            var v = dt.Rows[0]["StartYear"];
            if (v == DBNull.Value) return 1;

            if (int.TryParse(v.ToString(), out var startYear) && startYear > 1900)
            {
                var yip = (plannedYear - startYear) + 1;
                return yip < 1 ? 1 : (yip > 6 ? 6 : yip);
            }

            return 1;
        }

        // =========================
        // HELPERS
        // =========================
        private int? GetCourseId(string courseCode, out string error)
        {
            error = "";
            var sql = @"SELECT CourseID FROM Courses WHERE CourseCode = @code LIMIT 1;";
            var dt = _db.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@code", MySqlDbType.VarChar){ Value = courseCode }
            }, out var err);

            if (!string.IsNullOrEmpty(err)) { error = err; return null; }
            if (dt == null || dt.Rows.Count == 0) return null;

            return Convert.ToInt32(dt.Rows[0]["CourseID"]);
        }

        private static PlannerCommandResult Ok(string msg) => new() { Success = true, Message = msg };
        private static PlannerCommandResult Fail(string msg) => new() { Success = false, Message = "❌ " + msg };

        private static string NormalizeTerm(string term)
        {
            term = (term ?? "").Trim().ToLowerInvariant();
            return term switch
            {
                "fall" => "Fall",
                "spring" => "Spring",
                "summer" => "Summer",
                _ => Title(term)
            };
        }

        private static string NormalizeCourseCode(string input)
        {
            var m = Regex.Match(input ?? "", @"\b([A-Z]{2,4})\s*(\d{3})\b", RegexOptions.IgnoreCase);
            if (!m.Success) return (input ?? "").Trim().ToUpperInvariant();
            return $"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}";
        }

        private static string Title(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Trim();
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        }
    }
}