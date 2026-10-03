using System.Reflection;
using Jellyfin.Plugin.HomeScreenSections.Data;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen;
using Jellyfin.Plugin.HomeScreenSections.JellyfinVersionSpecific;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HomeScreenSections
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<CollectionManagerProxy>();
            serviceCollection.AddSingleton<HomeScreenSectionService>();
            serviceCollection.AddHttpClient();
            serviceCollection.AddSingleton<ArrApiService>(services =>
            {
                IHttpClientFactory httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
                return ActivatorUtilities.CreateInstance<ArrApiService>(services, httpClientFactory.CreateClient());
            });
            serviceCollection.AddSingleton<ImageCacheService>(services =>
            {
                IHttpClientFactory httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
                return ActivatorUtilities.CreateInstance<ImageCacheService>(services, httpClientFactory.CreateClient());
            });
            serviceCollection.AddSingleton<UserSectionsDataCache>();
            serviceCollection.AddSingleton<ITranslationManager, TranslationManager>();
            serviceCollection.AddSingleton<IHomeScreenManager, HomeScreenManager>(services =>
            {
                ILogger<HomeScreenManager> logger = services.GetRequiredService<ILogger<HomeScreenManager>>();
                IApplicationPaths appPaths = services.GetRequiredService<IApplicationPaths>();
                
                HomeScreenManager homeScreenManager = ActivatorUtilities.CreateInstance<HomeScreenManager>(services);
                
                string pluginLocation = Path.Combine(appPaths.PluginConfigurationsPath, typeof(HomeScreenSectionsPlugin).Namespace!);

                DirectoryInfo pluginDir = new DirectoryInfo(pluginLocation);
                pluginDir.Create();
                
                string[] extraDlls = Directory.GetFiles(pluginLocation, "*.dll", SearchOption.AllDirectories).ToArray();

                foreach (string extraDll in extraDlls)
                {
                    Type[] extensionTypes;
                    try
                    {
                        Assembly extraPluginAssembly = Assembly.LoadFile(extraDll);
                        extensionTypes = extraPluginAssembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        logger.LogWarning(ex, "Unable to load some extension types from {File}; continuing with available types", extraDll);
                        extensionTypes = ex.Types.OfType<Type>().ToArray();
                    }
                    catch (BadImageFormatException ex)
                    {
                        logger.LogWarning(ex, "Unable to load extension assembly {File}", extraDll);
                        continue;
                    }
                    catch (IOException ex)
                    {
                        logger.LogWarning(ex, "Unable to load extension assembly {File}", extraDll);
                        continue;
                    }

                    foreach (Type homeScreenSectionType in extensionTypes.Where(x => x.IsClass && !x.IsAbstract && !x.ContainsGenericParameters && x.IsAssignableTo(typeof(IHomeScreenSection))))
                    {
                        try
                        {
                            homeScreenManager.RegisterResultsDelegate(homeScreenSectionType);
                        }
                        // Third-party constructors and section properties can throw arbitrary exceptions.
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Unable to register extension type {Type} from {File}", homeScreenSectionType.FullName, extraDll);
                        }
                    }
                }

                return homeScreenManager;
            });
        }
    }
}
