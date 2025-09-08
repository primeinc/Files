// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.Application;
using Files.App.Services.SizeProvider;
using Files.App.Utils.Logger;
using Files.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentry;
using Sentry.Protocol;
using System.IO;
using System.Text;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Files.App.Communication; // Added for IPC service registrations

namespace Files.App.Helpers
{
	/// <summary>
	/// Provides static helper to manage app lifecycle.
	/// </summary>
	public static class AppLifecycleHelper
	{
		private readonly static string AppInformationKey = @$"Software\Files Community\{Package.Current.Id.Name}\v1\AppInformation";

		/// <summary>
		/// Gets the value that indicates whether the app is updated.
		/// </summary>
		public static bool IsAppUpdated { get; }

		/// <summary>
		/// Gets the value that indicates whether the app is running for the first time.
		/// </summary>
		public static bool IsFirstRun { get; }

		/// <summary>
		/// Gets the value that indicates the total launch count of the app.
		/// </summary>
		public static long TotalLaunchCount { get; }

		/// <summary>
		/// Gets the value that indicates if the release notes tab was automatically opened.
		/// </summary>
		private static bool ViewedReleaseNotes { get; set; } = false;

		static AppLifecycleHelper()
		{
			using var infoKey = Registry.CurrentUser.CreateSubKey(AppInformationKey);
			var version = infoKey.GetValue("LastLaunchVersion");
			var launchCount = infoKey.GetValue("TotalLaunchCount");
			if (version is null)
			{
				IsAppUpdated = true;
				IsFirstRun = true;
			}
			else
			{
				IsAppUpdated = version.ToString() != AppVersion.ToString();
			}

			TotalLaunchCount = long.TryParse(launchCount?.ToString(), out var v) ? v + 1 : 1;
			infoKey.SetValue("LastLaunchVersion", AppVersion.ToString());
			infoKey.SetValue("TotalLaunchCount", TotalLaunchCount);
		}

		/// <summary>
		/// Gets the value that provides application environment or branch name.
		/// </summary>
		public static AppEnvironment AppEnvironment =>
			Enum.TryParse("cd_app_env_placeholder", true, out AppEnvironment appEnvironment)
				? appEnvironment
				: AppEnvironment.Dev;


		/// <summary>
		/// Gets application package version.
		/// </summary>
		public static Version AppVersion { get; } =
			new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision);

		/// <summary>
		/// Gets application icon path.
		/// </summary>
		public static string AppIconPath { get; } =
			SystemIO.Path.Combine(Package.Current.InstalledLocation.Path, AppEnvironment switch
			{
				AppEnvironment.Dev => Constants.AssetPaths.DevLogo,
				AppEnvironment.SideloadPreview or AppEnvironment.StorePreview => Constants.AssetPaths.PreviewLogo,
				_ => Constants.AssetPaths.StableLogo
			});

		/// <summary>
		/// Initializes the app components.
		/// </summary>
		public static async Task InitializeAppComponentsAsync()
		{
			var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			var addItemService = Ioc.Default.GetRequiredService<IAddItemService>();
			var generalSettingsService = userSettingsService.GeneralSettingsService;
			var jumpListService = Ioc.Default.GetRequiredService<IWindowsJumpListService>();
			var ipcService = Ioc.Default.GetRequiredService<IAppCommunicationService>();
			var ipcCoordinator = Ioc.Default.GetRequiredService<IpcCoordinator>();

			// Start off a list of tasks we need to run before we can continue startup
			await Task.WhenAll(
				App.QuickAccessManager.InitializeAsync()
			);

			// Start non-critical tasks without waiting for them to complete
			_ = Task.Run(async () =>
			{
				await Task.WhenAll(
					OptionalTaskAsync(CloudDrivesManager.UpdateDrivesAsync(), generalSettingsService.ShowCloudDrivesSection),
					App.LibraryManager.UpdateLibrariesAsync(),
					OptionalTaskAsync(WSLDistroManager.UpdateDrivesAsync(), generalSettingsService.ShowWslSection),
					OptionalTaskAsync(App.FileTagsManager.UpdateFileTagsAsync(), generalSettingsService.ShowFileTagsSection),
					jumpListService.InitializeAsync(),
					addItemService.InitializeAsync(),
					ContextMenu.WarmUpQueryContextMenuAsync(),
					CheckAppUpdate(),
					// Initialize IPC service if remote control is enabled
					OptionalTaskAsync(InitializeIpcAsync(ipcService, ipcCoordinator), Files.App.Communication.ProtectedTokenStore.IsEnabled())
				);
			});

			FileTagsHelper.UpdateTagsDb();

			static Task OptionalTaskAsync(Task task, bool condition)
			{
				if (condition)
					return task;

				return Task.CompletedTask;
			}

			generalSettingsService.PropertyChanged += GeneralSettingsService_PropertyChanged;
		}

