using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Extensions;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Data;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HomeScreenSections.Services
{
    public class HomeScreenSectionService
    {
        private readonly IServerConfigurationManager m_configurationManager;
        private readonly IHomeScreenManager m_homeScreenManager;
        private readonly ILogger<HomeScreenSectionsPlugin> m_logger;
        private readonly ITranslationManager m_translationManager;
        private readonly UserSectionsDataCache m_dataCache;
    
        public HomeScreenSectionService(IHomeScreenManager homeScreenManager,
            ILogger<HomeScreenSectionsPlugin> logger, ITranslationManager translationManager,
            UserSectionsDataCache dataCache, IServerConfigurationManager _configurationManager)
        {
            m_homeScreenManager = homeScreenManager;
            m_logger = logger;
            m_translationManager = translationManager;
            m_dataCache = dataCache;
            m_configurationManager = _configurationManager;
        }

        public List<HomeScreenSectionInfo>? GetCachedSectionsForUser(Guid userId, string? language, int page, int pageSize, Guid pageHash)
        {
            if (!m_dataCache.Cache.TryGetValue(pageHash, out UserSectionsData? userSectionsData))
            {
                return null;
            }
            
            return GetCachedSectionsForUser(userSectionsData, language, page, pageSize, pageHash);
        }

        private List<HomeScreenSectionInfo>? GetCachedSectionsForUser(UserSectionsData userSectionsData, string? language, int page, int pageSize, Guid pageHash)
        {
            // Make sure that it's flagged as being used, even if we don't return anything here the page is still active
            // as we've received a request for it.
            userSectionsData.LastAccessed = DateTime.UtcNow;
            m_dataCache.PageHashExpiry.TryUpdate(pageHash, DateTime.UtcNow.AddHours(1), m_dataCache.PageHashExpiry[pageHash]); // TODO: In a future update we should make this configurable.
            
            // Check if the userSectionsData has the data we're after
            int[] orderedKeys = userSectionsData.OrderedSections.Keys.OrderBy(x => x).ToArray();

            List<(IHomeScreenSection Section, int ConfiguredOrder)> sectionsToReturn = new List<(IHomeScreenSection, int)>();
            bool isComplete = true;
            for (int i = 0; i < orderedKeys.Length; i++)
            {
                int key = orderedKeys[i];
                int prevKey = i > 0 ? orderedKeys[i - 1] : orderedKeys[i] - 1;

                bool cohesive = (key - prevKey) == 1;
                if (prevKey > 0 && key - prevKey > 1)
                {
                    // If any of the ranges contain both the "key before" and "key after" then we can safely know this is cohesive.
                    if (userSectionsData.OrderIndicesWithoutSections.Any(x => x.Contains(key - 1) && x.Contains(prevKey + 1)))
                    {
                        cohesive = true;
                    }
                }

                if (cohesive)
                {
                    sectionsToReturn.AddRange(userSectionsData.OrderedSections[key].Select(x => (x, key)));
                }
                else
                {
                    isComplete = false;
                    break;
                }
            }
            
            sectionsToReturn = sectionsToReturn.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            if ((isComplete && !userSectionsData.SectionsInProgress.Any()) || sectionsToReturn.Count == pageSize)
            {
                return sectionsToReturn
                    .Select(x => SectionToInfo(x.Section, x.ConfiguredOrder, language))
                    .ToList();
            }

            // Return nothing if we don't have the complete picture.
            return null;
        }

        private Guid GeneratePageHash(Guid userId)
        {
            Guid pageHash = Guid.NewGuid();
            m_dataCache.PageHashOwnerIds.TryAdd(pageHash, userId);
            m_dataCache.PageHashExpiry.TryAdd(pageHash, DateTime.UtcNow.AddHours(1)); // TODO: In a future update we should make this configurable.
            
            return pageHash;
        }

        public async Task<List<HomeScreenSectionInfo>?> MonitorLiveUpdatedSectionsForUser(Guid userId, string? language, int page, int? pageSize = null, Guid? pageHash = null, CancellationToken cancellationToken = default)
        {
            // Kick off the task to remove the expired "temp" user caches to avoid a memory leak.
            _ = Task.Run(() => ClearExpiredUserCaches(userId));

            // If the sections have been requested with a page hash that wasn't generated for this user, generate a new one.
            if (pageHash.HasValue && !DoesPageBelongToUser(pageHash.Value, userId))
            {
                pageHash = GeneratePageHash(userId);
            }
            
            bool returnAllSections = false;
            if (pageHash == null)
            {
                pageHash = GetActiveTempPageCacheForUser(userId);

                if (pageHash == null)
                {
                    pageHash = GeneratePageHash(userId);
                    returnAllSections = true;
                }
            }
            
            // Cancellation only stops this request's wait, not the shared generation.
            UserSectionsData cache = await Task.Run(() => CacheSectionsForUser(userId, pageHash.Value))
                .WaitAsync(cancellationToken).ConfigureAwait(false);

            // We always wait from the start, if we hit a page that's already cached then we'll just return immediately.
            // If its still in progress then we'll wait for it to finish.
            foreach (TaskCompletionSource completion in cache.SectionCompletions.Values)
            {
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (pageSize.HasValue && !returnAllSections)
                {
                    List<HomeScreenSectionInfo>? sections = GetCachedSectionsForUser(cache, language, page, pageSize.Value, pageHash.Value);
                    if (sections != null)
                    {
                        return sections;
                    }
                }
            }

            int totalSectionCount = cache.OrderedSections.SelectMany(x => x.Value).Count();
            return GetCachedSectionsForUser(cache, language, returnAllSections ? 1 : page,
                returnAllSections ? totalSectionCount : pageSize ?? totalSectionCount, pageHash.Value);
        }
    
        public UserSectionsData CacheSectionsForUser(Guid userId, Guid pageHash)
        {
            if (m_dataCache.Cache.TryGetValue(pageHash, out UserSectionsData? existingCache))
            {
                return existingCache;
            }
            
            ModularHomeUserSettings? settings = m_homeScreenManager.GetUserSettings(userId);

            List<IHomeScreenSection> sectionTypes = m_homeScreenManager.GetSectionTypes().Where(x => settings?.EnabledSections.Contains(x.Section ?? string.Empty) ?? false).ToList();

            IGrouping<int, SectionSettings>[] groupedOrderedSections = HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings
                .OrderBy(x => x.OrderIndex)
                .GroupBy(x => x.OrderIndex)
                .ToArray();

            UserSectionsData userSectionsData = new UserSectionsData()
            {
                UserId = userId,
                MaxOrderIndex = groupedOrderedSections.Select(g => g.Key).DefaultIfEmpty(0).Max()
            };

            foreach (int orderIndex in groupedOrderedSections.Select(x => x.Key).OrderBy(x => x))
            {
                userSectionsData.SectionsInProgress.TryAdd(orderIndex, true);
                userSectionsData.SectionCompletions.Add(orderIndex, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            int[] sectionIndices = userSectionsData.SectionsInProgress.Keys.OrderBy(x => x).ToArray();
            for (int i = 1; i < sectionIndices.Length; i++)
            {
                int prevIndex = sectionIndices[i - 1];
                int currentIndex = sectionIndices[i];

                if (currentIndex - prevIndex > 1)
                {
                    userSectionsData.OrderIndicesWithoutSections.Add(new IntRange()
                    {
                        Start = prevIndex + 1,
                        End = currentIndex - 1
                    });
                }
            }

            // Publish only fully initialized entries; only the winner starts generation.
            UserSectionsData cache = m_dataCache.Cache.GetOrAdd(pageHash, userSectionsData);
            if (!ReferenceEquals(cache, userSectionsData))
            {
                return cache;
            }

            foreach (IGrouping<int, SectionSettings> orderedSections in groupedOrderedSections)
            {
                _ = Task.Run(() => CacheSectionGroup(userId, pageHash, cache, sectionTypes, orderedSections));
            }

            return cache;
        }

        private void CacheSectionGroup(Guid userId, Guid pageHash, UserSectionsData cache, List<IHomeScreenSection> sectionTypes, IGrouping<int, SectionSettings> orderedSections)
        {
            TaskCompletionSource completion = cache.SectionCompletions[orderedSections.Key];
            try
            {
                ConcurrentBag<IHomeScreenSection?> tmpPluginSections = new ConcurrentBag<IHomeScreenSection?>(); // we want these randomly distributed among each other.

                Parallel.ForEach(orderedSections, sectionSettings =>
                {
                    IHomeScreenSection? sectionType =
                        sectionTypes.FirstOrDefault(x => x.Section == sectionSettings.SectionId);

                    if (sectionType != null)
                    {
                        try
                        {
                            int instanceCount = 1;
                            if (sectionType.Limit > 1)
                            {
                                Random rnd = new Random();
                                instanceCount = rnd.Next(sectionSettings.LowerLimit, sectionSettings.UpperLimit);
                            }

                            IEnumerable<IHomeScreenSection> instances = sectionType.CreateInstances(userId, instanceCount);

                            foreach (IHomeScreenSection sectionInstance in instances)
                            {
                                tmpPluginSections.Add(sectionInstance);
                            }
                        }
                        catch (Exception e)
                        {
                            // Adding an error log here to stop issues like #128 from completely breaking the home screen.
                            // Whatever this section is won't work, but the rest of the home screen will still work.
                            m_logger.LogError(e, $"An error occurred while creating section instances for user '{userId}' and section '{sectionType.Section}'.");
                        }
                    }
                });

                List<IHomeScreenSection> sectionList = tmpPluginSections.Where(x => x != null).Select(x => x!).ToList();
                sectionList.Shuffle();

                cache.OrderedSections.TryAdd(orderedSections.Key, sectionList);
                cache.SectionsInProgress.Remove(orderedSections.Key, out _);
                completion.SetResult();
            }
            catch (Exception e)
            {
                m_logger.LogError(e, $"An error occurred while creating section instances for user '{userId}' and order index '{orderedSections.Key}'.");
                // Remove only this failed entry so later requests can retry without evicting a replacement.
                m_dataCache.Cache.TryRemove(new KeyValuePair<Guid, UserSectionsData>(pageHash, cache));
                // Keep the fault available to every waiter holding this cache, even after eviction.
                completion.SetException(e);
            }
        }

        private HomeScreenSectionInfo SectionToInfo(IHomeScreenSection section, int configuredOrder, string? language)
        {
            HomeScreenSectionInfo info = section.AsInfo();

            info.OrderIndex = configuredOrder;
            info.ViewMode = HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings.FirstOrDefault(y => y.SectionId == info.Section)?.ViewMode ?? info.ViewMode ?? SectionViewMode.Landscape;
            
            if (info.DisplayText != null)
            {
                // Fallback to system default language if there's no language provided.
                string? translatedResult = m_translationManager.Translate(info.Section!, language?.Trim() ?? m_configurationManager.Configuration.UICulture, info.DisplayText, section.TranslationMetadata);

                info.DisplayText = translatedResult;
            }
            
            return info;
        }

        private async Task ClearExpiredUserCaches(Guid userId)
        {
            await Task.Yield();
            
            Guid[] userPageHashes = m_dataCache.PageHashOwnerIds
                .Where(x => x.Value == userId)
                .Select(x => x.Key)
                .ToArray();
            Guid[] expiredPageHashes = m_dataCache.PageHashExpiry
                .Where(x => userPageHashes.Any(y => y == x.Key))
                .Where(x => x.Value < DateTime.UtcNow)
                .Select(x => x.Key)
                .ToArray();

            foreach (Guid pageHash in expiredPageHashes)
            {
                m_dataCache.Cache.TryRemove(pageHash, out _);
            }
        }

        private Guid? GetActiveTempPageCacheForUser(Guid userId)
        {
            Guid[] userPageHashes = m_dataCache.PageHashOwnerIds
                .Where(x => x.Value == userId)
                .Select(x => x.Key)
                .ToArray();
            Guid[] activePageHashes = m_dataCache.PageHashExpiry
                .Where(x => userPageHashes.Any(y => y == x.Key))
                .Where(x => x.Value > DateTime.UtcNow)
                .OrderByDescending(x => x.Value)
                .Select(x => x.Key)
                .ToArray();

            if (activePageHashes.Length == 0)
            {
                return null;
            }
            
            return activePageHashes.First();
        }
        
        private bool DoesPageBelongToUser(Guid pageHash, Guid userId)
        {
            return m_dataCache.PageHashOwnerIds.TryGetValue(pageHash, out Guid ownerId) && ownerId == userId;
        }
    }

    public class UserHomeSections
    {
        public Guid PageHash { get; set; }
        public List<HomeScreenSectionInfo> Sections { get; set; } = new List<HomeScreenSectionInfo>();
    }
}
