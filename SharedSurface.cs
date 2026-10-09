using System;
using ComputeSharp;
using ComputeSharp.Interop;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using CsGraphicsDevice = ComputeSharp.GraphicsDevice;
using GraphicsDevice = Microsoft.Xna.Framework.Graphics.GraphicsDevice;

namespace ComputeSharp.MonoGame;

/// <summary>
/// 一块同一 D3D12 设备上"计算可写、渲染可读"的 GPU 纹理,零拷贝。
///
/// ComputeSharp 已被注入到 MonoGame 的设备上(见 CsDeviceInjection),所以这里
/// 的纹理本就分配在 MonoGame 的设备中:
///   • ComputeSharp 侧:AllocateReadWriteTexture2D 正常创建(UAV 可读写);
///   • MonoGame 侧:InteropServices.GetID3D12Resource(公开 API)取出资源指针,
///     RenderTarget2D.FromNativeHandle 官方正门包装(非拥有)。
/// 没有任何反射手术、没有共享句柄、没有跨设备 —— Tinters 桥的全部手术在这里消失。
/// </summary>
public sealed unsafe class SharedSurface : IDisposable
{
  /// <summary>MonoGame 视图:可被 SpriteBatch 采样/绘制(非拥有)。</summary>
  public RenderTarget2D MonoGame { get; }

  /// <summary>ComputeSharp 视图:可被着色器读写。</summary>
  public ReadWriteTexture2D<float4> Compute { get; }

  public int Width { get; }
  public int Height { get; }

  private SharedSurface(RenderTarget2D monoGame, ReadWriteTexture2D<float4> compute, int width, int height)
  {
    MonoGame = monoGame;
    Compute = compute;
    Width = width;
    Height = height;
  }

  public static SharedSurface Create(Microsoft.Xna.Framework.Graphics.GraphicsDevice mgd, CsGraphicsDevice csDevice, int width, int height)
  {
    ReadWriteTexture2D<float4> csTexture = csDevice.AllocateReadWriteTexture2D<float4>(width, height);

    Guid iid = D3D12Interop.IID_ID3D12Resource;
    void* resource = null;
    InteropServices.GetID3D12Resource(csTexture, &iid, &resource);   // AddRef

    try
    {
      var wrapper = RenderTarget2D.FromNativeHandle(
          mgd, (IntPtr)resource, width, height,
          SurfaceFormat.Vector4, DepthFormat.None, 0, externalPresentation: false);

      return new SharedSurface(wrapper, csTexture, width, height);
    }
    finally
    {
      D3D12Interop.Release(resource);   // MonoGame 侧非拥有;资源由 ComputeSharp 纹理持有
    }
  }

  public void Dispose()
  {
    Compute.Dispose();    // 释放资源引用(分配对象随之销毁)
    MonoGame.Dispose();   // 非拥有包装,仅解绑
  }
}
