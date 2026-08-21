using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;

namespace IptvSuite.PlaybackCompatibilitySpike;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private bool _disposed;

    public MainWindow()
    {
        Core.Initialize();
        InitializeComponent();
        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        PlaybackView.MediaPlayer = _mediaPlayer;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Closed -= MainWindow_Closed;
        PlaybackView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        GC.SuppressFinalize(this);
    }
}
