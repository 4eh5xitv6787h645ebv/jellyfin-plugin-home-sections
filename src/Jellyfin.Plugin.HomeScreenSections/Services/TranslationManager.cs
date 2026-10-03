using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.Services
{
    public class TranslationManager : ITranslationManager
    {
        private readonly object m_translationPacksLock = new object();
        private volatile Dictionary<string, JObject> m_translationPacks = new Dictionary<string, JObject>();
        private readonly ILogger<ITranslationManager> m_logger;

        public TranslationManager(ILogger<ITranslationManager> logger)
        {
            m_logger = logger;
        }

        public void Initialize()
        {
            m_logger.LogTrace("Loading translation files");
            m_logger.LogTrace($"Available resources: {string.Join(',', HomeScreenSectionsPlugin.Instance.GetType().Assembly.GetManifestResourceNames())}");
            
            // Get all the json files from the embedded resources
            string[] locJsonFiles = HomeScreenSectionsPlugin.Instance.GetType().Assembly.GetManifestResourceNames()
                .Where(x => x.EndsWith(".json") && x.Contains("_Localization.")).ToArray();

            Dictionary<string, JObject> translationPacks = new Dictionary<string, JObject>();
            foreach (string locFile in locJsonFiles)
            {
                m_logger.LogTrace($"Loading translation file: {locFile}");
                using Stream? locStream = HomeScreenSectionsPlugin.Instance.GetType().Assembly.GetManifestResourceStream(locFile);

                if (locStream != null)
                {
                    using TextReader reader = new StreamReader(locStream);

                    string key = locFile.Replace(".json", "").Split('.').Last();

                    if (!translationPacks.ContainsKey(key))
                    {
                        translationPacks.Add(key, JObject.Parse(reader.ReadToEnd()));
                        m_logger.LogTrace($"Loaded translation file: {locFile} with {translationPacks[key].Count} keys");
                    }
                    else
                    {
                        m_logger.LogTrace($"Translation file '{locFile}' already loaded, ignoring");
                    }
                }
            }
            lock (m_translationPacksLock)
            {
                m_translationPacks = translationPacks;
            }
        }

        public string Translate(string key, string desiredLanguage, string fallbackText, TranslationMetadata? metadata = null)
        {
            m_logger.LogTrace($"Translating key '{key}' to language '{desiredLanguage}'");
            
            Dictionary<string, JObject> translationPacks = m_translationPacks;
            bool languageFound = false;
            string languageKey = desiredLanguage;

            do
            {
                // If we don't have the language, but it has a region remove the region and just grab the language and see if we 
                // have a blanket translation for that language.
                if (!translationPacks.ContainsKey(languageKey) && languageKey.Contains("-"))
                {
                    m_logger.LogTrace($"Language '{languageKey}' doesn't exist, removing region and trying again");
                    languageKey = languageKey.Split("-")[0];
                }
                // If we don't then fallback to english so we don't get keys being sent to the client
                else if (!translationPacks.ContainsKey(languageKey))
                {
                    m_logger.LogTrace($"Language '{languageKey}' doesn't exist, falling back to english");
                    if (languageKey == "en")
                    {
                        break;
                    }
                    languageKey = "en";
                }
                // If we have it then we're done.
                else if (translationPacks.ContainsKey(languageKey))
                {
                    m_logger.LogTrace($"Found translation pack for language '{languageKey}'");
                    languageFound = true;
                }
            } while (!languageFound);

            translationPacks.TryGetValue(languageKey, out JObject? translationPack);

            string translatedText = fallbackText;
            string fullTextKey = fallbackText.Replace(" ", "").Replace("-", "");
            if (key != fullTextKey && translationPack != null && translationPack.ContainsKey(fullTextKey))
            {
                m_logger.LogTrace($"Found translation for key '{fullTextKey}' in language '{languageKey}'");
                translatedText = translationPack.Value<string>(fullTextKey)!;
                
                // Since we've got a full translation we don't need the metadata
                metadata = null;
            }
            else if (translationPack != null && translationPack.ContainsKey(key))
            {
                m_logger.LogTrace($"Found translation for key '{key}' in language '{languageKey}'");
                translatedText = translationPack.Value<string>(key)!;
            }
            else if (translationPack != null)
            {
                m_logger.LogWarning($"No translation found for key '{key}' in language '{languageKey}', falling back to previous routes");
                // If Libre is disabled this will be null
                string? libreTranslateVersion = LibreTranslateHelper.TranslateAsync(fallbackText, "en", desiredLanguage).GetAwaiter().GetResult();
                
                translatedText = libreTranslateVersion ?? translationPacks.GetValueOrDefault("en")?.Value<string>(key) ?? fallbackText;
            }

            if (metadata != null)
            {
                m_logger.LogTrace($"Applying metadata to translated text: {translatedText}");

                string? additionalContent = metadata.AdditionalContent;
                if (metadata.TranslateAdditionalContent && !string.IsNullOrEmpty(additionalContent))
                {
                    additionalContent = Translate(additionalContent.Replace(" ", "").Replace("-", ""), desiredLanguage, additionalContent);
                }
                
                if (metadata.Type == TranslationType.Prefix)
                {
                    translatedText = $"{translatedText} {additionalContent}".Trim();
                }
                else if (metadata.Type == TranslationType.Suffix)
                {
                    translatedText = $"{additionalContent} {translatedText}".Trim();
                }
                else if (metadata.Type == TranslationType.Pattern)
                {
                    translatedText = translatedText.Replace("{0}", additionalContent);
                }
                
                m_logger.LogTrace($"Applied metadata to translated text: {translatedText}");
            }
            
            return translatedText;
        }

        public void UpdateTranslationPack(string language, JObject translationPack)
        {
            UpdateTranslationPacks(new Dictionary<string, JObject> { { language, translationPack } });
        }

        public void UpdateTranslationPacks(IDictionary<string, JObject> translationPacks)
        {
            lock (m_translationPacksLock)
            {
                Dictionary<string, JObject> updatedPacks = new Dictionary<string, JObject>(m_translationPacks);
                foreach (KeyValuePair<string, JObject> translationPack in translationPacks)
                {
                    JObject mergedPack = updatedPacks.TryGetValue(translationPack.Key, out JObject? existingPack)
                        ? (JObject)existingPack.DeepClone()
                        : new JObject();
                    foreach (KeyValuePair<string, JToken?> keyValue in translationPack.Value)
                    {
                        mergedPack[keyValue.Key] = keyValue.Value?.DeepClone();
                    }
                    updatedPacks[translationPack.Key] = mergedPack;
                }
                m_translationPacks = updatedPacks;
            }
        }

        public IDictionary<string, string>? GetTranslationPack(string language)
        {
            Dictionary<string, JObject> translationPacks = m_translationPacks;
            string languageKey = language;

            if (!translationPacks.ContainsKey(languageKey) && languageKey.Contains("-"))
            {
                languageKey = languageKey.Split("-")[0];
            }

            if (!translationPacks.ContainsKey(languageKey))
            {
                languageKey = "en";
            }

            if (translationPacks.TryGetValue(languageKey, out JObject? pack))
            {
                return pack.ToObject<Dictionary<string, string>>();
            }

            return null;
        }
    }
}