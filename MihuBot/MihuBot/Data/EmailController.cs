using MihuBot.Helpers;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace MihuBot.Data
{
    [Route("[controller]/[action]")]
    public class EmailController : ControllerBase
    {
        private readonly Logger _logger;

        public EmailController(Logger logger)
        {
            _logger = logger;
        }

        [HttpPost]
        public async Task<OkResult> ReceiveDA07A01F888363A1D30F8236DD617302B3231E21BCA8CA79644820AF11835F73()
        {
            await _logger.DebugTextChannel.SendFileAsync(Request.Body, "Email.dump");
            return Ok();
        }
    }
}
