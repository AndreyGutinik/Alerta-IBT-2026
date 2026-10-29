using System.Diagnostics;
using System.Numerics;
using AlertaIBT.Core.Ndi;
using Microsoft.Extensions.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AlertaIBT.App.Ndi;

/// <summary>
/// Draws the newest frame of a <see cref="FrameMailbox"/> on a window with Direct3D 11: a swap chain presented with VSync, one reusable BGRA texture
/// (recreated only when the picture size changes), one full-screen triangle and a bilinear sampler. It paints only when a new frame arrived or the
/// picture moved (<see cref="RequestRepaint"/>, <see cref="SetPosition"/>), and the crop ("preencher e cortar", see <see cref="NdiCropMath"/>) is just
/// the texture coordinates of that triangle, so nothing is resampled on the CPU. It runs on its own thread and never touches the UI thread.
/// </summary>
public sealed class D3dVideoRenderer : IDisposable
{
    private const string Shader = """
        cbuffer Crop : register(b0) { float4 rect; };
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VSOut VS(uint id : SV_VertexID)
        {
            float2 t = float2((id << 1) & 2, id & 2);
            VSOut o;
            o.pos = float4(t.x * 2 - 1, 1 - t.y * 2, 0, 1);
            o.uv = float2(lerp(rect.x, rect.z, t.x), lerp(rect.y, rect.w, t.y));
            return o;
        }
        Texture2D tex : register(t0);
        SamplerState smp : register(s0);
        float4 PS(VSOut i) : SV_Target { return float4(tex.Sample(smp, i.uv).rgb, 1); }
        """;

    private readonly IntPtr _hwnd;
    private readonly FrameMailbox _mailbox;
    private readonly ILogger? _logger;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _dirty;
    private double _position = 50;
    private int _targetWidth;
    private int _targetHeight;
    private int _appliedWidth;
    private int _appliedHeight;
    private long _presented;
    private long _lastRenderTicks;
    private string? _error;

    /// <param name="hwnd">The window to present on (its client area is the target).</param>
    /// <param name="width">The target width in pixels (the monitor).</param>
    /// <param name="height">The target height in pixels.</param>
    /// <param name="mailbox">Where the newest frame comes from.</param>
    /// <param name="logger">Log.</param>
    public D3dVideoRenderer(IntPtr hwnd, int width, int height, FrameMailbox mailbox, ILogger? logger = null)
    {
        _hwnd = hwnd;
        _mailbox = mailbox;
        _logger = logger;
        _targetWidth = width;
        _targetHeight = height;
        _thread = new Thread(Run) { IsBackground = true, Name = "NDI render", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Pictures presented so far.</summary>
    public long FramesPresented => Interlocked.Read(ref _presented);

    /// <summary>How long the last paint took on the render thread, in milliseconds (the wait for the VSync is not counted).</summary>
    public double LastRenderMs => new TimeSpan(Interlocked.Read(ref _lastRenderTicks)).TotalMilliseconds;

    /// <summary>Why the GPU path failed (no Direct3D 11, shader error), or <c>null</c>.</summary>
    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>Starts the render thread.</summary>
    public void Start() => _thread.Start();

    /// <summary>Tells the renderer a new frame is waiting in the mailbox.</summary>
    public void Wake() => _wake.Set();

    /// <summary>Paints again with the stored picture (the position or the target changed).</summary>
    public void RequestRepaint()
    {
        _dirty = true;
        _wake.Set();
    }

    /// <summary>Moves the visible band (0 top .. 100 bottom) and repaints.</summary>
    public void SetPosition(double position)
    {
        lock (_gate)
        {
            _position = position;
        }

        RequestRepaint();
    }

    /// <summary>The window changed size (another monitor): the swap chain is resized before the next paint.</summary>
    public void SetTargetSize(int width, int height)
    {
        lock (_gate)
        {
            _targetWidth = width;
            _targetHeight = height;
        }

        RequestRepaint();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            using var gpu = new Gpu(_hwnd, _targetWidth, _targetHeight);
            _appliedWidth = _targetWidth;
            _appliedHeight = _targetHeight;
            long lastSequence = 0;
            int sourceWidth = 0;
            int sourceHeight = 0;
            double position;
            var timer = Stopwatch.StartNew();
            while (!_stop)
            {
                _wake.WaitOne(250);
                if (_stop)
                {
                    break;
                }

                int tw;
                int th;
                lock (_gate)
                {
                    tw = _targetWidth;
                    th = _targetHeight;
                    position = _position;
                }

                var started = Stopwatch.GetTimestamp();
                var repaint = _dirty;
                _dirty = false;
                if (tw != _appliedWidth || th != _appliedHeight)
                {
                    gpu.Resize(tw, th);
                    _appliedWidth = tw;
                    _appliedHeight = th;
                    repaint = true;
                }

                if (_mailbox.TryAcquire(lastSequence, out var frame))
                {
                    try
                    {
                        gpu.Upload(frame);
                    }
                    finally
                    {
                        _mailbox.Release();
                    }

                    lastSequence = frame.Sequence;
                    sourceWidth = frame.Width;
                    sourceHeight = frame.Height;
                    position = frame.VerticalPosition;
                    lock (_gate)
                    {
                        _position = position;
                    }

                    repaint = true;
                }

                if (repaint && sourceWidth > 0)
                {
                    var crop = NdiCropMath.Compute(sourceWidth, sourceHeight, tw, th, position);
                    gpu.Draw(crop);
                    Interlocked.Exchange(ref _lastRenderTicks, Stopwatch.GetElapsedTime(started).Ticks);
                    gpu.Present(); // blocks until the vertical blank (VSync)
                    Interlocked.Increment(ref _presented);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                _error = ex.Message;
            }

            _logger?.LogError(ex, "O renderizador Direct3D do NDI falhou");
        }
    }

    // The Direct3D objects, created and used on the render thread only.
    private sealed class Gpu : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGISwapChain1 _swapChain;
        private readonly ID3D11VertexShader _vs;
        private readonly ID3D11PixelShader _ps;
        private readonly ID3D11SamplerState _sampler;
        private readonly ID3D11Buffer _constants;
        private ID3D11RenderTargetView _rtv;
        private ID3D11Texture2D? _texture;
        private ID3D11ShaderResourceView? _srv;
        private int _textureWidth;
        private int _textureHeight;
        private int _width;
        private int _height;

        public Gpu(IntPtr hwnd, int width, int height)
        {
            _width = width;
            _height = height;
            var result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out _device!,
                out _,
                out _context!);
            result.CheckError();

            using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
            using (var factory = adapter.GetParent<IDXGIFactory2>())
            {
                // Blit-model swap chain: the only kind a layered (click-through) window can present.
                var description = new SwapChainDescription1
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 1,
                    BufferUsage = Usage.RenderTargetOutput,
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.Discard,
                    Scaling = Scaling.Stretch,
                    AlphaMode = AlphaMode.Ignore,
                };
                _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, description);
                factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            }

