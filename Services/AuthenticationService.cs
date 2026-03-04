using AdvisorDb;
using MySql.Data.MySqlClient;
using BCrypt.Net;

namespace CS_483_CSI_477.Services
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? StudentId { get; set; }
        public string? Role { get; set; }
    }

    public class AuthenticationService
    {
        private readonly DatabaseHelper _dbHelper;

        public AuthenticationService(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        /// <summary>
        /// Authenticate user with email and password
        /// </summary>
        public AuthResult Authenticate(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                return new AuthResult
                {
                    Success = false,
                    Message = "Email and password are required."
                };
            }

            // Check Students table first
            var studentQuery = @"
                SELECT StudentID, Email, PasswordHash 
                FROM Students 
                WHERE Email = @email AND IsActive = 1";

            var studentResult = _dbHelper.ExecuteQuery(studentQuery, new[]
            {
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email }
            }, out var err1);

            if (!string.IsNullOrEmpty(err1))
            {
                return new AuthResult { Success = false, Message = "Database error occurred." };
            }

            if (studentResult != null && studentResult.Rows.Count > 0)
            {
                var row = studentResult.Rows[0];
                var storedHash = row["PasswordHash"]?.ToString();

                if (string.IsNullOrEmpty(storedHash))
                {
                    return new AuthResult { Success = false, Message = "Account not properly configured." };
                }

                // Verify password against hash
                if (BCrypt.Net.BCrypt.Verify(password, storedHash))
                {
                    return new AuthResult
                    {
                        Success = true,
                        Message = "Login successful.",
                        StudentId = Convert.ToInt32(row["StudentID"]),
                        Role = "Student"
                    };
                }

                return new AuthResult { Success = false, Message = "Invalid email or password." };
            }

            // Check Admins table
            var adminQuery = @"
                SELECT AdminID, Email, PasswordHash 
                FROM Admins 
                WHERE Email = @email AND IsActive = 1";

            var adminResult = _dbHelper.ExecuteQuery(adminQuery, new[]
            {
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email }
            }, out var err2);

            if (!string.IsNullOrEmpty(err2))
            {
                return new AuthResult { Success = false, Message = "Database error occurred." };
            }

            if (adminResult != null && adminResult.Rows.Count > 0)
            {
                var row = adminResult.Rows[0];
                var storedHash = row["PasswordHash"]?.ToString();

                if (string.IsNullOrEmpty(storedHash))
                {
                    return new AuthResult { Success = false, Message = "Account not properly configured." };
                }

                if (BCrypt.Net.BCrypt.Verify(password, storedHash))
                {
                    return new AuthResult
                    {
                        Success = true,
                        Message = "Login successful.",
                        StudentId = Convert.ToInt32(row["AdminID"]),
                        Role = "Admin"
                    };
                }

                return new AuthResult { Success = false, Message = "Invalid email or password." };
            }

            return new AuthResult { Success = false, Message = "Invalid email or password." };
        }

        /// <summary>
        /// Hash a password using BCrypt
        /// </summary>
        public static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password, 12);
        }

        /// <summary>
        /// Verify a password against a hash
        /// </summary>
        public static bool VerifyPassword(string password, string hash)
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }

        /// <summary>
        /// Register a new student with hashed password
        /// </summary>
        public AuthResult RegisterStudent(string email, string password, string firstName, string lastName, string studentIdNumber)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {
                return new AuthResult { Success = false, Message = "Valid email is required." };
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                return new AuthResult { Success = false, Message = "Password must be at least 8 characters." };
            }

            // Check if email already exists
            var checkQuery = "SELECT StudentID FROM Students WHERE Email = @email";
            var existing = _dbHelper.ExecuteQuery(checkQuery, new[]
            {
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email }
            }, out var err);

            if (!string.IsNullOrEmpty(err))
            {
                return new AuthResult { Success = false, Message = "Database error occurred." };
            }

            if (existing != null && existing.Rows.Count > 0)
            {
                return new AuthResult { Success = false, Message = "Email already registered." };
            }

            // Hash password
            var passwordHash = HashPassword(password);

            // Insert new student
            var insertQuery = @"
                INSERT INTO Students 
                (StudentIDNumber, Email, FirstName, LastName, PasswordHash, Password, IsActive, CreatedAt)
                VALUES 
                (@idNumber, @email, @firstName, @lastName, @hash, @hash, 1, NOW())";

            var rows = _dbHelper.ExecuteNonQuery(insertQuery, new[]
            {
                new MySqlParameter("@idNumber", MySqlDbType.VarChar) { Value = studentIdNumber },
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email },
                new MySqlParameter("@firstName", MySqlDbType.VarChar) { Value = firstName },
                new MySqlParameter("@lastName", MySqlDbType.VarChar) { Value = lastName },
                new MySqlParameter("@hash", MySqlDbType.VarChar) { Value = passwordHash }
            }, out var insertErr);

            if (!string.IsNullOrEmpty(insertErr) || rows <= 0)
            {
                return new AuthResult { Success = false, Message = "Registration failed." };
            }

            return new AuthResult { Success = true, Message = "Registration successful! Please log in." };
        }
    }
}