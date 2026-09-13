using System;
using System.IO;

namespace Maple2.Server.Web.Helpers;

public static class PlayerTheme {
    private static readonly Lazy<string> Content = new(() => {
        using Stream stream = typeof(PlayerTheme).Assembly.GetManifestResourceStream("Maple2.Server.Web.PlayerTheme.css")
            ?? throw new InvalidOperationException("The embedded player theme is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    });

    public static string Css => Content.Value;
}
