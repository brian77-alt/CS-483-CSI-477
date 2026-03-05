using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages
{
    public class ResetPasswordModel : PageModel
    {
        private readonly AuthenticationService _authService;

        [BindProperty]
        public string Token { get; set; } = "";

        [BindProperty]
        public string NewPassword { get; set; } = "";

        [BindProperty]
        public string ConfirmPassword { get; set; } = "";

        public string Message { get; set; } = "";
        public bool Success { get; set; }

        public ResetPasswordModel(AuthenticationService authService)
        {
            _authService = authService;
        }

        public IActionResult OnGet(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                Message = "Invalid reset link.";
                Success = false;
                return Page();
            }

            Token = token;
            return Page();
        }

        public IActionResult OnPost()
        {
            if (string.IsNullOrWhiteSpace(NewPassword) || string.IsNullOrWhiteSpace(ConfirmPassword))
            {
                Message = "Please fill in all fields.";
                Success = false;
                return Page();
            }

            if (NewPassword != ConfirmPassword)
            {
                Message = "Passwords do not match.";
                Success = false;
                return Page();
            }

            if (NewPassword.Length < 8)
            {
                Message = "Password must be at least 8 characters.";
                Success = false;
                return Page();
            }

            var result = _authService.ResetPassword(Token, NewPassword);
            Message = result.Message;
            Success = result.Success;

            return Page();
        }
    }
}