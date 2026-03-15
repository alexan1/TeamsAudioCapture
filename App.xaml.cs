using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TeamsAudioCapture;

public partial class App : System.Windows.Application
{
	private IHost? _host;

	private async void Application_Startup(object sender, StartupEventArgs e)
	{
		_host = Host.CreateDefaultBuilder()
			.ConfigureAppConfiguration((context, config) =>
			{
				var localSettingsPath = LocalSettingsPath.GetPath();

				config.SetBasePath(AppContext.BaseDirectory);
				config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
				config.AddJsonFile(localSettingsPath, optional: true, reloadOnChange: true);
			})
			.ConfigureServices((context, services) =>
			{
				Program.ConfigureServices(services, context.Configuration);
				services.AddSingleton<IConfiguration>(context.Configuration);
			})
			.Build();

		await _host.StartAsync();

		var mainWindow = _host.Services.GetRequiredService<MainWindow>();
		mainWindow.Show();
	}

	protected override async void OnExit(ExitEventArgs e)
	{
		if (_host is not null)
		{
			await _host.StopAsync();
			_host.Dispose();
		}

		base.OnExit(e);
	}
}
