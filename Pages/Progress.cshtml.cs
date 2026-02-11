using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages;

public class ProgressModel : PageModel
{
    private readonly ILogger<ProgressModel> _logger;

    public ProgressModel(ILogger<ProgressModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        // Future: Load student progress data from database
    }
}