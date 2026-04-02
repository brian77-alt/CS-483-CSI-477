using AdvisorDb;
using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;
using System.Security.Cryptography;
using System.Text;
using BCrypt.Net;

namespace CS_483_CSI_477.Pages
{
    public class RegisterModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly ILogger<RegisterModel> _logger;
        private readonly EmailService _emailService;
        private readonly AuthenticationService _authService;

        [BindProperty] public string FirstName { get; set; } = "";
        [BindProperty] public string LastName { get; set; } = "";
        [BindProperty] public string StudentIDNumber { get; set; } = "";
        [BindProperty] public string Email { get; set; } = "";
        [BindProperty] public string Major { get; set; } = "";
        [BindProperty] public int EnrollmentYear { get; set; } = DateTime.Now.Year;
        [BindProperty] public string EnrollmentStatus { get; set; } = "Full-Time";
        [BindProperty] public string Password { get; set; } = "";
        [BindProperty] public string ConfirmPassword { get; set; } = "";

        public string ErrorMessage { get; set; } = "";
        public string SuccessMessage { get; set; } = "";

        public RegisterModel(DatabaseHelper dbHelper, ILogger<RegisterModel> logger, EmailService emailService, AuthenticationService authService)
        {
            _dbHelper = dbHelper;
            _logger = logger;
            _emailService = emailService;
            _authService = authService;
        }
        public IActionResult OnGet()
        {
            // Redirect already logged-in users
            if (HttpContext.Session.GetInt32("StudentID").HasValue)
                return RedirectToPage("/StudentDashboard");

            return Page();
        }

        public async Task<IActionResult> OnPost()
        {
            // Sanitize inputs
            FirstName = InputSanitizer.SanitizeGeneral(FirstName).Trim();
            LastName = InputSanitizer.SanitizeGeneral(LastName).Trim();
            StudentIDNumber = InputSanitizer.SanitizeGeneral(StudentIDNumber).Trim();
            Email = InputSanitizer.SanitizeGeneral(Email).Trim().ToLowerInvariant();
            Major = InputSanitizer.SanitizeGeneral(Major).Trim();
            Password = Password.Trim();
            ConfirmPassword = ConfirmPassword.Trim();

            // Validation
            if (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName))
            {
                ErrorMessage = "First and last name are required.";
                return Page();
            }

            if (string.IsNullOrWhiteSpace(StudentIDNumber))
            {
                ErrorMessage = "Student ID number is required.";
                return Page();
            }

            if (string.IsNullOrWhiteSpace(Email) || !Email.Contains("@"))
            {
                ErrorMessage = "A valid email address is required.";
                return Page();
            }

            if (!Email.EndsWith(".edu", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "You must register with a .edu email address.";
                return Page();
            }

            if (string.IsNullOrWhiteSpace(Password) || Password.Length < 8)
            {
                ErrorMessage = "Password must be at least 8 characters.";
                return Page();
            }

            if (Password != ConfirmPassword)
            {
                ErrorMessage = "Passwords do not match.";
                return Page();
            }

            if (EnrollmentYear < 2000 || EnrollmentYear > 2100)
            {
                ErrorMessage = "Please enter a valid enrollment year.";
                return Page();
            }

            // Check if email already exists
            var emailCheck = _dbHelper.ExecuteQuery(
                "SELECT StudentID FROM Students WHERE Email = @email LIMIT 1",
                new[] { new MySqlParameter("@email", MySqlDbType.VarChar) { Value = Email } },
                out var emailErr);

            if (!string.IsNullOrEmpty(emailErr))
            {
                ErrorMessage = "A database error occurred. Please try again.";
                _logger.LogError("Register email check error: {Err}", emailErr);
                return Page();
            }

            if (emailCheck != null && emailCheck.Rows.Count > 0)
            {
                ErrorMessage = "An account with that email already exists.";
                return Page();
            }

            // Check if Student ID number already exists
            var idCheck = _dbHelper.ExecuteQuery(
                "SELECT StudentID FROM Students WHERE StudentIDNumber = @idNum LIMIT 1",
                new[] { new MySqlParameter("@idNum", MySqlDbType.VarChar) { Value = StudentIDNumber } },
                out var idErr);

            if (idCheck != null && idCheck.Rows.Count > 0)
            {
                ErrorMessage = "An account with that Student ID number already exists.";
                return Page();
            }

            // Hash the password
            var passwordHash = HashPassword(Password);

            // Insert new student
            var insertSql = @"
                INSERT INTO Students 
                (StudentIDNumber, Email, EmailVerified, FirstName, LastName, PasswordHash, 
                 Major, EnrollmentYear, EnrollmentStatus, TotalCreditsEarned, CurrentGPA, IsActive, Password)
                VALUES 
                (@idNum, @email, 0, @firstName, @lastName, @passwordHash,
                 @major, @enrollYear, @enrollStatus, 0, 0.00, 1, @legacyPassword)";

            var rows = _dbHelper.ExecuteNonQuery(insertSql, new[]
            {
                new MySqlParameter("@idNum", MySqlDbType.VarChar)       { Value = StudentIDNumber },
                new MySqlParameter("@email", MySqlDbType.VarChar)       { Value = Email },
                new MySqlParameter("@firstName", MySqlDbType.VarChar)   { Value = FirstName },
                new MySqlParameter("@lastName", MySqlDbType.VarChar)    { Value = LastName },
                new MySqlParameter("@passwordHash", MySqlDbType.VarChar){ Value = passwordHash },
                new MySqlParameter("@major", MySqlDbType.VarChar)       { Value = string.IsNullOrEmpty(Major) ? (object)DBNull.Value : Major },
                new MySqlParameter("@enrollYear", MySqlDbType.Int32)    { Value = EnrollmentYear },
                new MySqlParameter("@enrollStatus", MySqlDbType.VarChar){ Value = EnrollmentStatus },
                new MySqlParameter("@legacyPassword", MySqlDbType.VarChar){ Value = "password123" }
            }, out var insertErr);

            if (!string.IsNullOrEmpty(insertErr))
            {
                ErrorMessage = "Failed to create account. Please try again.";
                _logger.LogError("Register insert error: {Err}", insertErr);
                return Page();
            }

            if (rows == 0)
            {
                ErrorMessage = "Account creation failed. Please try again.";
                return Page();
            }

            _logger.LogInformation("New student registered: {Email}", Email);

            // Get the new student's ID
            var idSql = "SELECT StudentID FROM Students WHERE Email = @email LIMIT 1";
            var idResult = _dbHelper.ExecuteQuery(idSql, new[]
            {
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = Email }
            }, out _);

            if (idResult != null && idResult.Rows.Count > 0)
            {
                int newStudentId = Convert.ToInt32(idResult.Rows[0]["StudentID"]);
                var token = _authService.CreateEmailVerificationToken(newStudentId);
                var fullName = $"{FirstName} {LastName}";
                await _emailService.SendVerificationEmailAsync(Email, fullName, token);
            }
            TempData["RegisterSuccess"] = "Account created! Please check your email to verify your account before signing in.";
            return RedirectToPage("/Login");
        }

        private static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
        }
    }
}
