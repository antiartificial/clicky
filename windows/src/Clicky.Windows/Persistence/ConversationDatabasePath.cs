using System.IO;

namespace Clicky.Windows.Persistence;

public static class ConversationDatabasePath
{
    public static string GetDefaultPath()
    {
        var localApplicationDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationDataPath))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return Path.Combine(localApplicationDataPath, "Clicky", "clicky.db");
    }
}
