using MySql.Data.MySqlClient;
using System;
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

        /// <summary>
        /// Test database connection with comprehensive error handling
        /// </summary>
        public bool TestConnection(out string errorMessage)
        {
            errorMessage = string.Empty;

            try
            {
                using (var connection = new MySqlConnection(_connectionString))
                {
                    connection.Open();
                    Console.WriteLine("✓ Database connection successful!");
                    return true;
                }
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

        /// <summary>
        /// Execute a query and return results with error handling
        /// </summary>
        public DataTable ExecuteQuery(string query, out string errorMessage)
        {
            errorMessage = string.Empty;
            DataTable dataTable = new DataTable();

            try
            {
                using (var connection = new MySqlConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = new MySqlCommand(query, connection))
                    {
                        using (var adapter = new MySqlDataAdapter(command))
                        {
                            adapter.Fill(dataTable);
                        }
                    }
                }
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

        /// <summary>
        /// Execute non-query commands (INSERT, UPDATE, DELETE) with error handling
        /// </summary>
        public int ExecuteNonQuery(string query, out string errorMessage)
        {
            errorMessage = string.Empty;

            try
            {
                using (var connection = new MySqlConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = new MySqlCommand(query, connection))
                    {
                        int rowsAffected = command.ExecuteNonQuery();
                        Console.WriteLine($"✓ {rowsAffected} row(s) affected");
                        return rowsAffected;
                    }
                }
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

        /// <summary>
        /// Handle MySQL-specific exceptions with user-friendly messages
        /// </summary>
        private string HandleMySqlException(MySqlException ex)
        {
            switch (ex.Number)
            {
                case 0:
                    return "Cannot connect to server. Check if the server is running and network is accessible.";
                case 1042:
                    return "Unable to connect to MySQL server. Check host and port.";
                case 1045:
                    return "Access denied. Check username and password.";
                case 1049:
                    return "Database does not exist.";
                case 1146:
                    return "Table does not exist.";
                case 1062:
                    return "Duplicate entry - this record already exists.";
                case 1064:
                    return "SQL syntax error. Check your query.";
                default:
                    return $"MySQL Error ({ex.Number}): {ex.Message}";
            }
        }
    }
}