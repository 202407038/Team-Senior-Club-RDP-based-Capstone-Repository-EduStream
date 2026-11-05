using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EduStream.Core.Common;

namespace EduStream.Desktop;

/// <summary>백그라운드 창만 작업 표시줄에서 알린다. 활성화/포커스 이동/소리 재생 없음.</summary>
internal sealed class ChatAttention
{
    public ChatAttention(Window window, System.Windows.Controls.ListBox list, ChatConversations conversations)
    {
        void Update() => conversations.IsReadingActive = window.IsActive && list.IsVisible;
        window.Activated += (_, _) => { Update(); Flash(window, 0); };
        window.Deactivated += (_, _) => Update();
        list.IsVisibleChanged += (_, _) => Update();
        window.Loaded += (_, _) => Update();
        conversations.AttentionRequested += () => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!window.IsActive && window.IsLoaded) Flash(window, 2);
        }));
        Update();
    }
    private static void Flash(Window window, uint flags)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = flags, Count = 3 };
        FlashWindowEx(ref info);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
