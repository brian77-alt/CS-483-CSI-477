using MailKit.Net.Smtp;
using MimeKit;

namespace CS_483_CSI_477.Services
{
    public class EmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// Send email verification link
        public async Task<bool> SendVerificationEmailAsync(string toEmail, string toName, string verificationToken)
        {
            var verificationLink = $"{(_configuration["AppUrl"] ?? "").TrimEnd('/')}/VerifyEmail?token={verificationToken}";
            var subject = "Verify Your Email - AI Academic Advisor";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>Welcome to AI Academic Advisor!</h2>
                    <p>Hi {toName},</p>
                    <p>Thank you for registering. Please verify your email address by clicking the link below:</p>
                    <p><a href='{verificationLink}' style='background-color: #007bff; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>Verify Email</a></p>
                    <p>Or copy and paste this link into your browser:</p>
                    <p>{verificationLink}</p>
                    <p>This link will expire in 24 hours.</p>
                    <p>If you didn't create this account, please ignore this email.</p>
                    <br>
                    <p>Best regards,<br>AI Academic Advisor Team</p>
                </body>
                </html>";

            return await SendEmailAsync(toEmail, toName, subject, body);
        }

        /// Send password reset link
        public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string toName, string resetToken)
        {
            var resetLink = $"{(_configuration["AppUrl"] ?? "").TrimEnd('/')}/ResetPassword?token={resetToken}";
            var subject = "Password Reset Request - AI Academic Advisor";
            var body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2>Password Reset Request</h2>
                    <p>Hi {toName},</p>
                    <p>We received a request to reset your password. Click the link below to create a new password:</p>
                    <p><a href='{resetLink}' style='background-color: #dc3545; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>Reset Password</a></p>
                    <p>Or copy and paste this link into your browser:</p>
                    <p>{resetLink}</p>
                    <p>This link will expire in 1 hour.</p>
                    <p>If you didn't request a password reset, please ignore this email and your password will remain unchanged.</p>
                    <br>
                    <p>Best regards,<br>AI Academic Advisor Team</p>
                </body>
                </html>";

            return await SendEmailAsync(toEmail, toName, subject, body);
        }

        /// email sending method
        private async Task<bool> SendEmailAsync(string toEmail, string toName, string subject, string htmlBody)
        {
            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(
                    _configuration["Email:FromName"] ?? "AI Academic Advisor",
                    _configuration["Email:FromAddress"] ?? "noreply@acadadvising.com"
                ));
                message.To.Add(new MailboxAddress(toName, toEmail));
                message.Subject = subject;

                var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();

                // For development, use Gmail SMTP or a test service like Mailtrap
                var smtpHost = _configuration["Email:SmtpHost"] ?? "smtp.gmail.com";
                var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
                var smtpUser = _configuration["Email:SmtpUser"];
                var smtpPass = _configuration["Email:SmtpPassword"];

                if (string.IsNullOrEmpty(smtpUser) || string.IsNullOrEmpty(smtpPass))
                {
                    _logger.LogWarning("Email credentials not configured. Email not sent.");
                    return false;
                }

                await client.ConnectAsync(smtpHost, smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(smtpUser, smtpPass);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation($"Email sent successfully to {toEmail}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send email to {toEmail}");
                return false;
            }
        }
    }
}