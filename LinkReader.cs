using System.Text;
using System.Windows.Forms;

namespace GitSync;

/// <summary>Finds the link in something dragged onto the window or pasted into it.</summary>
internal static class LinkReader
{
    /// <summary>
    /// Formats tried in order. The URL formats come first: when a link is
    /// dragged, the plain text may be the link's caption instead of its
    /// address. "UniformResourceLocatorW" is what Chrome and Edge send,
    /// "text/x-moz-url" is Firefox's.
    /// </summary>
    private static readonly string[] Formats =
    [
        "UniformResourceLocatorW",
        "text/x-moz-url",
        "UniformResourceLocator",
        DataFormats.UnicodeText,
        DataFormats.Text
    ];

    /// <summary>The address the drag or the clipboard carries, or null when there is none.</summary>
    internal static string? Read(IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }

        foreach (string format in Formats)
        {
            try
            {
                if (data.GetDataPresent(format)
                    && RepoAddress.FirstLink(TextOf(data.GetData(format), format)) is { } link)
                {
                    return link;
                }
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                // The source application can refuse a format it listed; try the next one.
            }
        }

        // A desktop shortcut dragged from Explorer.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (string file in files.Where(f => f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (RepoAddress.FirstLink(File.ReadAllText(file)) is { } link)
                    {
                        return link;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // An unreadable shortcut is simply not a link.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Formats that are not plain text arrive as raw bytes: UTF-16 for the
    /// "W" and Firefox formats, single-byte for the old URL format.
    /// </summary>
    private static string? TextOf(object? value, string format) => value switch
    {
        string text => text,
        MemoryStream stream when format == "UniformResourceLocator" => Encoding.UTF8.GetString(stream.ToArray()),
        MemoryStream stream => Encoding.Unicode.GetString(stream.ToArray()),
        _ => null
    };
}
