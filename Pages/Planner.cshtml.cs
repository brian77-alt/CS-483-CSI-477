using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CS_483_CSI_477.Pages;

public class PlannerModel : PageModel
{
    private readonly ILogger<PlannerModel> _logger;

    public PlannerModel(ILogger<PlannerModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        // Future: Load student's course plan from database
    }
}