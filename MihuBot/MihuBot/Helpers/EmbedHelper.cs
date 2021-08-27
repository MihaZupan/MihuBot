using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MihuBot.Helpers
{
    static class EmbedHelper
    {
        public static async Task SendEmbedAsync(string json, ISocketMessageChannel channel)
        {
            EmbedModel model = JsonConvert.DeserializeObject<EmbedModel>(json);
            EmbedModel.EmbedInfo embed = model.Embed;

            EmbedBuilder builder = new EmbedBuilder();

            if (embed != null)
            {
                if (embed.Color != null)
                    builder.WithColor(new Color(embed.Color.Value));

                if (!string.IsNullOrWhiteSpace(embed.Title))
                    builder.WithTitle(embed.Title);

                if (!string.IsNullOrWhiteSpace(embed.Description))
                    builder.WithDescription(embed.Description);

                if (!string.IsNullOrWhiteSpace(embed.Url))
                    builder.WithUrl(embed.Url);

                if (!string.IsNullOrWhiteSpace(embed.Timestamp))
                    builder.WithTimestamp(DateTimeOffset.Parse(embed.Timestamp));

                if (embed.Footer != null)
                {
                    EmbedFooterBuilder footer = new EmbedFooterBuilder();

                    if (!string.IsNullOrWhiteSpace(footer.Text))
                        footer.WithText(footer.Text);

                    if (!string.IsNullOrWhiteSpace(footer.IconUrl))
                        footer.WithIconUrl(footer.IconUrl);

                    builder.WithFooter(footer);
                }

                if (embed.Thumbnail != null)
                    builder.WithThumbnailUrl(embed.Thumbnail.Url);

                if (embed.Image != null)
                    builder.WithImageUrl(embed.Image.Url);

                if (embed.Author != null)
                {
                    EmbedAuthorBuilder author = new EmbedAuthorBuilder();

                    if (!string.IsNullOrWhiteSpace(embed.Author.Name))
                        author.WithName(embed.Author.Name);

                    if (!string.IsNullOrWhiteSpace(embed.Author.Url))
                        author.WithUrl(embed.Author.Url);

                    if (!string.IsNullOrWhiteSpace(embed.Author.IconUrl))
                        author.WithIconUrl(embed.Author.IconUrl);

                    builder.WithAuthor(author);
                }

                if (embed.Fields != null)
                {
                    List<EmbedFieldBuilder> fields = new List<EmbedFieldBuilder>();

                    foreach (var field in embed.Fields)
                    {
                        EmbedFieldBuilder embedFieldBuilder = new EmbedFieldBuilder();

                        if (!string.IsNullOrWhiteSpace(field.Name))
                            embedFieldBuilder.WithName(field.Name);

                        if (!string.IsNullOrWhiteSpace(field.Value))
                            embedFieldBuilder.WithValue(field.Value);

                        embedFieldBuilder.IsInline = field.Inline;

                        fields.Add(embedFieldBuilder);
                    }

                    builder.WithFields(fields);
                }
            }

            if (model.Embed is null && string.IsNullOrWhiteSpace(model.Content))
                return;

            await channel.SendMessageAsync(
                text: string.IsNullOrWhiteSpace(model.Content) ? null : model.Content,
                embed: model.Embed is null ? null : builder.Build());
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        private class EmbedModel
        {
            public string Content { get; set; }
            public EmbedInfo Embed { get; set; }

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class EmbedInfo
            {
                public uint? Color { get; set; }
                public string Title { get; set; }
                public string Description { get; set; }
                public string Url { get; set; }
                public string Timestamp { get; set; }
                public Footer Footer { get; set; }
                public EmbedUrl Thumbnail { get; set; }
                public EmbedUrl Image { get; set; }
                public Author Author { get; set; }
                public Field[] Fields { get; set; }
            }
            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class EmbedUrl
            {
                [JsonProperty(Required = Required.Always)]
                public string Url { get; set; }
            }
            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class Footer
            {
                public string IconUrl { get; set; }
                public string Text { get; set; }
            }
            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class Author
            {
                public string Name { get; set; }
                public string Url { get; set; }
                public string IconUrl { get; set; }
            }
            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class Field
            {
                public string Name { get; set; }
                public string Value { get; set; }
                public bool Inline { get; set; }
            }
        }
    }
}
