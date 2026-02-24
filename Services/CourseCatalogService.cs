using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Services
{
    public sealed class CatalogCourse
    {
        public string Code { get; set; } = "";   // "CS 311"
        public string Title { get; set; } = "";  // "Data Structures..."
        public string CreditsText { get; set; } = ""; // "3" or "1-3"
        public string Section { get; set; } = ""; // "Required" or "Elective"
        public int Number => TryGetNumber(Code);

        private static int TryGetNumber(string code)
        {
            var m = Regex.Match(code ?? "", @"\b\d{3}\b");
            return m.Success ? int.Parse(m.Value) : 0;
        }
    }

    public sealed class DegreePlanParseResult
    {
        public List<CatalogCourse> Required { get; set; } = new();
        public List<CatalogCourse> Electives { get; set; } = new();
        public int TotalCount => Required.Count + Electives.Count;
    }

    public class CourseCatalogService
    {
        // Matches lines like:
        // "CS 311 - Data Structures and Analysis of Algorithms Credits: 3"
        // "MATH 215 - Survey of Calculus Credits: 3 or"
        // "CS 499 - Projects in Computer Science Credits: 1-3"
        private static readonly Regex LineRx = new(
            @"^(?<dept>[A-Z]{2,4})\s*(?<num>\d{3})\s*-\s*(?<title>.+?)\s*Credits:\s*(?<cr>[\d\-]+)\s*(?:or)?\s*$",
            RegexOptions.Compiled);

        public DegreePlanParseResult ParseDegreePlanFromPdfPages(List<PdfPageText> pages)
        {
            var result = new DegreePlanParseResult();

            // Flatten all lines
            var lines = pages
                .SelectMany(p => (p.Text ?? "").Split('\n').Select(l => l.Trim()))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            // Track which section we are in (Required vs Electives)
            string currentSection = "";

            foreach (var raw in lines)
            {
                if (raw.Contains("Required Courses", StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = "Required";
                    continue;
                }

                if (raw.Contains("Directed Electives", StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = "Elective";
                    continue;
                }

                var m = LineRx.Match(raw);
                if (!m.Success) continue;

                var code = $"{m.Groups["dept"].Value.ToUpperInvariant()} {m.Groups["num"].Value}";
                var title = m.Groups["title"].Value.Trim();
                var credits = m.Groups["cr"].Value.Trim();

                var course = new CatalogCourse
                {
                    Code = code,
                    Title = title,
                    CreditsText = credits,
                    Section = string.IsNullOrEmpty(currentSection) ? "Unknown" : currentSection
                };

                if (currentSection == "Required")
                    result.Required.Add(course);
                else if (currentSection == "Elective")
                    result.Electives.Add(course);
                else
                {
                    // If section not detected, still keep it (some PDFs start mid-section)
                    result.Electives.Add(course);
                }
            }

            // Deduplicate by Code
            result.Required = result.Required
                .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            result.Electives = result.Electives
                .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return result;
        }

        public List<CatalogCourse> RecommendNextCourses(
            DegreePlanParseResult plan,
            HashSet<string> completed,
            int count = 5)
        {
            string norm(string s) => Regex.Replace((s ?? "").ToUpperInvariant(), @"\s+", " ").Trim();

            // Prefer remaining REQUIRED first
            var remainingRequired = plan.Required
                .Where(c => !completed.Contains(norm(c.Code)))
                .OrderBy(c => c.Number)
                .ToList();

            if (remainingRequired.Count >= count)
                return remainingRequired.Take(count).ToList();

            // If not enough required, add electives too
            var remainingElectives = plan.Electives
                .Where(c => !completed.Contains(norm(c.Code)))
                .OrderBy(c => c.Code)
                .ToList();

            var combined = new List<CatalogCourse>();
            combined.AddRange(remainingRequired);
            combined.AddRange(remainingElectives);

            return combined.Take(count).ToList();
        }

        public static HashSet<string> ExtractCompletedCourseCodes(string studentContext)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in (studentContext ?? "").Split('\n'))
            {
                // matches "- CS 215:" or "- CS215:"
                var m = Regex.Match(line, @"-\s+([A-Z]{2,4})\s*(\d{3})\s*:", RegexOptions.IgnoreCase);
                if (m.Success)
                    set.Add($"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}");
            }

            return set;
        }
    }
}