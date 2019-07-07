using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Web;

namespace TechnicalUpdates
{
    class Program
    {
        private static readonly HttpClient client = new HttpClient();
        private static readonly Regex ThreadRegex = new Regex("<h4><a href=\"https://community.fandom.com/wiki/Thread:(\\d+)\">(.*)</a></h4>");
        private static readonly Regex LinkRegex = new Regex("\\[\\[([^\\|]+)(?:\\|([^\\]]+))?\\]\\]");
        private static readonly Regex ExternalLinkRegex = new Regex("\\[(https?://[^\\s]+) ([^\\]]+)\\]");
        private static readonly Regex BoldRegex = new Regex("🇧([^🇧]+)🇧");
        private static readonly Regex ItalicRegex = new Regex("🇮([^🇮]+)🇮");

        private class Thread
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string Content { get; set; }
        }

        private class ForumResponse
        {
            public string Html { get; set; }
        }

        private class ThreadResponse
        {
            public string Htmlorwikitext { get; set; }
            public bool Status { get; set; }
        }

        private class DiscordMessage
        {
            [JsonProperty("embeds")]
            public List<DiscordEmbed> Embeds;
        }

        private class DiscordEmbed
        {
            [JsonProperty("title")]
            public string Title { get; set; }
            [JsonProperty("description")]
            public string Description { get; set; }
            [JsonProperty("url")]
            public string Url { get; set; }
            [JsonProperty("timestamp")]
            public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
            [JsonProperty("color")]
            public int Color { get; set; } = 0x00D6D6;
        }

        static void Main(string[] args)
        {
            var timer = new Timer(5000);
            timer.Elapsed += Process;
            timer.Start();
            Console.ReadLine();
        }

        static async void Process(object sender, EventArgs e)
        {
            var lastThreadId = await GetLastThreadId();
            var boardContent = await GetBoardContent();
            var lastThread = GetLastThread(boardContent);
            if (lastThreadId >= lastThread.Id)
            {
                return;
            }
            lastThread.Content = await GetThreadContent(lastThread.Id);
            lastThread.Content = ConvertThreadContent(lastThread.Content);
            var url = GetWebhookUrl();
            await PostToDiscord(lastThread, url);
            await SetLastThreadId(lastThread.Id);
        }

        static async Task<T> QueryNirvana<T>(string controller, string method, Dictionary<string, string> parameters)
        {
            var body = new FormUrlEncodedContent(parameters);
            var builder = new UriBuilder("https://community.fandom.com/wikia.php");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["controller"] = controller;
            query["format"] = "json";
            query["method"] = method;
            builder.Query = query.ToString();
            var response = await client.PostAsync(builder.ToString(), body);
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(content);
        }

        static async Task<int> GetLastThreadId()
        {
            if (!File.Exists("last.id"))
            {
                return -1;
            }
            using (var reader = new StreamReader("last.id"))
            {
                return int.Parse(await reader.ReadToEndAsync());
            }
        }

        static async Task<string> GetBoardContent()
        {
            var query = await QueryNirvana<ForumResponse>("ForumExternal", "getCommentsPage", new Dictionary<string, string>
            {
                { "page", "1" },
                { "pagetitle", "Technical Updates" },
                { "pagenamespace", "2000" }
            });
            return query.Html;
        }

        static Thread GetLastThread(string forumContent)
        {
            var matches = ThreadRegex.Matches(forumContent);
            var sorted = new List<Thread>();
            foreach (Match match in matches)
            {
                sorted.Add(new Thread
                {
                    Id = int.Parse(match.Groups[1].Value),
                    Title = HttpUtility.HtmlDecode(match.Groups[2].Value)
                });
            }
            sorted.Sort((a, b) => b.Id - a.Id);
            return sorted[0];
        }

        static async Task<string> GetThreadContent(int threadId)
        {
            var query = await QueryNirvana<ThreadResponse>("WallExternal", "editMessage", new Dictionary<string, string>
            {
                { "msgid", threadId.ToString() },
                { "pagetitle", threadId.ToString() },
                { "pagenamespace", threadId.ToString() }
            });
            return query.Htmlorwikitext;
        }

        static string ConvertThreadContent(string threadContent)
        {
            var builder = new StringBuilder();
            foreach (string line in threadContent.Split("\n"))
            {
                if (line.StartsWith("*"))
                {
                    builder.Append("•");
                    var replaced = ExternalLinkRegex.Replace(
                        ItalicRegex.Replace(
                            BoldRegex.Replace(
                                line.Substring(1)
                                    .Replace("'''", "🇧")
                                    .Replace("''", "🇮"),
                                "**$1**"
                            ),
                            "_$1_"
                        ),
                        "[$2]($1)"
                    );
                    var linkReplaced = replaced;
                    var links = LinkRegex.Matches(replaced);
                    foreach (Match match in links)
                    {
                        var link = match.Groups[1].Value;
                        var text = match.Groups[2].Value;
                        var markdownLink = $"[{(text == null ? link : text)}]({GetUrl(link)})";
                        linkReplaced = linkReplaced.Replace(match.Groups[0].Value, markdownLink);
                    }
                    builder.Append(linkReplaced);
                    builder.Append("\n");
                }
            }
            return builder.ToString();
        }

        static string GetUrl(string link)
        {
            string host;
            string page;
            if (link.StartsWith("w:c:"))
            {
                var split = link.Split(":", 3);
                host = $"https://{split[2]}.fandom.com/wiki/";
                page = split[3];
            }
            else if (link.StartsWith("mw:"))
            {
                host = "https://mediawiki.org/wiki/";
                page = link.Split(":", 1)[1];
            }
            else if (link.StartsWith("wikipedia:") || link.StartsWith("wp:"))
            {
                host = "https://en.wikipedia.org/wiki/";
                page = link.Split(":", 1)[1];
            }
            else
            {
                host = "https://community.fandom.com/wiki/";
                page = link;
            }
            var encodedLink = Uri.EscapeUriString(link)
                .Replace("!", "%21")
                .Replace("'", "%27")
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("*", "%2A")
                .Replace("~", "%7E")
                .Replace("%20", "_")
                .Replace("%2F", "/");
            return $"{host}{encodedLink}";
        }

        static string GetWebhookUrl()
        {
            return new ConfigurationBuilder()
                .AddUserSecrets<Program>()
                .Build()
                .GetSection("WebhookUrl")
                .Value;
        }

        static async Task PostToDiscord(Thread thread, string webhookUrl)
        {
            var parameters = JsonConvert.SerializeObject(new DiscordMessage
            {
                Embeds = new List<DiscordEmbed>
                {
                    new DiscordEmbed
                    {
                        Title = thread.Title,
                        Description = thread.Content,
                        Url = $"https://community.fandom.com/wiki/Thread:{thread.Id}"
                    }
                }
            });
            var body = new StringContent(parameters, Encoding.UTF8, "application/json");
            await client.PostAsync(webhookUrl, body);
        }

        static async Task SetLastThreadId(int threadId)
        {
            using (var writer = new StreamWriter("last.id"))
            {
                await writer.WriteAsync(threadId.ToString());
            }
        }
    }
}