            _rtv = CreateTarget();

            var vsBlob = Compile("VS", "vs_4_0");
            var psBlob = Compile("PS", "ps_4_0");
            _vs = _device.CreateVertexShader(vsBlob);
            _ps = _device.CreatePixelShader(psBlob);
            _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _constants = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        }

        public void Resize(int width, int height)
        {
            _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            _rtv.Dispose();
            _context.Flush();
            _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
            _width = width;
            _height = height;
            _rtv = CreateTarget();
        }

        public void Upload(in MailboxFrame frame)
        {
            if (_texture is null || _textureWidth != frame.Width || _textureHeight != frame.Height)
            {
                _srv?.Dispose();
                _texture?.Dispose();
                _texture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)frame.Width,
                    Height = (uint)frame.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
                _srv = _device.CreateShaderResourceView(_texture);
                _textureWidth = frame.Width;
                _textureHeight = frame.Height;
            }

            _context.UpdateSubresource(new ReadOnlySpan<byte>(frame.Buffer, 0, frame.Stride * frame.Height), _texture, 0, (uint)frame.Stride, 0);
        }

        public void Draw(NdiCrop crop)
        {
            var rect = new Vector4((float)crop.U0, (float)crop.V0, (float)crop.U1, (float)crop.V1);
            _context.UpdateSubresource(in rect, _constants);
            _context.OMSetRenderTargets(_rtv);
            _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.VSSetConstantBuffer(0, _constants);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResource(0, _srv!);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);
        }

        public void Present() => _swapChain.Present(1, PresentFlags.None);

        public void Dispose()
        {
            _context.ClearState();
            _srv?.Dispose();
            _texture?.Dispose();
            _constants.Dispose();
            _sampler.Dispose();
            _ps.Dispose();
            _vs.Dispose();
            _rtv.Dispose();
            _swapChain.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        private ID3D11RenderTargetView CreateTarget()
        {
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            return _device.CreateRenderTargetView(back);
        }

        private static byte[] Compile(string entry, string profile)
        {
            Compiler.Compile(Shader, null!, null!, entry, "ndi.hlsl", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None, out var blob, out var errors);
            using (errors)
            {
                if (blob is null)
                {
                    throw new InvalidOperationException("Shader: " + (errors is null ? "erro" : System.Text.Encoding.UTF8.GetString(errors.AsSpan())));
                }
            }

            using (blob)
            {
                return blob.AsSpan().ToArray();
            }
        }
    }
}
