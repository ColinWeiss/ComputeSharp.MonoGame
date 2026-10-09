using System;
using System.Collections.Generic;
using ComputeSharp;
using Microsoft.Xna.Framework.Graphics;

namespace ComputeSharp.MonoGame;

/// <summary>
/// 静态纹理缓存:MonoGame Texture2D → ComputeSharp ReadWriteTexture2D&lt;float4&gt;。
/// 首次使用时经桥上传一次并按实例(引用)为键缓存,之后 Dispatch/Process 直接查字典 ——
/// 调用方可以把 DispatchSurface/Process 的第一个参数直接传 MonoGame 的 Texture2D。
///
/// 约定:按引用缓存,适合静态内容;内容会变化的纹理调用 Invalidate 丢弃重传。
/// 单桥假设(整个进程一个 CsBridge);桥销毁时缓存整体清空。游戏主循环单线程,
/// 不加锁。
/// </summary>
public static class CsTextures
{
  private static readonly Dictionary<Texture2D, ReadWriteTexture2D<float4>> Cache = new();
  private static CsBridge _owner;

  internal static void Bind(CsBridge bridge) => _owner = bridge;

  /// <summary>取(或懒上传)指定纹理对应的 ComputeSharp 纹理。</summary>
  public static ReadWriteTexture2D<float4> Get(Texture2D source)
  {
    if (Cache.TryGetValue(source, out ReadWriteTexture2D<float4> cached))
    {
      return cached;
    }

    ReadWriteTexture2D<float4> created = (_owner ?? throw new InvalidOperationException("尚未创建 CsBridge"))
        .UploadTexture(source);
    Cache[source] = created;
    return created;
  }

  /// <summary>丢弃指定纹理的缓存(下次使用时自动重新上传)。</summary>
  public static void Invalidate(Texture2D source)
  {
    if (Cache.Remove(source, out ReadWriteTexture2D<float4> cached))
    {
      cached.Dispose();
    }
  }

  /// <summary>清空全部缓存(内容批量重载或桥销毁时)。</summary>
  public static void Clear()
  {
    foreach (ReadWriteTexture2D<float4> texture in Cache.Values)
    {
      texture.Dispose();
    }

    Cache.Clear();
  }
}
