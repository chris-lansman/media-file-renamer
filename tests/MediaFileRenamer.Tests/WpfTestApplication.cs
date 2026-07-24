using System.Windows;

namespace MediaFileRenamer.Tests;

internal static class WpfTestApplication
{
    public static void EnsureResources()
    {
        var application = Application.Current ?? new Application();
        if (application.TryFindResource("PrimaryButtonStyle") is not null)
        {
            return;
        }

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/MediaFileRenamer;component/Themes/Theme.xaml",
                UriKind.Relative)
        });
    }
}
