using AdvisorDb;
using MySql.Data.MySqlClient;

namespace CS_483_CSI_477.Services
{
    public class AccountHoldService
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly IConfiguration _configuration;

        public AccountHoldService(DatabaseHelper dbHelper, IConfiguration configuration)
        {
            _dbHelper = dbHelper;
            _configuration = configuration;
        }

        public string GetActiveHoldsMessage(int studentId)
        {
            var query = @"
                SELECT HoldType, HoldReason, DocumentPath 
                FROM AccountHolds 
                WHERE StudentID = @studentId AND IsActive = 1
                ORDER BY PlacedDate DESC";

            var holds = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || holds == null || holds.Rows.Count == 0)
                return "";

            var messages = new List<string>();
            messages.Add("⚠️ ACCOUNT HOLDS DETECTED:");

            foreach (System.Data.DataRow row in holds.Rows)
            {
                var holdType = row["HoldType"].ToString();
                var reason = row["HoldReason"].ToString();
                messages.Add($"\n• {holdType} Hold: {reason}");
            }

            messages.Add("\n\n Please contact your academic advisor to resolve these holds before registering for classes.");

            return string.Join("", messages);
        }

        public bool HasActiveHolds(int studentId)
        {
            var query = "SELECT COUNT(*) as Count FROM AccountHolds WHERE StudentID = @studentId AND IsActive = 1";
            var result = _dbHelper.ExecuteQuery(query, new[]
            {
                new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
            }, out _);

            if (result == null || result.Rows.Count == 0)
                return false;

            return Convert.ToInt32(result.Rows[0]["Count"]) > 0;
        }
    }
}