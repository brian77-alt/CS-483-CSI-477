using AdvisorDb;
using MySql.Data.MySqlClient;
using System.Text;
using System.Text.Json;

namespace CS_483_CSI_477.Services
{
    public class SupportingDocsRagService
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly PdfService _pdfService;
        private readonly PdfRagService _ragService;
        private readonly IConfiguration _config;
        private readonly ILogger<SupportingDocsRagService> _logger;

        public SupportingDocsRagService(
            DatabaseHelper dbHelper,
            PdfService pdfService,
            PdfRagService ragService,
            IConfiguration config,
            ILogger<SupportingDocsRagService> logger)
        {
            _dbHelper = dbHelper;
            _pdfService = pdfService;
            _ragService = ragService;
            _config = config;
            _logger = logger;
        }

        // Search across all relevant supporting documents
        public async Task<List<DocumentSearchResult>> SearchSupportingDocsAsync(
            string query,
            string? courseCode = null,
            string? documentYear = null,
            int maxDocuments = 5)
        {
            var results = new List<DocumentSearchResult>();

            try
            {
                // Build dynamic SQL to find relevant documents
                var conditions = new List<string> { "IsActive = 1" };
                var parameters = new List<MySqlParameter>();

                if (!string.IsNullOrEmpty(courseCode))
                {
                    conditions.Add("(CourseCode = @courseCode OR CourseCode IS NULL)");
                    parameters.Add(new MySqlParameter("@courseCode", MySqlDbType.VarChar) { Value = courseCode });
                }

                if (!string.IsNullOrEmpty(documentYear))
                {
                    conditions.Add("(DocumentYear = @documentYear OR DocumentYear IS NULL)");
                    parameters.Add(new MySqlParameter("@documentYear", MySqlDbType.VarChar) { Value = documentYear });
                }

                var whereClause = string.Join(" AND ", conditions);

                var sql = $@"
                    SELECT DocumentID, DocumentName, DocumentType, DocumentYear, 
                           CourseCode, FilePath, Description
                    FROM SupportingDocuments
                    WHERE {whereClause}
                    ORDER BY UploadDate DESC
                    LIMIT {maxDocuments * 2}";

                var docs = _dbHelper.ExecuteQuery(sql, parameters.ToArray(), out var err);

                if (!string.IsNullOrEmpty(err) || docs == null || docs.Rows.Count == 0)
                    return results;

                // Download and search each document
                foreach (System.Data.DataRow row in docs.Rows)
                {
                    var docId = Convert.ToInt32(row["DocumentID"]);
                    var docName = row["DocumentName"].ToString() ?? "";
                    var docType = row["DocumentType"].ToString() ?? "";
                    var docYear = row["DocumentYear"]?.ToString() ?? "N/A";
                    var docCourse = row["CourseCode"]?.ToString() ?? "";
                    var filePath = row["FilePath"].ToString() ?? "";

                    try
                    {
                        // Download and extract text from document
                        byte[] pdfBytes = await DownloadDocumentAsync(filePath);
                        if (pdfBytes == null || pdfBytes.Length == 0)
                            continue;

                        var extract = _pdfService.Extract(pdfBytes, docName, maxPages: 15, maxCharsTotal: 50_000);

                        // Use RAG to find relevant snippets
                        var hits = _ragService.FindTopRelevantSnippets(extract.Pages, query, topK: 2, snippetMaxChars: 400);

                        if (hits.Count > 0)
                        {
                            results.Add(new DocumentSearchResult
                            {
                                DocumentID = docId,
                                DocumentName = docName,
                                DocumentType = docType,
                                DocumentYear = docYear,
                                CourseCode = docCourse,
                                RelevantHits = hits
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to process document {docId}: {docName}");
                        continue;
                    }

                    // Limit results
                    if (results.Count >= maxDocuments)
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supporting docs search failed");
            }

            return results;
        }

        private async Task<byte[]?> DownloadDocumentAsync(string filePath)
        {
            try
            {
                // Local file path
                if (filePath.StartsWith("/uploads"))
                {
                    var localPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath.TrimStart('/'));
                    if (System.IO.File.Exists(localPath))
                        return await System.IO.File.ReadAllBytesAsync(localPath);
                    return null;
                }

                // Azure blob storage
                var azureConnStr = _config["AzureBlobStorage:ConnectionString"];
                if (string.IsNullOrEmpty(azureConnStr))
                    return null;

                var uri = new Uri(filePath);
                var pathParts = uri.AbsolutePath.TrimStart('/').Split('/', 2);

                if (pathParts.Length < 2)
                    return null;

                var containerName = pathParts[0];
                var blobName = pathParts[1];

                var blobServiceClient = new Azure.Storage.Blobs.BlobServiceClient(azureConnStr);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                using var ms = new MemoryStream();
                await blobClient.DownloadToAsync(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to download document from {filePath}");
                return null;
            }
        }
    }

    public class DocumentSearchResult
    {
        public int DocumentID { get; set; }
        public string DocumentName { get; set; } = "";
        public string DocumentType { get; set; } = "";
        public string DocumentYear { get; set; } = "";
        public string CourseCode { get; set; } = "";
        public List<RagHit> RelevantHits { get; set; } = new();
    }
}