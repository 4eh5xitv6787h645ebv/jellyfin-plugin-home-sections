using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections
{
	internal class MyListSection : IHomeScreenSection
	{
		public string? Section => "MyList";

		public string? DisplayText { get; set; } = "My List";

		public TranslationMetadata? TranslationMetadata => string.IsNullOrWhiteSpace(CustomTitle) ? null : new TranslationMetadata
		{
			Type = TranslationType.None
		};

		private string? CustomTitle => HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings.FirstOrDefault(x => x.SectionId == Section)?.GetAdminConfig<string>("customTitle");

		public int? Limit => 1;

		public string? Route => null;

		public string? AdditionalData { get; set; }

		public object? OriginalPayload => null;
		
		private IUserManager UserManager { get; set; }

		private IDtoService DtoService { get; set; }

		private IPlaylistManager PlaylistManager { get; set; }

		public MyListSection(IUserManager userManager, IDtoService dtoService, IPlaylistManager playlistManager)
		{
			UserManager = userManager;
			DtoService = dtoService;
			PlaylistManager = playlistManager;
		}

		public IEnumerable<PluginConfigurationOption> GetPluginConfigurationOptions()
		{
			yield return PluginConfigurationHelper.CreateTextBox("customTitle", "Custom title (blank uses the translated default)",
				"Change the display title only. The playlist must still be named My List.", "AdminMyListCustomTitle");
		}

		public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
		{
			yield return this;
		}

		public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
		{
			DtoOptions? dtoOptions = new DtoOptions
			{
				Fields = new List<ItemFields>
				{
					ItemFields.PrimaryImageAspectRatio
				},
				ImageTypeLimit = 1,
				ImageTypes = new List<ImageType>
				{
					ImageType.Thumb,
					ImageType.Backdrop,
					ImageType.Primary,
				}
			};

			User user = UserManager.GetUserById(payload.UserId)!;

			IEnumerable<Playlist> playlists = PlaylistManager.GetPlaylists(user.Id);
			Playlist? myListPlaylist = playlists.FirstOrDefault(x => x.Name == "My List");

			List<BaseItem> results = new List<BaseItem>();

			if (myListPlaylist != null)
			{
				results.AddRange(myListPlaylist.GetChildren(user, true, new InternalItemsQuery(user)
				{
					IsAiring = true
				}));
			}

			QueryResult<BaseItemDto>? result = new QueryResult<BaseItemDto>(DtoService.GetBaseItemDtos(results, dtoOptions, user));

			return result;
		}
		
		public HomeScreenSectionInfo GetInfo()
		{
			string? customTitle = CustomTitle;

			return new HomeScreenSectionInfo
			{
				Section = Section,
				DisplayText = string.IsNullOrWhiteSpace(customTitle) ? DisplayText : customTitle,
				AdditionalData = AdditionalData,
				Route = Route,
				Limit = Limit ?? 1,
				OriginalPayload = OriginalPayload,
				ViewMode = SectionViewMode.Landscape,
				PluginConfigurationOptions = (this as IHomeScreenSection).GetPluginConfigurationOptions().ToArray()
			};
		}
	}
}
