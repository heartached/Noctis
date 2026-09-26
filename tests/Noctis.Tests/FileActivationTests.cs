using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// macOS "Open With Noctis", Finder double-clicks and Dock-icon drops arrive as an
/// open-documents event (IActivatableLifetime.Activated + FileActivatedEventArgs), not
/// argv or the single-instance pipe. Nothing subscribed, so none of them played.
/// Both Avalonia interfaces are [NotClientImplementable]; the fakes are DispatchProxies.
/// </summary>
public class FileActivationTests
{
    public class FakeLifetime : DispatchProxy
    {
        public EventHandler<ActivatedEventArgs>? Activated;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "add_Activated": Activated += (EventHandler<ActivatedEventArgs>)args![0]!; return null;
                case "remove_Activated": Activated -= (EventHandler<ActivatedEventArgs>)args![0]!; return null;
                case "add_Deactivated" or "remove_Deactivated": return null;
                default: throw new NotSupportedException(method?.Name);
            }
        }

        public void Raise(ActivatedEventArgs e) => Activated?.Invoke(this, e);
    }

    public class FakeItem : DispatchProxy
    {
        public Uri Path = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Path" => Path,
            "get_Name" => System.IO.Path.GetFileName(Path.LocalPath),
            "Dispose" => null,
            _ => throw new NotSupportedException(method?.Name),
        };
    }

    private static (IActivatableLifetime Lifetime, FakeLifetime Fake) NewLifetime()
    {
        var lifetime = DispatchProxy.Create<IActivatableLifetime, FakeLifetime>();
        return (lifetime, (FakeLifetime)(object)lifetime);
    }

    private static IStorageItem Item(Uri uri)
    {
        var item = DispatchProxy.Create<IStorageItem, FakeItem>();
        ((FakeItem)(object)item).Path = uri;
        return item;
    }

    private static readonly string TrackA = Path.Combine(Path.GetTempPath(), "Noctis Album", "01 a.flac");
    private static readonly string TrackB = Path.Combine(Path.GetTempPath(), "Noctis Album", "02 b.mp3");

    [Fact]
    public void File_activation_hands_the_local_paths_to_open()
    {
        var (lifetime, fake) = NewLifetime();
        var opened = new List<IReadOnlyList<string>>();

        Assert.NotNull(FileActivation.Subscribe(lifetime, opened.Add));
        fake.Raise(new FileActivatedEventArgs(new[] { Item(new Uri(TrackA)), Item(new Uri(TrackB)) }));

        var files = Assert.Single(opened);
        Assert.Equal(new[] { TrackA, TrackB }, files);
    }

    [Fact]
    public void Non_file_activations_and_non_local_items_open_nothing()
    {
        var (lifetime, fake) = NewLifetime();
        var opened = new List<IReadOnlyList<string>>();
        FileActivation.Subscribe(lifetime, opened.Add);

        // Clicking the Dock icon / app switching raise Reopen / Background activations.
        fake.Raise(new ActivatedEventArgs(ActivationKind.Reopen));
        fake.Raise(new ActivatedEventArgs(ActivationKind.Background));
        fake.Raise(new ProtocolActivatedEventArgs(new Uri("noctis://open")));
        fake.Raise(new FileActivatedEventArgs(new[] { Item(new Uri("https://example.com/a.mp3")) }));

        Assert.Empty(opened);
    }

    [Fact]
    public void Detach_stops_delivery()
    {
        var (lifetime, fake) = NewLifetime();
        var opened = new List<IReadOnlyList<string>>();

        var detach = FileActivation.Subscribe(lifetime, opened.Add)!;
        detach();
        fake.Raise(new FileActivatedEventArgs(new[] { Item(new Uri(TrackA)) }));

        Assert.Empty(opened);
        Assert.Null(fake.Activated);
    }

    [Fact]
    public void No_activatable_lifetime_means_no_subscription()
    {
        // Windows and Linux: files come through argv and the single-instance pipe.
        Assert.Null(FileActivation.Subscribe(null, _ => throw new InvalidOperationException()));
        Assert.Null(FileActivation.SubscribeReopen(null, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Reopen_activation_calls_reopen_and_nothing_else_does()
    {
        // Audit P30: a Dock-icon click (or relaunch) on a window hidden into the tray
        // raised Reopen and nobody listened, so the window stayed hidden.
        var (lifetime, fake) = NewLifetime();
        var reopened = 0;

        Assert.NotNull(FileActivation.SubscribeReopen(lifetime, () => reopened++));
        fake.Raise(new ActivatedEventArgs(ActivationKind.Background));
        fake.Raise(new ProtocolActivatedEventArgs(new Uri("noctis://open")));
        fake.Raise(new FileActivatedEventArgs(new[] { Item(new Uri(TrackA)) }));
        Assert.Equal(0, reopened);

        fake.Raise(new ActivatedEventArgs(ActivationKind.Reopen));
        Assert.Equal(1, reopened);
    }

    [Fact]
    public void Reopen_detach_stops_delivery()
    {
        var (lifetime, fake) = NewLifetime();
        var reopened = 0;

        var detach = FileActivation.SubscribeReopen(lifetime, () => reopened++)!;
        detach();
        fake.Raise(new ActivatedEventArgs(ActivationKind.Reopen));

        Assert.Equal(0, reopened);
        Assert.Null(fake.Activated);
    }
}
