string token = await File.ReadAllTextAsync(@"C:\MihaZupan\MihuBot\MihuBot\LocalTesting\BotToken.txt");
using var client = new InitializedDiscordClient(new DiscordSocketConfig { }, TokenType.Bot, token);
await client.EnsureInitializedAsync();

client.InteractionCreated += async interaction =>
{
    switch (interaction.Type)
    {
        case InteractionType.MessageComponent:
            if (interaction is SocketMessageComponent messageComponent)
            {
                SocketMessageComponentData data = messageComponent.Data;

                if (data.Type == ComponentType.Button && data.CustomId.StartsWith("reminder-", StringComparison.Ordinal))
                {
                    await messageComponent.Message.ModifyAsync(m => m.Components = null);

                    switch (data.CustomId)
                    {
                        case "reminder-snooze-1h":
                            await messageComponent.FollowupAsync("You will be reminded again in 1 hour", ephemeral: true);
                            break;
                    }
                }
            }
            break;
    }
};

client.MessageReceived += async message =>
{
    if (message.Content.Contains("button", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var component = new ComponentBuilder()
                .WithButton("Got it", "reminder-confirm", ButtonStyle.Success)
                .WithButton("Snooze - 1 hour", "reminder-snooze-1h", ButtonStyle.Secondary)
                .WithButton("Snooze - 24 hours", "reminder-snooze-24h", ButtonStyle.Secondary)
                .Build();

            await message.Channel.SendMessageAsync(
                $"Some reminder {MentionUtils.MentionUser(message.Author.Id)}",
                component: component);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
};

await Task.Delay(-1);