using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MihuBot.Helpers.TeamUp
{
    public sealed class TeamUpClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _calendarKey;
        private readonly int? _subCalendarId;

        public TeamUpClient(HttpClient httpClient, string apiKey, string calendarKey, int? subCalendarId = null)
        {
            _httpClient = httpClient;
            _apiKey = apiKey;
            _calendarKey = calendarKey;
            _subCalendarId = subCalendarId;
        }

        private async Task<T> MakeRequestAsync<T>(HttpMethod method, string eventId = null, string content = null, string query = null)
        {
            var request = new HttpRequestMessage(method, $"https://api.teamup.com/{_calendarKey}/events/{eventId}{query}");

            request.Headers.Add("Teamup-Token", _apiKey);

            if (content != null)
            {
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request);

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            return JsonConvert.DeserializeObject<T>(json);
        }


        public async Task<Event[]> SearchEventsAsync(DateTime start, DateTime end)
        {
            string query = $"?startDate={start.ToISODate()}&endDate={end.ToISODate()}";
            if (_subCalendarId.HasValue)
            {
                query += "&subcalendarId[]=" + _subCalendarId;
            }

            EventsResponse response = await MakeRequestAsync<EventsResponse>(HttpMethod.Get, query: query);
            return response.Events;
        }

        public async Task<Event> CreateYearlyWholeDayEventAsync(string title, DateTime date)
        {
            var requestEvent = new Event()
            {
                Title = title,
                StartDt = new DateTimeOffset(date.Date, TimeSpan.Zero),
                EndDt = new DateTimeOffset(date.Date.AddHours(1), TimeSpan.Zero),
                AllDay = true,
                Rrule = "FREQ=YEARLY",
                SubcalendarIds = _subCalendarId is null ? null : new int[] { _subCalendarId.Value }
            };

            string content = JsonConvert.SerializeObject(requestEvent);

            EventResponse response = await MakeRequestAsync<EventResponse>(HttpMethod.Post, content: content);
            return response.Event;
        }


        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        private class EventsResponse
        {
            public Event[] Events;
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        private class EventResponse
        {
            public Event Event;
        }
    }

    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy), ItemNullValueHandling = NullValueHandling.Ignore)]
    public class Event
    {
        public string Id;
        public string Title;
        public DateTimeOffset StartDt;
        public DateTimeOffset EndDt;
        public bool AllDay;
        public int[] SubcalendarIds;
        public string Rrule;
    }
}
