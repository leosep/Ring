using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LibVLCSharp.Shared;

namespace RingControl.App.Controls;

/// <summary>
/// Hosts the LibVLC video output in a child HWND that belongs to the WPF window.
/// LibVLC draws straight into that handle, so it never creates its own top-level
/// window and the video always stays embedded in the app.
/// </summary>
public sealed class VideoSurface : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SsBlackRect = 0x00000004;
    private const int WmEraseBkgnd = 0x0014;
    private const string WindowClass = "static";

    public static readonly DependencyProperty MediaPlayerProperty = DependencyProperty.Register(
        nameof(MediaPlayer),
        typeof(MediaPlayer),
        typeof(VideoSurface),
        new PropertyMetadata(null, OnMediaPlayerChanged));

    private IntPtr _hwnd;

    /// <summary>Player whose video output is drawn into this surface.</summary>
    public MediaPlayer? MediaPlayer
    {
        get => (MediaPlayer?)GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0,
            WindowClass,
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings | SsBlackRect,
            0,
            0,
            0,
            0,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        AttachPlayer();

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DetachPlayer();

        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The static control would paint white on every resize; VLC fills the
        // surface itself, so suppressing the background erase avoids flicker.
        if (msg == WmEraseBkgnd)
        {
            handled = true;
            return new IntPtr(1);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void OnMediaPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (VideoSurface)d;
        surface.DetachPlayer();
        surface.AttachPlayer();
    }

    private void AttachPlayer()
    {
        var player = MediaPlayer;
        if (player is not null && _hwnd != IntPtr.Zero)
        {
            player.Hwnd = _hwnd;
        }
    }

    private void DetachPlayer()
    {
        try
        {
            if (MediaPlayer is { } player && player.Hwnd == _hwnd)
            {
                player.Hwnd = IntPtr.Zero;
            }
        }
        catch
        {
            // The player may already be disposing; nothing to detach.
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