		private static async Task InitializeIpcAsync(IAppCommunicationService ipcService, IpcCoordinator ipcCoordinator)
		{
			App.Logger?.LogInformation("[IPC] Starting IPC service...");
			await ipcService.StartAsync();
			App.Logger?.LogInformation("[IPC] IPC service started, initializing coordinator...");
			ipcCoordinator.Initialize();
			App.Logger?.LogInformation("[IPC] IPC system fully initialized and ready for requests");
		}

		/// <summary>
		/// Checks application updates and download if available.
		/// </summary>
		public static async Task CheckAppUpdate()
		{
			var updateService = Ioc.Default.GetRequiredService<IUpdateService>();

			await updateService.CheckForReleaseNotesAsync();

			// Check for release notes before checking for new updates
			if (AppEnvironment != AppEnvironment.Dev &&
				IsAppUpdated &&
				updateService.AreReleaseNotesAvailable &&
				!ViewedReleaseNotes)
			{
				await Ioc.Default.GetRequiredService<ICommandManager>().OpenReleaseNotes.ExecuteAsync();
				ViewedReleaseNotes = true;
			}

			await updateService.CheckForUpdatesAsync();
			await updateService.DownloadMandatoryUpdatesAsync();
			await updateService.CheckAndUpdateFilesLauncherAsync();
		}

