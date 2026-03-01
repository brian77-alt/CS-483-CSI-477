using MySql.Data.MySqlClient;
using System.Data;

namespace AdvisorDb
{
    public class DatabaseHelper
    {
        private readonly string _connectionString;

        public DatabaseHelper(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public bool TestConnection(out string errorMessage)
        {
            errorMessage = string.Empty;

            try
            {
                using var connection = new MySqlConnection(_connectionString);
                connection.Open();
                Console.WriteLine("✓ Database connection successful!");
                return true;
            }
            catch (MySqlException ex)
            {
                errorMessage = HandleMySqlException(ex);
                Console.WriteLine($"✗ MySQL Error: {errorMessage}");
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = $"Unexpected error: {ex.Message}";
                Console.WriteLine($"✗ {errorMessage}");
                return false;
            }
        }

        // Legacy (kept)
        public DataTable? ExecuteQuery(string query, out string errorMessage)
            => ExecuteQuery(query, Array.Empty<MySqlParameter>(), out errorMessage);

        // ✅ Parameterized query
        public DataTable? ExecuteQuery(string query, IEnumerable<MySqlParameter> parameters, out string errorMessage)
        {
            errorMessage = string.Empty;
            var dataTable = new DataTable();

            try
            {
                using var connection = new MySqlConnection(_connectionString);
                connection.Open();

                using var command = new MySqlCommand(query, connection);
                foreach (var p in parameters)
                    command.Parameters.Add(p);

                using var adapter = new MySqlDataAdapter(command);
                adapter.Fill(dataTable);

                return dataTable;
            }
            catch (MySqlException ex)
            {
                errorMessage = HandleMySqlException(ex);
                Console.WriteLine($"✗ Query Error: {errorMessage}");
                return null;
            }
            catch (Exception ex)
            {
                errorMessage = $"Unexpected error: {ex.Message}";
                Console.WriteLine($"✗ {errorMessage}");
                return null;
            }
        }

        // Legacy (kept)
        public int ExecuteNonQuery(string query, out string errorMessage)
            => ExecuteNonQuery(query, Array.Empty<MySqlParameter>(), out errorMessage);

        // ✅ Parameterized non-query
        public int ExecuteNonQuery(string query, IEnumerable<MySqlParameter> parameters, out string errorMessage)
        {
            errorMessage = string.Empty;

            try
            {
                using var connection = new MySqlConnection(_connectionString);
                connection.Open();

                using var command = new MySqlCommand(query, connection);
                foreach (var p in parameters)
                    command.Parameters.Add(p);

                return command.ExecuteNonQuery();
            }
            catch (MySqlException ex)
            {
                errorMessage = HandleMySqlException(ex);
                Console.WriteLine($"✗ Execute Error: {errorMessage}");
                return -1;
            }
            catch (Exception ex)
            {
                errorMessage = $"Unexpected error: {ex.Message}";
                Console.WriteLine($"✗ {errorMessage}");
                return -1;
            }
        }

        private string HandleMySqlException(MySqlException ex)
        {
            return ex.Number switch
            {
                0 => "Cannot connect to server. Check if the server is running and network is accessible.",
                1042 => "Unable to connect to MySQL server. Check host and port.",
                1045 => "Access denied. Check username and password.",
                1049 => "Database does not exist.",
                1146 => "Table does not exist.",
                1062 => "Duplicate entry - this record already exists.",
                1064 => "SQL syntax error. Check your query.",
                _ => $"MySQL Error ({ex.Number}): {ex.Message}"
            };
        }
    }
}