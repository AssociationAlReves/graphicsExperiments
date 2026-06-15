using System.Windows.Forms;

namespace ComputeSharpVoronoi;

/// <summary>
/// Minimal double-buffered WinForms window. A render-loop callback is driven
/// off Application.Idle so we present as fast as the GPU produces frames.
/// </summary>
internal sealed class VoronoiForm : Form
{
    private System.Drawing.Bitmap? _current;
    public event Action? RenderFrame;

    public VoronoiForm(int width, int height)
    {
        Text = "ComputeSharp — Voronoi Moving Field (JFA, DX12)";
        ClientSize = new System.Drawing.Size(width, height);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;

        Application.Idle += (_, _) =>
        {
            while (IsIdle())
            {
                RenderFrame?.Invoke();
                Invalidate();
            }
        };
    }

    // Win32 peek to know when the message queue is empty (idle).
    private static bool IsIdle() => !NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

    public void Present(System.Drawing.Bitmap bmp)
    {
        _current = bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_current != null)
            e.Graphics.DrawImageUnscaled(_current, 0, 0);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* skip flicker */ }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool PeekMessage(out Msg msg, IntPtr hWnd,
        uint min, uint max, uint remove);
}
