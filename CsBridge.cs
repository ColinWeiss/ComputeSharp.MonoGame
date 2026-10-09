using System;
using ComputeSharp;
using ComputeSharp.Descriptors;
using ComputeSharp.Interop;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Framework.Utilities;
using CsGraphicsDevice = ComputeSharp.GraphicsDevice;
using GraphicsDevice = Microsoft.Xna.Framework.Graphics.GraphicsDevice;

namespace ComputeSharp.MonoGame;

/// <summary>
/// MonoGame(新后端, DirectX12) ⇄ ComputeSharp 的零拷贝计算桥(同设备方案)。
///
/// 通过 CsDeviceInjection 把 ComputeSharp 注入到 MonoGame 的 D3D12 设备上,
/// 双方在同设备的不同命令队列上工作:
///   • 计算输出表面三缓冲轮转 —— 两队列间无隐式同步,第 N+3 次派发重写第 N 帧
///     输出时,MonoGame 对它的采样早已完成(Tinters 桥验证过的时序方案);
///   • 每次派发后 CPU 等待 ComputeSharp 内部完成栅栏,返回即可直接绘制。
///
/// 相比 Tinters 桥(D3D11↔D3D12 跨设备)的简化:没有共享句柄、没有描述符重写、
/// 没有资源换心,只有官方 FromNativeHandle 与一个转发到 CreateCommittedResource
/// 的小分配器(见 CsDeviceInjection)。
/// </summary>
public sealed unsafe class CsBridge : IDisposable
{
  private readonly Microsoft.Xna.Framework.Graphics.GraphicsDevice _mgd;
  private readonly void* _d3d12;                // MonoGame 的 ID3D12Device(借自 GetNativeHandles,不释放)
  private readonly CsGraphicsDevice _csDevice;  // 注入到 MonoGame 设备上的 ComputeSharp 实例
  private readonly ComputeFenceSync _sync;
  private readonly string _reason = "";
  private SharedSurface[] _outputs = [];
  private int _outputIndex;
  private ulong _pendingValue;
  private bool _hasPending;

  public bool IsZeroCopy { get; }
  public string Status { get; }

  public CsBridge(Microsoft.Xna.Framework.Graphics.GraphicsDevice graphicsDevice)
  {
    _mgd = graphicsDevice;
    CsTextures.Bind(this);   // 静态纹理缓存绑定到本桥(单桥假设)

    NativeGraphicsHandles handles = graphicsDevice.GetNativeHandles();
    if (handles.Backend != GraphicsBackend.DirectX12)
    {
      throw new NotSupportedException($"需要 DirectX12 后端,当前为 {handles.Backend}");
    }

    _d3d12 = (void*)handles.LogicalDevice;
    Console.WriteLine($"[COLS] handles: backend={handles.Backend} logical=0x{(IntPtr)_d3d12:X} queue=0x{handles.Queue:X}");

    try
    {
      if (D3D12Interop.GetDeviceRemovedReason(_d3d12) != 0)
      {
        throw new InvalidOperationException("MonoGame 的 D3D12 设备已移除");
      }

      _csDevice = CsDeviceInjection.Inject(_mgd, _d3d12, (void*)handles.PhysicalDevice);
      _sync = new ComputeFenceSync(_csDevice);

      // 启动探针:完整走一遍 分配 → 资源取出 → MonoGame 包装
      using SharedSurface probe = SharedSurface.Create(_mgd, _csDevice, 4, 4);
      IsZeroCopy = true;
    }
    catch (Exception e)
    {
      _reason = $"{e.GetType().Name}: {e.Message}";
      IsZeroCopy = false;
    }

    Status = IsZeroCopy
        ? $"same-device | backend={handles.Backend} | {_csDevice.Name}"
        : $"fallback | {_csDevice?.Name ?? "??"} | {_reason}";
  }

  /// <summary>创建一块计算可写、渲染可读的共享表面。</summary>
  public SharedSurface CreateSurface(int width, int height)
  {
    if (!IsZeroCopy)
    {
      throw new InvalidOperationException($"零拷贝路径不可用: {_reason}");
    }

    return SharedSurface.Create(_mgd, _csDevice, width, height);
  }

  /// <summary>创建三缓冲输出表面(尺寸需与输入一致)。重复调用会先释放旧表面。</summary>
  public void CreateOutputs(int width, int height, int count = 3)
  {
    foreach (SharedSurface surface in _outputs)
    {
      surface.Dispose();
    }

    _outputs = new SharedSurface[count];
    for (int i = 0; i < count; i++)
    {
      _outputs[i] = CreateSurface(width, height);
    }
  }

