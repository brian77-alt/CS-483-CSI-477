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

        /// Authenticate user with email and password
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

        /// Hash a password using BCrypt

        public static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password, 12);
        }

        /// Verify a password against a hash
        public static bool VerifyPassword(string password, string hash)
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }

        /// Register a new student with hashed password
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

        /// <summary>
        /// Generate email verification token
        /// </summary>
        public string CreateEmailVerificationToken(int studentId)
        {
            var token = Guid.NewGuid().ToString("N");
            var expiresAt = DateTime.Now.AddHours(24);

            var sql = @"
        INSERT INTO EmailVerificationTokens (StudentID, Token, ExpiresAt)
        VALUES (@studentId, @token, @expiresAt)";

            _dbHelper.ExecuteNonQuery(sql, new[]
            {
        new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId },
        new MySqlParameter("@token", MySqlDbType.VarChar) { Value = token },
        new MySqlParameter("@expiresAt", MySqlDbType.DateTime) { Value = expiresAt }
    }, out _);

            return token;
        }

        /// <summary>
        /// Verify email with token
        /// </summary>
        public AuthResult VerifyEmail(string token)
        {
            var sql = @"
        SELECT TokenID, StudentID, ExpiresAt, IsUsed
        FROM EmailVerificationTokens
        WHERE Token = @token
        LIMIT 1";

            var result = _dbHelper.ExecuteQuery(sql, new[]
            {
        new MySqlParameter("@token", MySqlDbType.VarChar) { Value = token }
    }, out var err);

            if (!string.IsNullOrEmpty(err) || result == null || result.Rows.Count == 0)
            {
                return new AuthResult { Success = false, Message = "Invalid verification token." };
            }

            var row = result.Rows[0];
            var isUsed = Convert.ToBoolean(row["IsUsed"]);
            var expiresAt = Convert.ToDateTime(row["ExpiresAt"]);
            var studentId = Convert.ToInt32(row["StudentID"]);
            var tokenId = Convert.ToInt32(row["TokenID"]);

            if (isUsed)
            {
                return new AuthResult { Success = false, Message = "This verification link has already been used." };
            }

            if (DateTime.Now > expiresAt)
            {
                return new AuthResult { Success = false, Message = "This verification link has expired." };
            }

            // Mark token as used
            var markUsedSql = "UPDATE EmailVerificationTokens SET IsUsed = 1 WHERE TokenID = @tokenId";
            _dbHelper.ExecuteNonQuery(markUsedSql, new[]
            {
        new MySqlParameter("@tokenId", MySqlDbType.Int32) { Value = tokenId }
    }, out _);

            // Mark email as verified
            var verifySql = "UPDATE Students SET EmailVerified = 1 WHERE StudentID = @studentId";
            _dbHelper.ExecuteNonQuery(verifySql, new[]
            {
        new MySqlParameter("@studentId", MySqlDbType.Int32) { Value = studentId }
    }, out _);

            return new AuthResult { Success = true, Message = "Email verified successfully! You can now log in." };
        }

        /// <summary>
        /// Create password reset token
        /// </summary>
        public string CreatePasswordResetToken(string email)
        {
            var token = Guid.NewGuid().ToString("N");
            var expiresAt = DateTime.Now.AddHours(1);

            // Check if email exists
            var checkSql = @"
        SELECT StudentID FROM Students WHERE Email = @email
        UNION
        SELECT AdminID FROM Admins WHERE Email = @email";

            var checkResult = _dbHelper.ExecuteQuery(checkSql, new[]
            {
        new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email }
    }, out _);

            if (checkResult == null || checkResult.Rows.Count == 0)
            {
                return ""; // Email not found
            }

            var sql = @"
        INSERT INTO PasswordResetTokens (Email, Token, ExpiresAt)
        VALUES (@email, @token, @expiresAt)";

            _dbHelper.ExecuteNonQuery(sql, new[]
            {
        new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email },
        new MySqlParameter("@token", MySqlDbType.VarChar) { Value = token },
        new MySqlParameter("@expiresAt", MySqlDbType.DateTime) { Value = expiresAt }
    }, out _);

            return token;
        }

        /// Reset password with token
        public AuthResult ResetPassword(string token, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            {
                return new AuthResult { Success = false, Message = "Password must be at least 8 characters." };
            }

            var sql = @"
                SELECT TokenID, Email, ExpiresAt, IsUsed
                FROM PasswordResetTokens
                WHERE Token = @token
                LIMIT 1";

            var result = _dbHelper.ExecuteQuery(sql, new[]
            {
                new MySqlParameter("@token", MySqlDbType.VarChar) { Value = token }
            }, out var err);

            if (!string.IsNullOrEmpty(err) || result == null || result.Rows.Count == 0)
            {
                return new AuthResult { Success = false, Message = "Invalid reset token." };
            }

            var row = result.Rows[0];
            var isUsed = Convert.ToBoolean(row["IsUsed"]);
            var expiresAt = Convert.ToDateTime(row["ExpiresAt"]);
            var email = row["Email"].ToString() ?? "";
            var tokenId = Convert.ToInt32(row["TokenID"]);

            if (isUsed)
            {
                return new AuthResult { Success = false, Message = "This reset link has already been used." };
            }

            if (DateTime.Now > expiresAt)
            {
                return new AuthResult { Success = false, Message = "This reset link has expired." };
            }

            // Hash new password
            var passwordHash = HashPassword(newPassword);

            // Update password for student or admin
            var updateStudentSql = "UPDATE Students SET PasswordHash = @hash WHERE Email = @email";
            _dbHelper.ExecuteNonQuery(updateStudentSql, new[]
            {
                new MySqlParameter("@hash", MySqlDbType.VarChar) { Value = passwordHash },
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email }
            }, out _);

            var updateAdminSql = "UPDATE Admins SET PasswordHash = @hash WHERE Email = @email";
            _dbHelper.ExecuteNonQuery(updateAdminSql, new[]
            {
                new MySqlParameter("@hash", MySqlDbType.VarChar) { Value = passwordHash },
                new MySqlParameter("@email", MySqlDbType.VarChar) { Value = email }
            }, out _);

            // Mark token as used
            var markUsedSql = "UPDATE PasswordResetTokens SET IsUsed = 1 WHERE TokenID = @tokenId";
            _dbHelper.ExecuteNonQuery(markUsedSql, new[]
            {
                new MySqlParameter("@tokenId", MySqlDbType.Int32) { Value = tokenId }
            }, out _);

            return new AuthResult { Success = true, Message = "Password reset successfully! You can now log in with your new password." };
        }

    }
}