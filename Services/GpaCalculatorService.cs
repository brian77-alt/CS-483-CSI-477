using AdvisorDb;
using MySql.Data.MySqlClient;

namespace CS_483_CSI_477.Services
{
    public class GpaCalculation
    {
        public decimal CurrentGpa { get; set; }
        public int TotalCredits { get; set; }
        public int CompletedCredits { get; set; }
        public decimal CumulativePoints { get; set; }
        public List<CourseGrade> Courses { get; set; } = new();
    }

    public class CourseGrade
    {
        public string CourseCode { get; set; } = "";
        public string CourseName { get; set; } = "";
        public int CreditHours { get; set; }
        public string Grade { get; set; } = "";
        public decimal GradePoints { get; set; }
        public string Term { get; set; } = "";
        public int Year { get; set; }
    }

    public class WhatIfResult
    {
        public decimal ProjectedGpa { get; set; }
        public int ProjectedCredits { get; set; }
        public string Message { get; set; } = "";
    }

    public class GpaCalculatorService
    {
        private readonly DatabaseHelper _dbHelper;

        public GpaCalculatorService(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Calculate current GPA from completed courses
        public GpaCalculation CalculateCurrentGpa(int studentId)
        {
            var result = new GpaCalculation();

            var query = @"
                SELECT 
                    c.CourseCode,
                    c.CourseName,
                    c.CreditHours,
                    sch.Grade,
                    sch.Term,
                    sch.AcademicYear
                FROM StudentCourseHistory sch
                JOIN Courses c ON sch.CourseID = c.CourseID
                WHERE sch.StudentID = @studentId
                AND sch.Status = 'Completed'
                AND sch.Grade NOT IN ('W', 'I', 'P')
                ORDER BY sch.AcademicYear DESC, sch.Term DESC";

            var data = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || data == null)
                return result;

            decimal totalPoints = 0;
            int totalCredits = 0;

            foreach (System.Data.DataRow row in data.Rows)
            {
                var grade = row["Grade"]?.ToString() ?? "";
                var credits = Convert.ToInt32(row["CreditHours"]);
                var gradePoints = GetGradePoints(grade);

                if (gradePoints >= 0) // Valid grade
                {
                    totalPoints += gradePoints * credits;
                    totalCredits += credits;

                    result.Courses.Add(new CourseGrade
                    {
                        CourseCode = row["CourseCode"]?.ToString() ?? "",
                        CourseName = row["CourseName"]?.ToString() ?? "",
                        CreditHours = credits,
                        Grade = grade,
                        GradePoints = gradePoints,
                        Term = row["Term"]?.ToString() ?? "",
                        Year = Convert.ToInt32(row["AcademicYear"])
                    });
                }
            }

            result.TotalCredits = totalCredits;
            result.CompletedCredits = totalCredits;
            result.CumulativePoints = totalPoints;
            result.CurrentGpa = totalCredits > 0 ? Math.Round(totalPoints / totalCredits, 2) : 0;

            return result;
        }

        // Calculate what-if GPA with hypothetical future courses

        public WhatIfResult CalculateWhatIfGpa(int studentId, List<(string courseCode, int credits, string grade)> futureCourses)
        {
            var current = CalculateCurrentGpa(studentId);

            decimal futurePoints = 0;
            int futureCredits = 0;

            foreach (var (code, credits, grade) in futureCourses)
            {
                var gradePoints = GetGradePoints(grade);
                if (gradePoints >= 0)
                {
                    futurePoints += gradePoints * credits;
                    futureCredits += credits;
                }
            }

            var totalPoints = current.CumulativePoints + futurePoints;
            var totalCredits = current.TotalCredits + futureCredits;

            var projectedGpa = totalCredits > 0 ? Math.Round(totalPoints / totalCredits, 2) : 0;

            return new WhatIfResult
            {
                ProjectedGpa = projectedGpa,
                ProjectedCredits = totalCredits,
                Message = $"Current GPA: {current.CurrentGpa} ({current.TotalCredits} credits) → Projected GPA: {projectedGpa} ({totalCredits} credits)"
            };
        }

        // Calculate GPA needed in remaining courses to reach target
        public string CalculateGpaNeeded(int studentId, decimal targetGpa, int remainingCredits)
        {
            var current = CalculateCurrentGpa(studentId);

            if (remainingCredits <= 0)
                return "No remaining credits to calculate.";

            var totalCreditsNeeded = current.TotalCredits + remainingCredits;
            var totalPointsNeeded = targetGpa * totalCreditsNeeded;
            var pointsNeeded = totalPointsNeeded - current.CumulativePoints;
            var gpaNeeded = pointsNeeded / remainingCredits;

            if (gpaNeeded > 4.0m)
                return $"Target GPA of {targetGpa} is not achievable even with perfect 4.0 in remaining {remainingCredits} credits.";

            if (gpaNeeded < 0)
                return $"You've already exceeded the target GPA of {targetGpa}! Current GPA: {current.CurrentGpa}";

            return $"To reach {targetGpa} GPA, you need an average of {Math.Round(gpaNeeded, 2)} in your next {remainingCredits} credits.";
        }

        // Convert letter grade to grade points (4.0 scale)
        private static decimal GetGradePoints(string grade)
        {
            return grade?.ToUpperInvariant() switch
            {
                "A" => 4.0m,
                "A-" => 3.7m,
                "B+" => 3.3m,
                "B" => 3.0m,
                "B-" => 2.7m,
                "C+" => 2.3m,
                "C" => 2.0m,
                "C-" => 1.7m,
                "D+" => 1.3m,
                "D" => 1.0m,
                "D-" => 0.7m,
                "F" => 0.0m,
                _ => -1 // Invalid/non-graded (W, I, P, etc.)
            };
        }
    }
}
