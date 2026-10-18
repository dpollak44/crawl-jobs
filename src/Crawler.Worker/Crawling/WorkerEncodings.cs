using System.Text;

namespace Crawler.Worker.Crawling;

/// <summary>
/// .NET on Linux knows only the Unicode encodings unless the legacy code pages (windows-1252, iso-8859-x, …)
/// are registered. The worker registers them at start-up; a page that declares one is then decoded as it says.
/// Registering twice is harmless, so tests may call it too.
/// </summary>
public static class WorkerEncodings
{
    public static void EnsureRegistered() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
