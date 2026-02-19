using AdvisorDb;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace CS_483_CSI_477.Pages;

public class AdminDashboardModel : PageModel
{
    private readonly DatabaseHelper _dbHelper;
    private readonly IConfiguration _config;

    // Connection status
    public bool IsConnected { get; set; }
    public string ConnectionMessage { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    // Student lookup
    [BindProperty]
    public string StudentId { get; set; } = string.Empty;
    public DataTable? StudentResults { get; set; }
    public string SearchMessage { get; set; } = string.Empty;

    // Upload status
    public string UploadMessage { get; set; } = string.Empty;
    public bool UploadSuccess { get; set; }

    // Bulletin and document lists from DB
    public DataTable? Bulletins { get; set; }
    public DataTable? Documents { get; set; }

    // Upload form fields
    [BindProperty]
    public int BulletinYear { get; set; } = DateTime.Now.Year;
    [BindProperty]
    public string BulletinDescription { get; set; } = string.Empty;
    [BindProperty]
    public string DocType { get; set; } = "Syllabus";
    [BindProperty]
    public string CourseCode { get; set; } = string.Empty;
    [BindProperty]
    public string DocDescription { get; set; } = string.Empty;

    public AdminDashboardModel(DatabaseHelper dbHelper, IConfiguration config)
    {
        _dbHelper = dbHelper;
        _config = config;
    }

    public IActionResult OnGet()
    {
        // Redirect to login if not authenticated
        if (!HttpContext.Session.GetInt32("AdminID").HasValue)
        {
            return RedirectToPage("/Login");
        }

        // Check if user is admin
        string role = HttpContext.Session.GetString("Role") ?? "";
        if (role != "Admin")
        {
            return RedirectToPage("/StudentDashboard");
        }

        // Test connection and load data
        IsConnected = _dbHelper.TestConnection(out string error);
        ConnectionMessage = IsConnected ? "✓ Database connected successfully" : "";
        ErrorMessage = IsConnected ? "" : $"Database connection failed: {error}";

        if (IsConnected)
        {
            LoadBulletins();
            LoadDocuments();
        }

        return Page();
    }

    // ----------------------------------------
    // POST: Student Lookup
    // ----------------------------------------
    public IActionResult OnPostSearch()
    {
        IsConnected = _dbHelper.TestConnection(out string error);
        if (!IsConnected)
        {
            ErrorMessage = $"Connection failed: {error}";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(StudentId))
        {
            SearchMessage = "Please enter a Student ID.";
            LoadBulletins(); LoadDocuments();
            return Page();
        }

        string query = $@"
            SELECT s.StudentID, s.FirstName, s.LastName, s.Email,
                   s.Major, s.CurrentGPA, s.TotalCreditsEarned, s.EnrollmentStatus
            FROM Students s
            WHERE s.StudentID = '{MySqlEscape(StudentId)}'
               OR s.Email = '{MySqlEscape(StudentId)}'
            LIMIT 10";

        StudentResults = _dbHelper.ExecuteQuery(query, out string queryError);

        if (!string.IsNullOrEmpty(queryError))
            ErrorMessage = $"Search error: {queryError}";
        else if (StudentResults == null || StudentResults.Rows.Count == 0)
            SearchMessage = "No student found with that ID or email.";
        else
            SearchMessage = $"Found {StudentResults.Rows.Count} record(s).";

        LoadBulletins(); LoadDocuments();
        return Page();
    }

    // ----------------------------------------
    // POST: Upload Bulletin PDF to Azure Blob
    // ----------------------------------------
    public async Task<IActionResult> OnPostUploadBulletinAsync()
    {
        IsConnected = _dbHelper.TestConnection(out string error);
        if (!IsConnected)
        {
            ErrorMessage = $"Connection failed: {error}";
            return Page();
        }

        var file = Request.Form.Files["bulletinFile"];
        if (file == null || file.Length == 0)
        {
            UploadMessage = "Please select a PDF file to upload.";
            UploadSuccess = false;
            LoadBulletins(); LoadDocuments();
            return Page();
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".pdf")
        {
            UploadMessage = "Only PDF files are accepted for bulletins.";
            UploadSuccess = false;
            LoadBulletins(); LoadDocuments();
            return Page();
        }

        try
        {
            var azureConnStr = _config["AzureBlobStorage:ConnectionString"];
            var containerName = _config["AzureBlobStorage:BulletinContainer"] ?? "bulletins";

            string fileUrl;

            // If Azure not yet configured, save locally instead
            if (string.IsNullOrEmpty(azureConnStr) ||
                azureConnStr == "PASTE_YOUR_AZURE_CONNECTION_STRING_HERE")
            {
                fileUrl = await SaveLocalAsync(file, "bulletins", BulletinYear.ToString());
            }
            else
            {
                var blobClient = new BlobServiceClient(azureConnStr);
                var container = blobClient.GetBlobContainerClient(containerName);
                await container.CreateIfNotExistsAsync(PublicAccessType.None);

                var blobName = $"{BulletinYear}/Bulletin_{BulletinYear}_{Guid.NewGuid()}{ext}";
                var blob = container.GetBlobClient(blobName);
                using var stream = file.OpenReadStream();
                await blob.UploadAsync(stream, new BlobHttpHeaders
                {
                    ContentType = "application/pdf"
                });
                fileUrl = blob.Uri.ToString();
            }

            // Save metadata to MySQL
            string insert = $@"
                INSERT INTO Bulletins 
                    (AcademicYear, BulletinType, FileName, FilePath, FileSize, UploadedBy, Description)
                VALUES 
                    ({BulletinYear}, 'Undergraduate', '{MySqlEscape(file.FileName)}',
                     '{MySqlEscape(fileUrl)}', {file.Length}, 1,
                     '{MySqlEscape(BulletinDescription ?? $"Bulletin {BulletinYear}-{BulletinYear + 1}")}')";

            int rows = _dbHelper.ExecuteNonQuery(insert, out string dbError);

            if (rows > 0)
            {
                UploadMessage = $"✓ Bulletin for {BulletinYear} uploaded successfully!";
                UploadSuccess = true;
            }
            else
            {
                UploadMessage = $"File uploaded but DB save failed: {dbError}";
                UploadSuccess = false;
            }
        }
        catch (Exception ex)
        {
            UploadMessage = $"Upload failed: {ex.Message}";
            UploadSuccess = false;
        }

        LoadBulletins(); LoadDocuments();
        return Page();
    }

    // ----------------------------------------
    // POST: Upload Supporting Document
    // ----------------------------------------
    public async Task<IActionResult> OnPostUploadDocumentAsync()
    {
        IsConnected = _dbHelper.TestConnection(out string error);
        if (!IsConnected)
        {
            ErrorMessage = $"Connection failed: {error}";
            return Page();
        }

        var file = Request.Form.Files["documentFile"];
        if (file == null || file.Length == 0)
        {
            UploadMessage = "Please select a file to upload.";
            UploadSuccess = false;
            LoadBulletins(); LoadDocuments();
            return Page();
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".pdf", ".docx", ".doc", ".txt" };
        if (!allowed.Contains(ext))
        {
            UploadMessage = $"File type '{ext}' is not allowed.";
            UploadSuccess = false;
            LoadBulletins(); LoadDocuments();
            return Page();
        }

        try
        {
            var azureConnStr = _config["AzureBlobStorage:ConnectionString"];
            var containerName = _config["AzureBlobStorage:SupportingDocsContainer"]
                                ?? "supporting-docs";

            string fileUrl;

            if (string.IsNullOrEmpty(azureConnStr) ||
                azureConnStr == "PASTE_AZURE_KEY_HERE")
            {
                fileUrl = await SaveLocalAsync(file, "documents", DocType.ToLower());
            }
            else
            {
                var blobClient = new BlobServiceClient(azureConnStr);
                var container = blobClient.GetBlobContainerClient(containerName);
                await container.CreateIfNotExistsAsync(PublicAccessType.None);

                var prefix = string.IsNullOrEmpty(CourseCode)
                               ? "doc"
                               : CourseCode.Replace(" ", "_");
                var blobName = $"{DocType.ToLower()}/{prefix}_{Guid.NewGuid()}{ext}";
                var blob = container.GetBlobClient(blobName);
                using var stream = file.OpenReadStream();
                await blob.UploadAsync(stream, new BlobHttpHeaders
                {
                    ContentType = file.ContentType
                });
                fileUrl = blob.Uri.ToString();
            }

            string insert = $@"
                INSERT INTO SupportingDocuments 
                    (DocumentName, DocumentType, FilePath, FileSize, CourseCode, UploadedBy, Description)
                VALUES 
                    ('{MySqlEscape(file.FileName)}', '{MySqlEscape(DocType)}',
                     '{MySqlEscape(fileUrl)}', {file.Length},
                     '{MySqlEscape(CourseCode ?? "")}', 1,
                     '{MySqlEscape(DocDescription ?? DocType + " document")}')";

            int rows = _dbHelper.ExecuteNonQuery(insert, out string dbError);

            UploadMessage = rows > 0
                ? $"✓ Document uploaded successfully!"
                : $"File uploaded but DB save failed: {dbError}";
            UploadSuccess = rows > 0;
        }
        catch (Exception ex)
        {
            UploadMessage = $"Upload failed: {ex.Message}";
            UploadSuccess = false;
        }

        LoadBulletins(); LoadDocuments();
        return Page();
    }

    // ----------------------------------------
    // POST: Delete a Bulletin
    // ----------------------------------------
    public IActionResult OnPostDeleteBulletin(int id)
    {
        _dbHelper.ExecuteNonQuery(
            $"UPDATE Bulletins SET IsActive=0 WHERE BulletinID={id}", out _);
        return RedirectToPage();
    }

    // ----------------------------------------
    // POST: Delete a Document
    // ----------------------------------------
    public IActionResult OnPostDeleteDocument(int id)
    {
        _dbHelper.ExecuteNonQuery(
            $"UPDATE SupportingDocuments SET IsActive=0 WHERE DocumentID={id}", out _);
        return RedirectToPage();
    }

    // ----------------------------------------
    // Helpers
    // ----------------------------------------
    private void LoadBulletins()
    {
        Bulletins = _dbHelper.ExecuteQuery(
            @"SELECT BulletinID, AcademicYear, FileName, FileSize, UploadDate, Description 
              FROM Bulletins 
              WHERE IsActive=1 
              ORDER BY AcademicYear DESC",
            out _);
    }

    private void LoadDocuments()
    {
        Documents = _dbHelper.ExecuteQuery(
            @"SELECT DocumentID, DocumentName, DocumentType, CourseCode, FileSize, UploadDate 
              FROM SupportingDocuments 
              WHERE IsActive=1 
              ORDER BY UploadDate DESC",
            out _);
    }

    private async Task<string> SaveLocalAsync(
        IFormFile file, string folder, string subfolder)
    {
        var uploadPath = Path.Combine(
            Directory.GetCurrentDirectory(), "wwwroot", "uploads", folder, subfolder);
        Directory.CreateDirectory(uploadPath);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        var fullPath = Path.Combine(uploadPath, fileName);
        using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream);
        return $"/uploads/{folder}/{subfolder}/{fileName}";
    }

    private static string MySqlEscape(string input) =>
        input?.Replace("'", "''").Replace("\\", "\\\\") ?? string.Empty;
}