using System.Configuration;
using System.Data;
using System.Windows;

namespace Klicky;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		this.DispatcherUnhandledException += (_, args) =>
		{
			MessageBox.Show($"Unerwarteter Fehler:\n{args.Exception}", "Klicky", MessageBoxButton.OK, MessageBoxImage.Error);
			args.Handled = true;
		};

		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			var ex = args.ExceptionObject as Exception;
			MessageBox.Show($"Fataler Fehler:\n{ex}", "Klicky", MessageBoxButton.OK, MessageBoxImage.Error);
		};

		base.OnStartup(e);
	}
}

