using AdvisorDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Data;

namespace CS_483_CSI_477.Pages;

public class AdminDashboardModel : PageModel
{
    private readonly DatabaseHelper _dbHelper;

    // Properties for database connection status
    public bool IsConnected { get; set; }
    public string ConnectionMessage { get; set; }
    public string ErrorMessage { get; set; }

    // Properties for student lookup
    [BindProperty]
    public string StudentId { get; set; }
    public DataTable StudentResults { get; set; }
    public string SearchMessage { get; set; }

    public AdminDashboardModel()
    {
        // Initialize database helper with Clever Cloud connection string
        string connectionString = "Server=bvvipshgowv49ljl7uni-mysql.services.clever-cloud.com;" +
                                 "Port=3306;" +
                                 "Database=bvvipshgowv49ljl7uni;" +
                                 "Uid=u6pui7n6rzvmricq;" +
                                 "Pwd=vz3U2cLHHqCttetUEkX2;" +
                                 "SslMode=Required;";

        _dbHelper = new DatabaseHelper(connectionString);
    }

    public void OnGet()
    {
        // Test database connection when page loads
        IsConnected = _dbHelper.TestConnection(out string error);

        if (IsConnected)
        {
            ConnectionMessage = "Database connected successfully";
        }
        else
        {
            ErrorMessage = $"Database connection failed: {error}";
        }
    }

    public IActionResult OnPostSearch()
    {
        // Test connection first
        IsConnected = _dbHelper.TestConnection(out string error);

        if (!IsConnected)
        {
            ErrorMessage = $"Database connection failed: {error}";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(StudentId))
        {
            SearchMessage = "Please enter a student ID";
            return Page();
        }

        // TODO: Replace 'students' with your actual table name
        // Example query - adjust based on your actual database schema
        string query = $"SELECT * FROM students WHERE student_id = '{StudentId}'";

        StudentResults = _dbHelper.ExecuteQuery(query, out string queryError);

        if (StudentResults != null && StudentResults.Rows.Count > 0)
        {
            SearchMessage = $"Found {StudentResults.Rows.Count} record(s)";
        }
        else if (!string.IsNullOrEmpty(queryError))
        {
            ErrorMessage = $"Search error: {queryError}";
        }
        else
        {
            SearchMessage = "No student found with that ID";
        }

        return Page();
    }

    public IActionResult OnPostUpload()
    {
        // Test connection first
        IsConnected = _dbHelper.TestConnection(out string error);

        if (!IsConnected)
        {
            ErrorMessage = $"Database connection failed: {error}";
            return Page();
        }

        // TODO: Implement file upload logic here
        SearchMessage = "Upload functionality - to be implemented";
        return Page();
    }
}