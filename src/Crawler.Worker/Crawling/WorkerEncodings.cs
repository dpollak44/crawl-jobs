using System.Text;

namespace Crawler.Worker.Crawling;

/// <summary>
/// .NET on Linux knows only the Unicode encodings unless the legacy code pages (windows-1252, iso-8859-x, …)
/// are registered. The worker registers them once at start-up; a page that declares one is then decoded as it says.
/// </summary>
public static class WorkerEncodings
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
