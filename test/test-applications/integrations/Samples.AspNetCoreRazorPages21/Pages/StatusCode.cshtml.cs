using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Samples.AspNetCoreMvc.Pages
{
    public class StatusCode : PageModel
    {
        public void OnGet(int value)
        {
            Response.StatusCode = value;
        }

        public void OnGetCustomHandler(int value)
        {            
            Response.StatusCode = value;
        }
    }
}
