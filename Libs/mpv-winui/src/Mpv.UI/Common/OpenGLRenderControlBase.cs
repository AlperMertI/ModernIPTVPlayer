using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;

namespace MpvWinUI.Common;

public abstract class OpenGLRenderControlBase<TFrame> : ContentControl where TFrame : FrameBufferBase
{
    protected Stopwatch _stopwatch = Stopwatch.StartNew();
    protected TimeSpan _lastRenderTime = TimeSpan.FromSeconds(-1);
    protected TimeSpan _lastFrameStamp;

    public TFrame FrameBuffer { get; set; }

    public virtual void Initialize()
    {
        CompositionTarget.Rendering += OnRendering;
    }

    public virtual void Release()
    {
        CompositionTarget.Rendering -= OnRendering;
    }

    protected abstract void Draw();

    protected void SafeDraw()
    {
        if (Environment.CurrentManagedThreadId == 1) // Assuming 1 is UI thread
        {
             // Debug.WriteLine("[OpenGLRenderControlBase] Draw attempted on UI thread. Skipping.");
             // return; 
        }
        Draw();
    }

    private void InvalidateVisual()
    {
        if (FrameBuffer != null)
        {
            SafeDraw();
            _stopwatch.Restart();
        }
    }

    private bool _renderRequested = false;
    public bool ContinuousRendering { get; set; } = true;

    public void RequestRender()
    {
        _renderRequested = true;
    }

    private void OnRendering(object sender, object e)
    {
        var args = (RenderingEventArgs)e;

        if (_lastRenderTime != args.RenderingTime)
        {
            if (ContinuousRendering || _renderRequested)
            {
                _renderRequested = false;
                InvalidateVisual();
                _lastRenderTime = args.RenderingTime;
            }
        }
    }
}
