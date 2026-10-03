using Jellyfin.Plugin.HomeScreenSections.Configuration;
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

		public int? Limit => 1;

		public string? Route => OriginalPayload != null ? "originalpayload" : null;

		public string? AdditionalData { get; set; }

		public object? OriginalPayload { get; private set; }
		
		private IUserManager UserManager { get; set; }

		private IDtoService DtoService { get; set; }

		private IPlaylistManager PlaylistManager { get; set; }

		public MyListSection(IUserManager userManager, IDtoService dtoService, IPlaylistManager playlistManager)
		{
			UserManager = userManager;
			DtoService = dtoService;
			PlaylistManager = playlistManager;
		}

		public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
		{
			User? user = userId == null || userId == Guid.Empty ? null : UserManager.GetUserById(userId.Value);
			Playlist? myListPlaylist = user == null ? null : PlaylistManager.GetPlaylists(user.Id).FirstOrDefault(x => x.Name == "My List");

			yield return new MyListSection(UserManager, DtoService, PlaylistManager)
			{
				DisplayText = DisplayText,
				AdditionalData = AdditionalData,
				OriginalPayload = myListPlaylist == null ? null : DtoService.GetBaseItemDto(myListPlaylist, new DtoOptions(), user)
			};
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
			return new HomeScreenSectionInfo
			{
				Section = Section,
				DisplayText = DisplayText,
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
