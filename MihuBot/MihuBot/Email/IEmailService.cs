using SendGrid;

namespace MihuBot.Email
{
    public interface IEmailService
    {
        Task<Response> SendEmailAsync(string fromName, string fromEmailPrefix, string toName, string toEmail, string subject, string plainText, string htmlText);
    }
}
