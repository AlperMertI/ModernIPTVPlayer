using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using WinRT;

namespace MpvWinUI.Common;

public unsafe class RenderControl : OpenGLRenderControlBase<FrameBuffer>
{
    private SwapChainPanel _swapChainPanel;

    public ContextSettings Setting { get; set; } = new ContextSettings();
    public RenderContext Context { get; private set; }

    public event EventHandler Ready;
    public event Action<TimeSpan> Render;

    public double ScaleX => _swapChainPanel?.CompositionScaleX ?? 1;
    public double ScaleY => _swapChainPanel?.CompositionScaleY ?? 1;

    public bool ResizeRequested { get; set; }
    public bool BufferNeedsLoading { get; set; } = true;
    public int BufferWidth => _capturedWidth;
    public int BufferHeight => _capturedHeight;
    public double BufferScaleX => _capturedScaleX;
    public double BufferScaleY => _capturedScaleY;

    private int _capturedWidth;
    private int _capturedHeight;
    private double _capturedScaleX;
    private double _capturedScaleY;

    public RenderControl()
    {
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    public override void Initialize()
    {
        if (Context == null)
        {
            base.Initialize();
            Context = new RenderContext(Setting);
            _swapChainPanel = new SwapChainPanel();
            _swapChainPanel.CompositionScaleChanged += OnCompositionScaleChanged;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Content = _swapChainPanel;

            // Step 3: Capture initial dimensions for background loading
            _capturedWidth = (int)ActualWidth;
            _capturedHeight = (int)ActualHeight;
            _capturedScaleX = ScaleX;
            _capturedScaleY = ScaleY;
            BufferNeedsLoading = true;

            Ready?.Invoke(this, EventArgs.Empty);
        }
    }

    public int GetBufferHandle()
        => FrameBuffer.GLFrameBufferHandle;

    protected override void Draw()
    {
        FrameBuffer.Begin(FrameBuffer.CurrentBufferIndex);
        Render?.Invoke(_stopwatch.Elapsed - _lastFrameStamp);
        FrameBuffer.End();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Release();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Context != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _capturedWidth = (int)e.NewSize.Width;
            _capturedHeight = (int)e.NewSize.Height;
            _capturedScaleX = ScaleX;
            _capturedScaleY = ScaleY;

            ResizeRequested = true; // Defer to rendering thread
        }
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        _capturedScaleX = ScaleX;
        _capturedScaleY = ScaleY;
        ResizeRequested = true; // Defer to rendering thread
    }

    public void UpdateFrameBufferSize()
    {
        ResizeRequested = false;
        if (FrameBuffer != null)
        {
            System.Diagnostics.Debug.WriteLine($"[RenderControl] Updating FrameBuffer size to {_capturedWidth}x{_capturedHeight} (Scale: {_capturedScaleX}x{_capturedScaleY})");
            FrameBuffer.UpdateSize(_capturedWidth, _capturedHeight, _capturedScaleX, _capturedScaleY);
        }
    }

    public void CreateFrameBufferOnCurrentThread()
    {
        BufferNeedsLoading = false;
        if (FrameBuffer != null) return;
        
        FrameBuffer = new FrameBuffer(Context, _capturedWidth, _capturedHeight, _capturedScaleX, _capturedScaleY);
        // We still need to call SetSwapChain on UI thread, but we'll do that via a callback or handled in Player
    }
}
