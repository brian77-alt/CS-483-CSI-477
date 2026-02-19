using AdvisorDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages
{
    public class LoginModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;

        [BindProperty]
        public string StudentID { get; set; } = "";

        [BindProperty]
        public string Password { get; set; } = "";

        public string ErrorMessage { get; set; } = "";

        public LoginModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public IActionResult OnGet()
        {
            // If already logged in, redirect to dashboard
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

            // Check if this is a student ID (all digits) or admin username
            bool isStudentID = StudentID.All(char.IsDigit);

            if (isStudentID)
            {
                // Student login
                string query = $@"
                    SELECT StudentID, FirstName, LastName, Password 
                    FROM Students 
                    WHERE StudentID = {StudentID}";

                var result = _dbHelper.ExecuteQuery(query, out _);

                if (result == null || result.Rows.Count == 0)
                {
                    ErrorMessage = "Invalid Student ID or Password.";
                    return Page();
                }

                var row = result.Rows[0];
                string dbPassword = row["Password"].ToString() ?? "";

                if (dbPassword != Password)
                {
                    ErrorMessage = "Invalid Student ID or Password.";
                    return Page();
                }

                // Student login successful
                HttpContext.Session.SetInt32("StudentID", int.Parse(StudentID));
                HttpContext.Session.SetString("StudentName", $"{row["FirstName"]} {row["LastName"]}");
                HttpContext.Session.SetString("Role", "Student");

                return RedirectToPage("/StudentDashboard");
            }
            else
            {
                // Admin login
                string query = $@"
                    SELECT AdminID, FirstName, LastName, Password 
                    FROM Admins 
                    WHERE Username = '{StudentID.Replace("'", "''")}'";

                var result = _dbHelper.ExecuteQuery(query, out _);

                if (result == null || result.Rows.Count == 0)
                {
                    ErrorMessage = "Invalid username or password.";
                    return Page();
                }

                var row = result.Rows[0];
                string dbPassword = row["Password"].ToString() ?? "";

                if (dbPassword != Password)
                {
                    ErrorMessage = "Invalid username or password.";
                    return Page();
                }

                // Admin login successful
                HttpContext.Session.SetInt32("AdminID", int.Parse(row["AdminID"].ToString()!));
                HttpContext.Session.SetString("StudentName", $"{row["FirstName"]} {row["LastName"]}");
                HttpContext.Session.SetString("Role", "Admin");

                return RedirectToPage("/AdminDashboard");
            }
        }
    }
}