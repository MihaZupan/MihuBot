using Microsoft.AspNetCore.Mvc;

namespace MihuBot.Data;

[Route("[controller]/[action]")]
public class EmailController : ControllerBase
{
    private readonly DiscordSocketClient _discord;

    public EmailController(DiscordSocketClient discord)
    {
        _discord = discord;
    }

    [HttpPost]
    public async Task<OkResult> ReceiveDA07A01F888363A1D30F8236DD617302B3231E21BCA8CA79644820AF11835F73()
    {
        if (_discord.GetTextChannel(Channels.Email) is SocketTextChannel channel)
        {
            await channel.SendFileAsync(Request.Body, "Email.txt");
        }

        return Ok();
    }
}