  /// <summary>把一张 CPU 可读的 MonoGame 纹理上传为 ComputeSharp 原生纹理
  /// (一次性上传,不走共享路径 —— 静态内容无跨队列竞态)。</summary>
  public ReadWriteTexture2D<float4> UploadTexture(Texture2D source)
  {
    var colors = new Color[source.Width * source.Height];
    source.GetData(colors);

    var pixels = new float4[colors.Length];
    for (int i = 0; i < colors.Length; i++)
    {
      pixels[i] = new float4(
          colors[i].R / 255f,
          colors[i].G / 255f,
          colors[i].B / 255f,
          colors[i].A / 255f);
    }

    return _csDevice.AllocateReadWriteTexture2D(pixels, source.Width, source.Height);
  }

  // ————— Texture2D 直传重载:静态缓存查字典,未命中懒上传一次 —————

  /// <summary>同 Process(Texture2D 版):源纹理经静态缓存自动上传。</summary>
  public Texture2D Process<TShader>(Texture2D source, ShaderStage<TShader> stage)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>
  {
    return Process(CsTextures.Get(source), stage);
  }

  /// <summary>同 ProcessSurface(Texture2D 版)。</summary>
  public SharedSurface ProcessSurface<TShader>(Texture2D source, ShaderStage<TShader> stage)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>
  {
    return ProcessSurface(CsTextures.Get(source), stage);
  }

  /// <summary>同 DispatchSurface(Texture2D 版)。</summary>
  public SharedSurface DispatchSurface<TShader>(Texture2D source, ShaderStage<TShader> stage)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>
  {
    return DispatchSurface(CsTextures.Get(source), stage);
  }

  public delegate TShader ShaderStage<TShader>(
      ReadWriteTexture2D<float4> source,
      ReadWriteTexture2D<float4> target)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>;

  /// <summary>Process 的共享表面版本(验证/高级用法)。</summary>
  public SharedSurface ProcessSurface<TShader>(ReadWriteTexture2D<float4> source, ShaderStage<TShader> stage)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>
  {
    if (_outputs.Length == 0)
    {
      throw new InvalidOperationException("先调用 CreateOutputs 创建输出表面");
    }

    SharedSurface surface = _outputs[_outputIndex];
    _outputIndex = (_outputIndex + 1) % _outputs.Length;

    ulong value = _sync.ReadNextValue(_csDevice);
    _csDevice.For(surface.Width, surface.Height, stage(source, surface.Compute));
    _sync.Wait(value);   // 同步模式:返回前确保本次派发已在 GPU 完成
    _hasPending = false;

    return surface;
  }

  // ————— 流水线模式:BeginFrame + Dispatch,稳态零 CPU 停顿,输出滞后一帧 —————

  /// <summary>帧首调用(可选):等待上一帧的派发完成。此时几乎总是早已完成,
  /// 等待本身零成本;把同步点尽量提前,给本帧 CPU 工作让路。</summary>
  public void BeginFrame()
  {
    if (_hasPending)
    {
      _sync.Wait(_pendingValue);
    }
  }

  /// <summary>冲刷流水线:阻塞到所有已提交派发完成(读回数据/退出前用)。</summary>
  public void Flush()
  {
    if (_hasPending)
    {
      _sync.Wait(_pendingValue);
    }
  }

  /// <summary>流水线派发:不等本次派发完成,返回上一帧的输出(已完成,可直接绘制)。
  /// 三缓冲 + "写前等上一帧"的时序与 Tinters 桥一致:任意写与读错开一整帧以上,
  /// 稳态下 CPU 从不停顿;代价是效果滞后一帧。</summary>
  public SharedSurface DispatchSurface<TShader>(ReadWriteTexture2D<float4> source, ShaderStage<TShader> stage)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>
  {
    if (_outputs.Length == 0)
    {
      throw new InvalidOperationException("先调用 CreateOutputs 创建输出表面");
    }

    if (_hasPending)
    {
      _sync.Wait(_pendingValue);   // 与 BeginFrame 幂等;重复等待的代价≈0(栅栏已到)
    }

    SharedSurface surface = _outputs[_outputIndex];
    _outputIndex = (_outputIndex + 1) % _outputs.Length;

    _pendingValue = _sync.ReadNextValue(_csDevice);
    _csDevice.For(surface.Width, surface.Height, stage(source, surface.Compute));
    _hasPending = true;

    return _outputs[(_outputIndex + 2) % _outputs.Length];   // 上一帧的输出,已完成
  }

  /// <summary>把 source 经一段计算着色器写进轮转的输出表面,阻塞到完成后
  /// 返回其 MonoGame 视图(可直接被 SpriteBatch 绘制)。</summary>
  public Texture2D Process<TShader>(ReadWriteTexture2D<float4> source, ShaderStage<TShader> stage)
      where TShader : struct, IComputeShader, IComputeShaderDescriptor<TShader>
  {
    return ProcessSurface(source, stage).MonoGame;
  }

  public void Dispose()
  {
    CsTextures.Clear();   // 缓存的 ComputeSharp 纹理都建在本桥设备上,先清缓存

    foreach (SharedSurface surface in _outputs)
    {
      surface.Dispose();
    }

    _outputs = [];
    _sync?.Dispose();
    _csDevice?.Dispose();
  }
}
