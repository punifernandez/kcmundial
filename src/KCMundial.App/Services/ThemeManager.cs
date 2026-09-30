using System.Windows;

namespace KCMundial.App.Services;

/// <summary>
/// Cambia el tema visual (colores, textos, fondo) en vivo. Los estilos comunes están en Themes/AppTheme.xaml y
/// leen los recursos del tema con DynamicResource.
/// </summary>
public static class ThemeManager
{
    public const string Mundialista = "mundialista";
    public const string Plano = "plano";
    private const string ThemeFolder = "Themes/";

    public static string Normalize(string? name) =>
        string.Equals(name?.Trim(), Plano, StringComparison.OrdinalIgnoreCase) ? Plano : Mundialista;

    public static void Apply(string? name)
    {
        var theme = Normalize(name);
        var file = theme == Plano ? "Plano.xaml" : "Mundialista.xaml";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.EndsWith("Mundialista.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Plano.xaml", StringComparison.OrdinalIgnoreCase)));
        var next = new ResourceDictionary { Source = new Uri(ThemeFolder + file, UriKind.Relative) };
        if (current != null)
            dictionaries[dictionaries.IndexOf(current)] = next;
        else
            dictionaries.Add(next);
    }
}
