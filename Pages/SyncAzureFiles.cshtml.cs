using AdvisorDb;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySql.Data.MySqlClient;

namespace CS_483_CSI_477.Pages
{
    public class SyncAzureFilesModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly IConfiguration _configuration;

        public List<string> Results { get; set; } = new();
        public int BulletinsAdded { get; set; }
        public int MinorsAdded { get; set; }
        public int CertificatesAdded { get; set; }

        public SyncAzureFilesModel(DatabaseHelper dbHelper, IConfiguration configuration)
        {
            _dbHelper = dbHelper;
            _configuration = configuration;
        }

        public IActionResult OnGet()
        {
            if (HttpContext.Session.GetString("Role") != "Admin")
                return RedirectToPage("/Login");

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (HttpContext.Session.GetString("Role") != "Admin")
                return RedirectToPage("/Login");

            var connStr = _configuration["AzureBlobStorage:ConnectionString"];
            if (string.IsNullOrEmpty(connStr))
            {
                Results.Add("Error: Azure connection string not configured");
                return Page();
            }

            var blobServiceClient = new BlobServiceClient(connStr);

            await SyncBulletins(blobServiceClient);
            await SyncMinors(blobServiceClient);
            await SyncCertificates(blobServiceClient);

            Results.Add($"Sync Complete! Added {BulletinsAdded} bulletins, {MinorsAdded} minors, {CertificatesAdded} certificates");

            return Page();
        }

