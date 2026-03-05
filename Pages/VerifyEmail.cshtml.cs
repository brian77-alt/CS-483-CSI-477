using CS_483_CSI_477.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages
{
    public class VerifyEmailModel : PageModel
    {
        private readonly AuthenticationService _authService;

        public string Message { get; set; } = "";
        public bool Success { get; set; }

        public VerifyEmailModel(AuthenticationService authService)
        {
            _authService = authService;
        }

        public IActionResult OnGet(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                Message = "Invalid verification link.";
                Success = false;
                return Page();
            }

            var result = _authService.VerifyEmail(token);
            Message = result.Message;
            Success = result.Success;

            return Page();
        }
    }
}