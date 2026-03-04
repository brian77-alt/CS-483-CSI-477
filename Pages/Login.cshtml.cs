using AdvisorDb;
using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;

namespace CS_483_CSI_477.Pages
{
    public class LoginModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly AuthenticationService _authService;

        [BindProperty]
        public string StudentID { get; set; } = "";

        [BindProperty]
        public string Password { get; set; } = "";

        public string ErrorMessage { get; set; } = "";

        public LoginModel(DatabaseHelper dbHelper, AuthenticationService authService)
        {
            _dbHelper = dbHelper;
            _authService = authService;
        }

        public IActionResult OnGet()
        {
            if (HttpContext.Session.GetInt32("StudentID").HasValue ||
                HttpContext.Session.GetInt32("AdminID").HasValue)
            {
                string role = HttpContext.Session.GetString("Role") ?? "Student";
                return role == "Admin"
                    ? RedirectToPage("/AdminDashboard")
                    : RedirectToPage("/StudentDashboard");
            }

            return Page();
        }

        public IActionResult OnPost()
        {
            if (string.IsNullOrWhiteSpace(StudentID) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Please enter both login credentials and password.";
                return Page();
            }

            // Check if input is email or student ID
            bool isEmail = StudentID.Contains("@");

            if (isEmail)
            {
                // New email-based login with hashed passwords
                var authResult = _authService.Authenticate(StudentID, Password);

                if (!authResult.Success)
                {
                    ErrorMessage = authResult.Message;
                    return Page();
                }

                if (authResult.Role == "Student")
                {
                    HttpContext.Session.SetInt32("StudentID", authResult.StudentId!.Value);
                    HttpContext.Session.SetString("Role", "Student");
                    HttpContext.Session.Remove("StudentContextText");
                    return RedirectToPage("/StudentDashboard");
                }
                else if (authResult.Role == "Admin")
                {
                    HttpContext.Session.SetInt32("AdminID", authResult.StudentId!.Value);
                    HttpContext.Session.SetString("Role", "Admin");
                    HttpContext.Session.Remove("StudentID");
                    HttpContext.Session.Remove("StudentContextText");
                    return RedirectToPage("/AdminDashboard");
                }

                ErrorMessage = "Login failed.";
                return Page();
            }
            else
            {
                // Legacy login logic (Student ID or Username)
                bool isStudentID = StudentID.All(char.IsDigit);

                if (isStudentID)
                {
                    var sql = @"
                        SELECT StudentID, FirstName, LastName, Password
                        FROM Students
                        WHERE StudentID = @studentId
                        LIMIT 1;";

                    var dt = _dbHelper.ExecuteQuery(sql, new[]
                    {
                        new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = int.Parse(StudentID) }
                    }, out var err);

                    if (!string.IsNullOrEmpty(err))
                    {
                        ErrorMessage = err;
                        return Page();
                    }

                    if (dt == null || dt.Rows.Count == 0)
                    {
                        ErrorMessage = "Invalid Student ID or Password.";
                        return Page();
                    }

                    var row = dt.Rows[0];
                    var dbPassword = row["Password"]?.ToString() ?? "";

                    if (dbPassword != Password)
                    {
                        ErrorMessage = "Invalid Student ID or Password.";
                        return Page();
                    }

                    int sid = Convert.ToInt32(row["StudentID"]);
                    string fullName = $"{row["FirstName"]} {row["LastName"]}";

                    HttpContext.Session.SetInt32("StudentID", sid);
                    HttpContext.Session.SetString("StudentName", fullName);
                    HttpContext.Session.SetString("Role", "Student");
                    HttpContext.Session.Remove("StudentContextText");

                    return RedirectToPage("/StudentDashboard");
                }
                else
                {
                    var sql = @"
                        SELECT AdminID, FirstName, LastName, Password
                        FROM Admins
                        WHERE Username = @username
                        LIMIT 1;";

                    var dt = _dbHelper.ExecuteQuery(sql, new[]
                    {
                        new MySqlParameter("@username", MySqlDbType.VarChar) { Value = StudentID.Trim() }
                    }, out var err);

                    if (!string.IsNullOrEmpty(err))
                    {
                        ErrorMessage = err;
                        return Page();
                    }

                    if (dt == null || dt.Rows.Count == 0)
                    {
                        ErrorMessage = "Invalid username or password.";
                        return Page();
                    }

                    var row = dt.Rows[0];
                    string dbPassword = row["Password"]?.ToString() ?? "";

                    if (dbPassword != Password)
                    {
                        ErrorMessage = "Invalid username or password.";
                        return Page();
                    }

                    HttpContext.Session.SetInt32("AdminID", Convert.ToInt32(row["AdminID"]));
                    HttpContext.Session.SetString("StudentName", $"{row["FirstName"]} {row["LastName"]}");
                    HttpContext.Session.SetString("Role", "Admin");
                    HttpContext.Session.Remove("StudentID");
                    HttpContext.Session.Remove("StudentContextText");

                    return RedirectToPage("/AdminDashboard");
                }
            }
        }
    }
}