        private async Task SyncBulletins(BlobServiceClient client)
        {
            var containerClient = client.GetBlobContainerClient("bulletins");
            var prefix = "2026/";
            var academicYear = 2026;

            await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
            {
                if (!blob.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(blob.Name);
                var filePath = $"https://acadadvising26.blob.core.windows.net/bulletins/{blob.Name}";
                var fileSize = blob.Properties.ContentLength ?? 0;

                var checkSql = "SELECT BulletinID FROM Bulletins WHERE FileName = @fileName AND BulletinYear = '2026-2027'";
                var exists = _dbHelper.ExecuteQuery(checkSql, new[]
                {
                    new MySqlParameter("@fileName", MySqlDbType.VarChar) { Value = fileName }
                }, out _);

                if (exists != null && exists.Rows.Count > 0)
                {
                    Results.Add($"Bulletin already exists: {fileName}");
                    continue;
                }

                var bulletinType = DetermineBulletinType(fileName);
                var bulletinCategory = DetermineBulletinCategory(fileName);

                var insertSql = @"
                    INSERT INTO Bulletins 
                    (AcademicYear, FileName, BulletinType, BulletinCategory, BulletinYear, FilePath, FileSize, IsActive, UploadDate)
                    VALUES 
                    (@academicYear, @fileName, @type, @category, '2026-2027', @filePath, @fileSize, 1, NOW())";

                var rows = _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
                    new MySqlParameter("@academicYear", MySqlDbType.Int32) { Value = academicYear },
                    new MySqlParameter("@fileName", MySqlDbType.VarChar) { Value = fileName },
                    new MySqlParameter("@type", MySqlDbType.VarChar) { Value = bulletinType },
                    new MySqlParameter("@category", MySqlDbType.VarChar) { Value = bulletinCategory },
                    new MySqlParameter("@filePath", MySqlDbType.VarChar) { Value = filePath },
                    new MySqlParameter("@fileSize", MySqlDbType.Int64) { Value = fileSize }
                }, out var err);

                if (string.IsNullOrEmpty(err) && rows > 0)
                {
                    BulletinsAdded++;
                    Results.Add($"Added bulletin: {fileName}");
                }
                else
                {
                    Results.Add($"Failed to add bulletin: {fileName} - {err}");
                }
            }
        }

        private async Task SyncMinors(BlobServiceClient client)
        {
            var containerClient = client.GetBlobContainerClient("minors");
            var academicYear = 2026;

            await foreach (var blob in containerClient.GetBlobsAsync())
            {
                if (!blob.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = blob.Name;
                var filePath = $"https://acadadvising26.blob.core.windows.net/minors/{blob.Name}";
                var fileSize = blob.Properties.ContentLength ?? 0;

                var checkSql = "SELECT BulletinID FROM Bulletins WHERE FileName = @fileName AND BulletinCategory = 'Minor'";
                var exists = _dbHelper.ExecuteQuery(checkSql, new[]
                {
                    new MySqlParameter("@fileName", MySqlDbType.VarChar) { Value = fileName }
                }, out _);

                if (exists != null && exists.Rows.Count > 0)
                {
                    Results.Add($"Minor already exists: {fileName}");
                    continue;
                }

                var insertSql = @"
                    INSERT INTO Bulletins 
                    (AcademicYear, FileName, BulletinType, BulletinCategory, BulletinYear, FilePath, FileSize, IsActive, UploadDate)
                    VALUES 
                    (@academicYear, @fileName, 'Undergraduate', 'Minor', '2026-2027', @filePath, @fileSize, 1, NOW())";

                var rows = _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
                    new MySqlParameter("@academicYear", MySqlDbType.Int32) { Value = academicYear },
                    new MySqlParameter("@fileName", MySqlDbType.VarChar) { Value = fileName },
                    new MySqlParameter("@filePath", MySqlDbType.VarChar) { Value = filePath },
                    new MySqlParameter("@fileSize", MySqlDbType.Int64) { Value = fileSize }
                }, out var err);

                if (string.IsNullOrEmpty(err) && rows > 0)
                {
                    MinorsAdded++;
                    Results.Add($"Added minor: {fileName}");
                }
                else
                {
                    Results.Add($"Failed to add minor: {fileName} - {err}");
                }
            }
        }

        private async Task SyncCertificates(BlobServiceClient client)
        {
            var containerClient = client.GetBlobContainerClient("certificates");
            var academicYear = 2026;

            await foreach (var blob in containerClient.GetBlobsAsync())
            {
                if (!blob.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = blob.Name;
                var filePath = $"https://acadadvising26.blob.core.windows.net/certificates/{blob.Name}";
                var fileSize = blob.Properties.ContentLength ?? 0;

                var checkSql = "SELECT BulletinID FROM Bulletins WHERE FileName = @fileName AND BulletinType = 'Certificate'";
                var exists = _dbHelper.ExecuteQuery(checkSql, new[]
                {
                    new MySqlParameter("@fileName", MySqlDbType.VarChar) { Value = fileName }
                }, out _);

                if (exists != null && exists.Rows.Count > 0)
                {
                    Results.Add($"Certificate already exists: {fileName}");
                    continue;
                }

                var insertSql = @"
                    INSERT INTO Bulletins 
                    (AcademicYear, FileName, BulletinType, BulletinCategory, BulletinYear, FilePath, FileSize, IsActive, UploadDate)
                    VALUES 
                    (@academicYear, @fileName, 'Certificate', 'Certificate', '2026-2027', @filePath, @fileSize, 1, NOW())";

                var rows = _dbHelper.ExecuteNonQuery(insertSql, new[]
                {
                    new MySqlParameter("@academicYear", MySqlDbType.Int32) { Value = academicYear },
                    new MySqlParameter("@fileName", MySqlDbType.VarChar) { Value = fileName },
                    new MySqlParameter("@filePath", MySqlDbType.VarChar) { Value = filePath },
                    new MySqlParameter("@fileSize", MySqlDbType.Int64) { Value = fileSize }
                }, out var err);

                if (string.IsNullOrEmpty(err) && rows > 0)
                {
                    CertificatesAdded++;
                    Results.Add($"Added certificate: {fileName}");
                }
                else
                {
                    Results.Add($"Failed to add certificate: {fileName} - {err}");
                }
            }
        }

        private string DetermineBulletinType(string fileName)
        {
            // Map to the same values used elsewhere in the app (e.g. 'Undergraduate')
            // to avoid INSERT errors when BulletinType is an ENUM in the DB.
            if (fileName.Contains("Minor", StringComparison.OrdinalIgnoreCase))
                return "Undergraduate";
            if (fileName.Contains("Certificate", StringComparison.OrdinalIgnoreCase))
                return "Certificate";
            if (fileName.Contains("Major", StringComparison.OrdinalIgnoreCase))
                return "Undergraduate";
            return "Undergraduate";
        }

        private string DetermineBulletinCategory(string fileName)
        {
            if (fileName.Contains("Minor", StringComparison.OrdinalIgnoreCase))
                return "Minor";
            if (fileName.Contains("Core", StringComparison.OrdinalIgnoreCase))
                return "Core39";
            return "Major";
        }
    }
}