using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MarkMello.Applicate.Tests.ApplicateAvaloniaTestApp))]

namespace MarkMello.Applicate.Tests;

public static class ApplicateAvaloniaTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Avalonia.Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
