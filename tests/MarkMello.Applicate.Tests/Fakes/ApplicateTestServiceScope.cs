using System;
using System.Reflection;
using MarkMello.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Applicate.Tests.Fakes;

/// <summary>
/// Installs a test-owned <see cref="IServiceProvider"/> into <see cref="App.Services"/> for the
/// duration of a <c>using</c> block, then restores whatever was there before.
///
/// <para><b>Why this exists as ONE type.</b> <see cref="App.Services"/> is process-global mutable
/// state (<c>src/MarkMello.Presentation/App.axaml.cs:20</c>) with a public setter
/// (<c>RegisterServices</c>) that rejects <c>null</c> — so there is no public way to restore the
/// "never registered" state a test process starts in. That requires reflection over the auto-property
/// backing field. Centralising it here means exactly one place carries that fragility, instead of one
/// copy per test file. If Avalonia or the App shape ever changes the property, this single ctor
/// breaks loudly rather than silently leaking a provider into unrelated tests.</para>
///
/// <para>Tests using this MUST be in the non-parallel <see cref="ApplicateAppServicesCollection"/>,
/// because the state being mutated is process-global.</para>
/// </summary>
internal sealed class ApplicateTestServiceScope : IDisposable
{
    private readonly IServiceProvider? _previous;
    private readonly ServiceProvider _provider;
    private bool _disposed;

    public ApplicateTestServiceScope(Action<ServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _previous = App.Services;

        var collection = new ServiceCollection();
        configure(collection);
        _provider = collection.BuildServiceProvider();

        App.RegisterServices(_provider);
    }

    public IServiceProvider Services => _provider;

    public T? GetService<T>() => _provider.GetService<T>() is T value ? value : default;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_previous is not null)
        {
            App.RegisterServices(_previous);
        }
        else
        {
            RestoreToUnregistered();
        }

        _provider.Dispose();
    }

    /// <summary>
    /// Reset <see cref="App.Services"/> to <c>null</c>. <c>App.RegisterServices</c> throws on null by
    /// design, so the auto-property backing field is written directly. Fails loudly if the shape ever
    /// changes rather than leaving a stale provider installed for every later test in the process.
    /// </summary>
    private static void RestoreToUnregistered()
    {
        var property = typeof(App).GetProperty(
            nameof(App.Services),
            BindingFlags.Public | BindingFlags.Static);
        var setter = property?.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(null, new object?[] { null });
            return;
        }

        var field = typeof(App).GetField(
            $"<{nameof(App.Services)}>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (field is null)
        {
            throw new InvalidOperationException(
                "Cannot restore App.Services to its unregistered state: neither a non-public setter "
                + "nor the auto-property backing field was found. App's shape changed; update "
                + "ApplicateTestServiceScope so test providers stop leaking across tests.");
        }

        field.SetValue(null, null);
    }
}
