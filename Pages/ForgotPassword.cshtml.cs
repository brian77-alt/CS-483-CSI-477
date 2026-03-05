using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages
{
    public class ForgotPasswordModel : PageModel
    {
        private readonly AuthenticationService _authService;
        private readonly EmailService _emailService;

        [BindProperty]
        public string Email { get; set; } = "";

        public string Message { get; set; } = "";
        public bool Success { get; set; }

        public ForgotPasswordModel(AuthenticationService authService, EmailService emailService)
        {
            _authService = authService;
            _emailService = emailService;
        }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                Message = "Please enter your email address.";
                Success = false;
                return Page();
            }

            var token = _authService.CreatePasswordResetToken(Email);

            if (string.IsNullOrEmpty(token))
            {
                Message = "If an account exists with this email, a password reset link has been sent.";
                Success = true;
                return Page();
            }

            await _emailService.SendPasswordResetEmailAsync(Email, "User", token);

            Message = "If an account exists with this email, a password reset link has been sent. Please check your inbox.";
            Success = true;

            return Page();
        }
    }
}