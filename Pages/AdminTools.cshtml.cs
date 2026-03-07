using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages
{
    public class AdminToolsModel : PageModel
    {
        public IActionResult OnGet()
        {
            if (HttpContext.Session.GetString("Role") != "Admin")
                return RedirectToPage("/Login");

            return Page();
        }
    }
}