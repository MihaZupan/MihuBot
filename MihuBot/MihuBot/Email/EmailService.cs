using MihuBot.Helpers;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace MihuBot.Email
{
    public sealed class EmailService : IEmailService
    {
        private readonly SendGridClient _emailClient;
        private readonly Logger _logger;

        public EmailService(HttpClient httpClient, Logger logger)
        {
            _emailClient = new SendGridClient(httpClient, Secrets.SendGrid.ApiKey);
            _logger = logger;
        }

        public async Task<Response> SendEmailAsync(string fromName, string fromEmailPrefix, string toName, string toEmail, string subject, string plainText, string htmlText)
        {
            try
            {
                var from = new EmailAddress($"{fromEmailPrefix}@darlings.me", fromName);
                var to = new EmailAddress(toEmail, toName);
                SendGridMessage msg = MailHelper.CreateSingleEmail(from, to, subject, plainText, htmlText);
                return await _emailClient.SendEmailAsync(msg);
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync($"{ex} for fromName={fromName} fromEmailPrefix={fromEmailPrefix} toName={toName} toEmail={toEmail} subject={subject} plainText={plainText} htmlText={htmlText}");
                throw;
            }
        }
    }
}
