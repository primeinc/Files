// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Files.App.Communication;
using Files.App.Helpers.Application;
using Files.App.Services.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.ViewModels.Settings
{
	public sealed partial class IpcViewModel : ObservableObject
	{
		private readonly ILogger<IpcViewModel> _logger = Ioc.Default.GetRequiredService<ILogger<IpcViewModel>>();
		private readonly IAppCommunicationService _wsService = Ioc.Default.GetRequiredService<IAppCommunicationService>();

		[ObservableProperty]
		private bool _isEnabled;

		[ObservableProperty]
		private string _token = string.Empty;

		public IpcViewModel()
		{
			// Initialize from store
			IsEnabled = ProtectedTokenStore.IsEnabled();
			_ = LoadTokenAsync();
		}

		partial void OnIsEnabledChanged(bool value)
		{
			try
			{
				ProtectedTokenStore.SetEnabled(value);

				if (value)
					_ = _wsService.StartAsync();
				else
					_ = _wsService.StopAsync();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to toggle IPC service");
			}
		}

		public async Task LoadTokenAsync()
		{
			try
			{
				Token = await ProtectedTokenStore.GetOrCreateTokenAsync();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to load token");
			}
		}

		[RelayCommand]
		private async Task RotateTokenAsync()
		{
			try
			{
				Token = await ProtectedTokenStore.RotateTokenAsync();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to rotate token");
			}
		}

		[RelayCommand]
		private void CopyToken()
		{
			try
			{
				if (!string.IsNullOrWhiteSpace(Token))
				{
					var data = new DataPackage();
					data.SetText(Token);
					Clipboard.SetContent(data);
					Clipboard.Flush();

				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to copy token to clipboard");
			}
		}
	}
}
