using System.Text;
using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Services
{
    public class DegreeWorksTranscript
    {
        public string StudentName { get; set; } = "";
        public string StudentId { get; set; } = "";
        public string Degree { get; set; } = "";
        public string Major { get; set; } = "";
        public decimal OverallGpa { get; set; }
        public int CreditsRequired { get; set; }
        public int CreditsApplied { get; set; }
        public string AcademicStanding { get; set; } = "";
        public string GraduationStatus { get; set; } = "";
        public List<DegreeWorksBlock> Blocks { get; set; } = new();
        public List<DegreeWorksCourse> AllCourses { get; set; } = new();
        public List<DegreeWorksCourse> TransferCourses { get; set; } = new();
        public List<DegreeWorksCourse> InProgressCourses { get; set; } = new();
        public List<DegreeWorksCourse> InsufficientCourses { get; set; } = new();
        public string RawSummary { get; set; } = "";
    }

    public class DegreeWorksBlock
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public int CreditsRequired { get; set; }
        public int CreditsApplied { get; set; }
    }

    public class DegreeWorksCourse
    {
        public string CourseCode { get; set; } = "";
        public string Title { get; set; } = "";
        public string Grade { get; set; } = "";
        public int Credits { get; set; }
        public string Term { get; set; } = "";
        public bool IsTransfer { get; set; }
        public bool IsInProgress { get; set; }
        public bool IsRepeat { get; set; }
        public bool IsAp { get; set; }
        public bool IsWithdrawn { get; set; }
        public string TransferId { get; set; } = "";
        public string TransferInstitution { get; set; } = "";
        public string Block { get; set; } = "";
    }

    public class DegreeWorksParserService
    {
        private readonly ILogger<DegreeWorksParserService> _logger;

        // Matches a full course row on one line produced by ExtractWithLines:
        // [optional label] COURSE_CODE. Title GRADE CREDITS TERM [Repeat]
        // Examples:
        //   "Composition I ENG 101. Rhet&Comp I:Literacy/Self TSC 3 Spring 2022"
        //   "CS 321. Architec Digitl Comput IP (3) Spring 2026"
        //   "ACCT 201. Accounting Princ I B 3 Fall 2023"
        private static readonly Regex CourseLineRx = new(
            @"([A-Z]{2,5}(?:\s+\d+[A-Z]?|-\d+-?[A-Z]{0,3}))\." +    // course code + period
            @"\s+" +
            @"(.+?)" +                                                  // title (non-greedy)
            @"\s+(IP|TA|TB|TC|TSC|TH|[A-D][+-]?|F|W|WF|AU)\s+" +     // grade
            @"\(?([\d]+)\)?\s+" +                                       // credits (optional parens)
            @"((?:First |Second )?(?:Spring|Fall|Summer)\s+\d{4})",    // term
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Transfer/AP line: "Satisfied by: POLS101 - Transferred Course - Ivy Tech..."
        private static readonly Regex TransferLineRx = new(
            @"Satisfied by:\s*([A-Z0-9]+)\s*-\s*(.+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Block header: "Core 39 COMPLETE" or "Major in Computer Science IN-PROGRESS"
        private static readonly Regex BlockHeaderRx = new(
            @"(Core 39|Bachelor of [A-Za-z\s]+?Skills Requirement|Embedded Experience|Major in [A-Za-z\s]+?|General Electives|Insufficient|In-progress)\s+(COMPLETE|IN-PROGRESS|INCOMPLETE)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CreditsLineRx = new(
            @"Credits required:\s*(\d+)\s+Credits applied:\s*(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public DegreeWorksParserService(ILogger<DegreeWorksParserService> logger)
        {
            _logger = logger;
        }

        public DegreeWorksTranscript Parse(string fullText)
        {
            var transcript = new DegreeWorksTranscript();

            try
            {
                var lines = fullText
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                ParseHeader(fullText, transcript);
                ParseBlocksAndCourses(lines, transcript);
                BuildSummaryLists(transcript);
                transcript.RawSummary = BuildAiSummary(transcript);

                _logger.LogInformation(
                    "DegreeWorks parsed: {Courses} courses, {Transfers} transfers, {InProgress} in-progress",
                    transcript.AllCourses.Count,
                    transcript.TransferCourses.Count,
                    transcript.InProgressCourses.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DegreeWorks parse error");
            }

            return transcript;
        }

        private void ParseHeader(string text, DegreeWorksTranscript t)
        {
            // Name and ID: "Brown, Ethan Austin - 000677084"
            var nameIdMatch = Regex.Match(text, @"([A-Za-z,\s]+)\s*-\s*(\d{6,10})");
            if (nameIdMatch.Success)
            {
                t.StudentName = nameIdMatch.Groups[1].Value.Trim();
                t.StudentId = nameIdMatch.Groups[2].Value.Trim();
            }

            // Overall GPA — "98% 100 % 3.133" or "Overall GPA\n3.133"
            var gpaMatch = Regex.Match(text, @"Overall GPA\s*\n\s*([\d.]+)");
            if (!gpaMatch.Success)
                gpaMatch = Regex.Match(text, @"\b([\d]\.\d{3})\b");
            if (gpaMatch.Success && decimal.TryParse(gpaMatch.Groups[1].Value, out var gpa))
                t.OverallGpa = gpa;

            // Degree type
            var degreeMatch = Regex.Match(text, @"Degree\s+(Bachelor of [A-Za-z\s]+?)(?:\n|Audit)");
            if (degreeMatch.Success)
                t.Degree = degreeMatch.Groups[1].Value.Trim();

            // Major from student info line
            var majorMatch = Regex.Match(text,
                @"Major\s+([A-Za-z\s]+?)\s+Academic Standing", RegexOptions.IgnoreCase);
            if (majorMatch.Success)
                t.Major = majorMatch.Groups[1].Value.Trim();

            // Academic Standing
            var standingMatch = Regex.Match(text,
                @"Academic Standing\s+([A-Za-z\s]+?)\s+Graduation", RegexOptions.IgnoreCase);
            if (standingMatch.Success)
                t.AcademicStanding = standingMatch.Groups[1].Value.Trim();

            // Degree-level credits — first occurrence
            var credMatch = CreditsLineRx.Match(text);
            if (credMatch.Success)
            {
                t.CreditsRequired = int.Parse(credMatch.Groups[1].Value);
                t.CreditsApplied = int.Parse(credMatch.Groups[2].Value);
            }
        }

        private static readonly HashSet<string> SubLabelRx = new(StringComparer.OrdinalIgnoreCase)
        {
            "Directed Electives", "General Electives"
        };

        private void ParseBlocksAndCourses(List<string> lines, DegreeWorksTranscript t)
        {
            // Pre-process: join lines where "Second" or "First" got split from "Summer YYYY"
            var joinedLines = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                var current = lines[i].TrimEnd();
                if (i + 1 < lines.Count)
                {
                    var next = lines[i + 1].Trim();
                    if ((current.EndsWith("Second", StringComparison.OrdinalIgnoreCase) ||
                         current.EndsWith("First", StringComparison.OrdinalIgnoreCase))
                        && Regex.IsMatch(next, @"^(Development\s+)?Summer\s+\d{4}$", RegexOptions.IgnoreCase))
                    {
                        // Strip "Development" prefix if present, keep only "Second/First Summer YYYY"
                        var termPart = Regex.Replace(next, @"^Development\s+", "", RegexOptions.IgnoreCase);
                        joinedLines.Add(current + " " + termPart);
                        i++;
                        continue;
                    }
                }
                joinedLines.Add(current);
            }
            lines = joinedLines;

            string currentBlock = "";
            string currentSubLabel = "";
            DegreeWorksCourse? lastCourse = null;

            foreach (var line in lines)
            {
                // Check for block header first
                var blockMatch = BlockHeaderRx.Match(line);
                if (blockMatch.Success)
                {
                    var blockName = blockMatch.Groups[1].Value.Trim();
                    var blockStatus = blockMatch.Groups[2].Value.Trim().ToUpperInvariant();
                    currentBlock = blockName;
                    currentSubLabel = "";

                    var block = new DegreeWorksBlock
                    {
                        Name = blockName,
                        Status = blockStatus
                    };

                    // Credits may be on same line: "Core 39 COMPLETE Credits required: 39..."
                    var credMatch = CreditsLineRx.Match(line);
                    if (credMatch.Success)
                    {
                        block.CreditsRequired = int.Parse(credMatch.Groups[1].Value);
                        block.CreditsApplied = int.Parse(credMatch.Groups[2].Value);
                    }

                    t.Blocks.Add(block);
                    continue;
                }

                // Credits line for current block (standalone line after block header)
                if (string.IsNullOrEmpty(currentBlock))
                {
                    var credMatch = CreditsLineRx.Match(line);
                    if (credMatch.Success && t.Blocks.Any())
                    {
                        var last = t.Blocks[^1];
                        if (last.CreditsRequired == 0)
                        {
                            last.CreditsRequired = int.Parse(credMatch.Groups[1].Value);
                            last.CreditsApplied = int.Parse(credMatch.Groups[2].Value);
                        }
                    }
                }

                // Check for sub-labels within a block (e.g. "Directed Electives", "General Electives")
                if (!blockMatch.Success && !TransferLineRx.IsMatch(line) && !CourseLineRx.IsMatch(line))
                {
                    var trimmed = line.Trim();
                    if (SubLabelRx.Contains(trimmed))
                    {
                        // Normalize to canonical name regardless of PDF casing
                        currentSubLabel = trimmed.ToLowerInvariant().Contains("general")
                            ? "General Electives"
                            : "Directed Electives";
                    }
                }

                // Check for transfer/AP line
                var transferMatch = TransferLineRx.Match(line);
                if (transferMatch.Success && lastCourse != null)
                {
                    var tid = transferMatch.Groups[1].Value.Trim();
                    var tdesc = transferMatch.Groups[2].Value.Trim();
                    bool isAp = tdesc.Contains("Advanced Placement", StringComparison.OrdinalIgnoreCase)
                             || tdesc.Contains("AP-", StringComparison.OrdinalIgnoreCase);

                    lastCourse.IsTransfer = true;
                    lastCourse.IsAp = isAp;
                    lastCourse.TransferId = tid;
                    lastCourse.TransferInstitution = tdesc;
                    continue;
                }

                // Check for course row
                var courseMatch = CourseLineRx.Match(line);
                if (courseMatch.Success)
                {
                    var grade = courseMatch.Groups[3].Value.Trim();
                    bool isIp = grade.Equals("IP", StringComparison.OrdinalIgnoreCase);
                    var course = new DegreeWorksCourse
                    {
                        CourseCode = courseMatch.Groups[1].Value.Trim(),
                        Title = courseMatch.Groups[2].Value.Trim(),
                        Grade = grade,
                        Credits = int.Parse(courseMatch.Groups[4].Value),
                        Term = courseMatch.Groups[5].Value.Trim(),
                        IsInProgress = isIp,
                        IsWithdrawn = grade.Equals("W", StringComparison.OrdinalIgnoreCase)
                                || grade.Equals("WF", StringComparison.OrdinalIgnoreCase),
                        Block = string.IsNullOrEmpty(currentSubLabel) ? currentBlock : currentSubLabel
                    };

                    t.AllCourses.Add(course);
                    lastCourse = course;
                }
            }
        }

        private void BuildSummaryLists(DegreeWorksTranscript t)
        {
            // Deduplicate by course code — keep first occurrence
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<DegreeWorksCourse>();
            foreach (var c in t.AllCourses)
            {
                if (seen.Add(c.CourseCode))
                    deduped.Add(c);
            }
            t.AllCourses = deduped;

            t.TransferCourses = t.AllCourses.Where(c => c.IsTransfer || c.IsAp).ToList();
            t.InProgressCourses = t.AllCourses.Where(c => c.IsInProgress).ToList();
            t.InsufficientCourses = t.AllCourses.Where(c => c.IsWithdrawn).ToList();
        }

        private string BuildAiSummary(DegreeWorksTranscript t)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== DEGREE WORKS TRANSCRIPT (uploaded by student) ===");
            sb.AppendLine($"Student: {t.StudentName} (ID: {t.StudentId})");
            sb.AppendLine($"Degree: {t.Degree}");
            sb.AppendLine($"Major: {t.Major}");
            sb.AppendLine($"Overall GPA: {t.OverallGpa:F3}");
            sb.AppendLine($"Credits Required: {t.CreditsRequired} | Credits Applied: {t.CreditsApplied}");
            sb.AppendLine($"Academic Standing: {t.AcademicStanding}");
            sb.AppendLine();

            if (t.Blocks.Any())
            {
                sb.AppendLine("--- Degree Requirement Blocks ---");
                foreach (var block in t.Blocks)
                    sb.AppendLine($"{block.Name} [{block.Status}] — {block.CreditsApplied}/{block.CreditsRequired} credits");
                sb.AppendLine();
            }

            if (t.TransferCourses.Any())
            {
                sb.AppendLine("--- Transfer and AP Credits ---");
                foreach (var c in t.TransferCourses)
                {
                    var tag = c.IsAp ? "[AP CREDIT]" : "[TRANSFER]";
                    sb.AppendLine($"- {c.CourseCode} ({c.Title}) | {c.Credits} cr | Grade: {c.Grade} | {c.Term} {tag}");
                    sb.AppendLine($"  Original: {c.TransferId} — {c.TransferInstitution}");
                    if (!string.IsNullOrEmpty(c.Block))
                        sb.AppendLine($"  Applied to: {c.Block}");
                }
                sb.AppendLine();
            }

            if (t.InProgressCourses.Any())
            {
                sb.AppendLine("--- Currently In Progress ---");
                foreach (var c in t.InProgressCourses)
                    sb.AppendLine($"- {c.CourseCode} ({c.Title}) | {c.Credits} cr | {c.Term} | Block: {c.Block}");
                sb.AppendLine();
            }

            if (t.InsufficientCourses.Any())
            {
                sb.AppendLine("--- Withdrawn / Insufficient ---");
                foreach (var c in t.InsufficientCourses)
                    sb.AppendLine($"- {c.CourseCode} ({c.Title}) | Grade: {c.Grade} | {c.Term}");
                sb.AppendLine();
            }

            sb.AppendLine("--- Completed Courses ---");
            var completedByBlock = t.AllCourses
                .Where(c => !c.IsInProgress && !c.IsWithdrawn)
                .GroupBy(c => string.IsNullOrEmpty(c.Block) ? "Other" : c.Block)
                .OrderBy(g => g.Key);

            foreach (var group in completedByBlock)
            {
                sb.AppendLine($"{group.Key}:");
                foreach (var c in group)
                {
                    var transferTag = c.IsTransfer ? $" [Transfer: {c.TransferId}]" : "";
                    var apTag = c.IsAp ? " [AP]" : "";
                    sb.AppendLine(
                        $"  - {c.CourseCode} | {c.Title} | Grade: {c.Grade} | {c.Credits} cr | {c.Term}{transferTag}{apTag}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== END DEGREE WORKS TRANSCRIPT ===");
            return sb.ToString();
        }
    }
}