		/// <summary>
		/// Configures Sentry service, such as Analytics and Crash Report.
		/// </summary>
		public static void ConfigureSentry()
		{
			SentrySdk.Init(options =>
			{
				options.Dsn = Constants.AutomatedWorkflowInjectionKeys.SentrySecret;
				options.AutoSessionTracking = true;
				var packageVersion = Package.Current.Id.Version;
				options.Release = $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}";
				options.TracesSampleRate = 0.80;
				options.ProfilesSampleRate = 0.40;
				options.Environment = AppEnvironment == AppEnvironment.StorePreview || AppEnvironment == AppEnvironment.SideloadPreview ? "preview" : "production";

				options.DisableWinUiUnhandledExceptionIntegration();
			});
		}

		/// <summary>
		/// Configures DI (dependency injection) container.
		/// </summary>
		public static IHost ConfigureHost()
		{
			var builder = Host.CreateDefaultBuilder()
				.UseContentRoot(Package.Current.InstalledLocation.Path)
				.UseEnvironment(AppLifecycleHelper.AppEnvironment.ToString())
				.ConfigureLogging(builder => builder
					.ClearProviders()
					.AddConsole()
					.AddDebug()
					.AddProvider(new FileLoggerProvider(Path.Combine(ApplicationData.Current.LocalFolder.Path, "debug.log")))
					.AddProvider(new SentryLoggerProvider())
					.SetMinimumLevel(LogLevel.Information))
				.ConfigureServices(services => services
					// Settings services
					.AddSingleton<IUserSettingsService, UserSettingsService>()
					.AddSingleton<IAppearanceSettingsService, AppearanceSettingsService>(sp => new AppearanceSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IGeneralSettingsService, GeneralSettingsService>(sp => new GeneralSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IFoldersSettingsService, FoldersSettingsService>(sp => new FoldersSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IDevToolsSettingsService, DevToolsSettingsService>(sp => new DevToolsSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IApplicationSettingsService, ApplicationSettingsService>(sp => new ApplicationSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IInfoPaneSettingsService, InfoPaneSettingsService>(sp => new InfoPaneSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<ILayoutSettingsService, LayoutSettingsService>(sp => new LayoutSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IAppSettingsService, AppSettingsService>(sp => new AppSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IActionsSettingsService, ActionsSettingsService>(sp => new ActionsSettingsService(((UserSettingsService)sp.GetRequiredService<IUserSettingsService>()).GetSharingContext()))
					.AddSingleton<IFileTagsSettingsService, FileTagsSettingsService>()
					// Contexts
					.AddSingleton<IMultiPanesContext, MultiPanesContext>()
					.AddSingleton<IContentPageContext, ContentPageContext>()
					.AddSingleton<IDisplayPageContext, DisplayPageContext>()
					.AddSingleton<IHomePageContext, HomePageContext>()
					.AddSingleton<IWindowContext, WindowContext>()
					.AddSingleton<IMultitaskingContext, MultitaskingContext>()
					.AddSingleton<ITagsContext, TagsContext>()
					.AddSingleton<ISidebarContext, SidebarContext>()
					// Services
					.AddSingleton(Ioc.Default)
					.AddSingleton<IWindowsRecentItemsService, WindowsRecentItemsService>()
					.AddSingleton<IWindowsIniService, WindowsIniService>()
					.AddSingleton<IWindowsWallpaperService, WindowsWallpaperService>()
					.AddSingleton<IWindowsSecurityService, WindowsSecurityService>()
					.AddSingleton<IAppThemeModeService, AppThemeModeService>()
					.AddSingleton<IDialogService, DialogService>()
					.AddSingleton<ICommonDialogService, CommonDialogService>()
					.AddSingleton<IImageService, ImagingService>()
					.AddSingleton<IThreadingService, ThreadingService>()
					.AddSingleton<ILocalizationService, LocalizationService>()
					.AddSingleton<ICloudDetector, CloudDetector>()
					.AddSingleton<IFileTagsService, FileTagsService>()
					.AddSingleton<ICommandManager, CommandManager>()
					.AddSingleton<IModifiableCommandManager, ModifiableCommandManager>()
					.AddSingleton<IStorageService, NativeStorageLegacyService>()
					.AddSingleton<IFtpStorageService, FtpStorageService>()
					.AddSingleton<IAddItemService, AddItemService>()
					.AddSingleton<IPreviewPopupService, PreviewPopupService>()
					.AddSingleton<IDateTimeFormatterFactory, DateTimeFormatterFactory>()
					.AddSingleton<IDateTimeFormatter, UserDateTimeFormatter>()
					.AddSingleton<ISizeProvider, UserSizeProvider>()
					.AddSingleton<IQuickAccessService, QuickAccessService>()
					.AddSingleton<IResourcesService, ResourcesService>()
					.AddSingleton<IWindowsJumpListService, WindowsJumpListService>()
					.AddSingleton<IStorageTrashBinService, StorageTrashBinService>()
					.AddSingleton<IRemovableDrivesService, RemovableDrivesService>()
					.AddSingleton<INetworkService, NetworkService>()
					.AddSingleton<IStartMenuService, StartMenuService>()
					.AddSingleton<IStorageCacheService, StorageCacheService>()
					.AddSingleton<IStorageArchiveService, StorageArchiveService>()
					.AddSingleton<IStorageSecurityService, StorageSecurityService>()
					.AddSingleton<IWindowsCompatibilityService, WindowsCompatibilityService>()
					// IPC system
					.AddSingleton<RpcMethodRegistry>()
					.AddSingleton<WebSocketAppCommunicationService>()
					.AddSingleton<NamedPipeAppCommunicationService>()
					.AddSingleton<IAppCommunicationService, MultiTransportCommunicationService>()
					.AddSingleton<IIpcShellRegistry, IpcShellRegistry>()
					.AddSingleton<IWindowResolver, WindowResolver>()
					.AddSingleton<IpcCoordinator>()
					// ViewModels
					.AddSingleton<MainPageViewModel>()
					.AddSingleton<InfoPaneViewModel>()
					.AddSingleton<SidebarViewModel>()
					.AddSingleton<DrivesViewModel>()
					.AddSingleton<ShelfViewModel>()
					.AddSingleton<StatusCenterViewModel>()
					.AddSingleton<AppearanceViewModel>()
					.AddTransient<HomeViewModel>()
					.AddSingleton<QuickAccessWidgetViewModel>()
					.AddSingleton<DrivesWidgetViewModel>()
					.AddSingleton<NetworkLocationsWidgetViewModel>()
					.AddSingleton<FileTagsWidgetViewModel>()
					.AddSingleton<RecentFilesWidgetViewModel>()
					.AddSingleton<ReleaseNotesViewModel>()
					// Utilities
					.AddSingleton<QuickAccessManager>()
					.AddSingleton<StorageHistoryWrapper>()
					.AddSingleton<FileTagsManager>()
					.AddSingleton<LibraryManager>()
					.AddSingleton<AppModel>()
				);

			// Conditional DI
			if (AppEnvironment is AppEnvironment.SideloadPreview or AppEnvironment.SideloadStable)
				builder.ConfigureServices(s => s.AddSingleton<IUpdateService, SideloadUpdateService>());
			else if (AppEnvironment is AppEnvironment.StorePreview or AppEnvironment.StoreStable)
				builder.ConfigureServices(s => s.AddSingleton<IUpdateService, StoreUpdateService>());
			else
				builder.ConfigureServices(s => s.AddSingleton<IUpdateService, DummyUpdateService>());

			return builder.Build();
		}

		/// <summary>
		/// Saves saves all opened tabs to the app cache.
		/// </summary>
		public static void SaveSessionTabs()
		{
			var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();

			userSettingsService.GeneralSettingsService.LastSessionTabList = MainPageViewModel.AppInstances.DefaultIfEmpty().Select(tab =>
			{
				if (tab is not null && tab.NavigationParameter is not null)
				{
					return tab.NavigationParameter.Serialize();
				}
				else
				{
					return "";
				}
			})
			.ToList();
		}

		/// <summary>
		/// Shows exception on the Debug Output and sends Toast Notification to the Windows Notification Center.
		/// </summary>
		public static void HandleAppUnhandledException(Exception? ex, bool showToastNotification)
		{
			var generalSettingsService = Ioc.Default.GetRequiredService<IGeneralSettingsService>();

			StringBuilder formattedException = new()
			{
				Capacity = 200
			};

			formattedException.AppendLine("--------- UNHANDLED EXCEPTION ---------");

			if (ex is not null)
			{
				try
				{
					// Mark as unhandled for Sentry
					ex.Data[Mechanism.HandledKey] = false;
					ex.Data[Mechanism.MechanismKey] = "Application.UnhandledException";
				}
				catch (Exception exData)
				{
					App.Logger?.LogTrace(exData, "Failed to set exception data for Sentry");
				}

				// Capture with highest severity
				SentrySdk.CaptureException(ex, scope =>
				{
					scope.User.Id = generalSettingsService?.UserId;
					scope.Level = SentryLevel.Fatal;
				});

				Exception primary = ex;
				// Flatten aggregate exceptions so we log all inner exceptions
				List<Exception> all = new();
				if (ex is AggregateException aggr)
				{
					var flat = aggr.Flatten();
					primary = flat.InnerExceptions.FirstOrDefault() ?? aggr;
					all.AddRange(flat.InnerExceptions);
				}
				else
				{
					all.Add(primary);
				}

				formattedException.AppendLine($">>>> HRESULT: {ex.HResult}");

				if (!string.IsNullOrWhiteSpace(primary.Message))
				{
					formattedException.AppendLine("--- MESSAGE ---");
					formattedException.AppendLine(primary.Message);
				}

				if (!string.IsNullOrWhiteSpace(primary.StackTrace))
				{
					formattedException.AppendLine("--- STACKTRACE ---");
					formattedException.AppendLine(primary.StackTrace);
				}

				if (!string.IsNullOrWhiteSpace(primary.Source))
				{
					formattedException.AppendLine("--- SOURCE ---");
					formattedException.AppendLine(primary.Source);
				}

				// Log all inner/aggregate exceptions (excluding the primary already logged above)
				if (all.Count > 1 || primary.InnerException is not null)
				{
					formattedException.AppendLine("--- INNER EXCEPTIONS ---");
					int idx = 0;
					foreach (var inner in all)
					{
						if (ReferenceEquals(inner, primary))
							continue;
						formattedException.AppendLine($"[{idx++}] {inner.GetType().FullName}: {inner.Message}");
						if (!string.IsNullOrWhiteSpace(inner.StackTrace))
							formattedException.AppendLine(inner.StackTrace);
					}
					if (primary.InnerException is not null && !all.Contains(primary.InnerException))
					{
						formattedException.AppendLine($"[Inner] {primary.InnerException}");
					}
				}
			}
			else
			{
				formattedException.AppendLine("Exception data is not available.");
			}

			formattedException.AppendLine("---------------------------------------");

			Debug.WriteLine(formattedException.ToString());

			// Only break if a debugger is attached to avoid prompting end users.
			// Wrap in DEBUG directive to prevent breaking in release builds
#if DEBUG
			if (Debugger.IsAttached)
				Debugger.Break();
#endif

			// Save the current tab list in case it was overwritten by another instance
			SaveSessionTabs();
			App.Logger?.LogError(ex, ex?.Message ?? "An unhandled error occurred.");

			// Show toast if requested but do not short‑circuit restart logic.
			if (showToastNotification)
			{
				SafetyExtensions.IgnoreExceptions(() =>
				{
					AppToastNotificationHelper.ShowUnhandledExceptionToast();
				});
			}

			// Restart the app attempting to restore tabs (unless we detect a crash loop)
			try
			{
				var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
				var lastSessionTabList = userSettingsService.GeneralSettingsService.LastSessionTabList;

				if (userSettingsService.GeneralSettingsService.LastCrashedTabList?.SequenceEqual(lastSessionTabList) ?? false)
				{
					// Avoid infinite restart loop
					userSettingsService.GeneralSettingsService.LastSessionTabList = null;
				}
				else
				{
					userSettingsService.AppSettingsService.RestoreTabsOnStartup = true;
					userSettingsService.GeneralSettingsService.LastCrashedTabList = lastSessionTabList;

					// Try to re-launch and start over (best effort, do not await indefinitely)
					MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
					{
						try
						{
							await Launcher.LaunchUriAsync(new Uri("files-dev:"));
						}
						catch (Exception ex)
						{
							App.Logger?.LogError(ex, "Failed to restart app via Launcher.LaunchUriAsync after crash.");
						}
					})
					.Wait(100);
				}
			}
			catch (Exception restartEx)
			{
				App.Logger?.LogError(restartEx, "Failed while attempting auto-restart after unhandled exception.");
			}
			finally
			{
				// Give Sentry a brief moment to flush events (best effort, non-blocking long)
				try { SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
				
				// DESIGN DECISION: Abrupt Process Termination
				// ============================================
				// We use Process.Kill() instead of Environment.Exit() or Application.Exit() because:
				// 1. This is an unhandled exception handler - the app state may be corrupted
				// 2. Graceful shutdown methods might hang or fail in corrupted state
				// 3. We've already attempted restart and logged telemetry - immediate termination is safest
				// 4. Any important data should have been saved during the restart attempt above
				// 
				// Alternative approaches considered:
				// - Environment.Exit(): May hang if finalizers are corrupted
				// - Application.Exit(): May not work if UI thread is corrupted
				// - Natural termination: May leave process hanging indefinitely
				//
				// Risk mitigation: The restart logic above saves session state before this point.
				Process.GetCurrentProcess().Kill();
			}
		}

		/// <summary>
		/// Updates the visibility of the system tray icon
		/// </summary>
		private static void GeneralSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (sender is not IGeneralSettingsService generalSettingsService)
				return;

			if (e.PropertyName == nameof(IGeneralSettingsService.ShowSystemTrayIcon))
			{
				if (generalSettingsService.ShowSystemTrayIcon)
					App.SystemTrayIcon?.Show();
				else
					App.SystemTrayIcon?.Hide();
			}
		}
	}
}
