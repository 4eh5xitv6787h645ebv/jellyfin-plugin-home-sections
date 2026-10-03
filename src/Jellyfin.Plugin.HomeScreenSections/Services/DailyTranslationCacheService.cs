using System.Reflection;
using Jellyfin.Plugin.HomeScreenSections.JellyfinVersionSpecific;
using Jellyfin.Plugin.HomeScreenSections.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.Services
{
    public class DailyTranslationCacheService : IScheduledTask
    {
        public string Name => "HSS Daily Translation Cache";
        public string Key => "Jellyfin.Plugin.HomeScreenSections.DailyTranslationCache";
        public string Description => "Goes to GitHub and downloads the latest translation files.";
        public string Category => "Maintenance";

        private readonly ITranslationManager m_translationManager;
        private readonly ILogger<DailyTranslationCacheService> m_logger;
        
        // Trailing slash included to avoid getting the folder from the github trees JSON data
        private const string c_locPath = "src/Jellyfin.Plugin.HomeScreenSections/_Localization/";

        public DailyTranslationCacheService(ITranslationManager translationManager, ILogger<DailyTranslationCacheService> logger)
        {
            m_translationManager = translationManager;
            m_logger = logger;
        }
        
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => StartupServiceHelper.GetStartupTrigger()
            .Concat(StartupServiceHelper.GetDailyTrigger(TimeSpan.FromHours(3)));

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                await RefreshAsync(progress, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                m_logger.LogWarning(ex, "Unable to download translation packs; keeping cached translations");
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                m_logger.LogWarning(ex, "Translation download timed out; keeping cached translations");
            }

            progress.Report(1);
        }

        private async Task RefreshAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
            
            string? gitBranch = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => x.Key == "GitBranch")?.Value;

            if (!string.IsNullOrEmpty(gitBranch))
            {
                using HttpResponseMessage treesResponse = await client.GetAsync(new Uri($"https://api.github.com/repos/IAmParadox27/jellyfin-plugin-home-sections/git/trees/{gitBranch}?recursive=1"), cancellationToken);

                // After getting the trees we say we're 10% just to signify something has happened
                double currentProgress = 0.1;
                progress.Report(currentProgress);
                
                JObject? treesObj = await ReadResponseAsync(treesResponse, cancellationToken);
                if (treesObj == null)
                {
                    return;
                }
                if (treesObj["tree"] is not JArray tree)
                {
                    m_logger.LogWarning("Translation tree response has no tree array; keeping cached translations");
                    return;
                }
                IEnumerable<JObject> data = tree.OfType<JObject>().Where(x => x["path"]?.Type == JTokenType.String && (x.Value<string>("path")?.StartsWith(c_locPath) ?? false) && (x.Value<string>("path")?.EndsWith(".json") ?? false));
                if (data != null)
                {
                    string[] blobUrls = data.Select(x => x.Value<string>("path")).Where(x => x != null).Select(x => x!).ToArray();
                    
                    Dictionary<string, JObject> translationPacks = new Dictionary<string, JObject>();
                    double progressIncrement = 0.9 / blobUrls.Length;
                    
                    foreach (string blobUrl in blobUrls)
                    {
                        using HttpResponseMessage blobResponse = await client.GetAsync(new Uri($"https://raw.githubusercontent.com/IAmParadox27/jellyfin-plugin-home-sections/refs/heads/{gitBranch}/{blobUrl}"), cancellationToken);
                        JObject? languagePack = await ReadResponseAsync(blobResponse, cancellationToken);
                        if (languagePack == null)
                        {
                            return;
                        }
                        if (languagePack.Properties().Any(x => x.Value.Type != JTokenType.String))
                        {
                            m_logger.LogWarning("Translation pack {Path} contains non-string values; keeping cached translations", blobUrl);
                            return;
                        }

                        string languageCode = Path.GetFileNameWithoutExtension(blobUrl);
                        translationPacks[languageCode] = languagePack;

                        currentProgress += progressIncrement;
                        progress.Report(currentProgress);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    m_translationManager.UpdateTranslationPacks(translationPacks);
                }
            }
        }

        private async Task<JObject?> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode)
            {
                m_logger.LogWarning("Translation request {Uri} returned {StatusCode}; keeping cached translations", response.RequestMessage?.RequestUri, response.StatusCode);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonReaderException ex)
            {
                m_logger.LogWarning(ex, "Translation request {Uri} did not return a JSON object; keeping cached translations", response.RequestMessage?.RequestUri);
                return null;
            }
        }
    }
}