using System;
using System.Reflection;
using ComputeSharp;
using CsGraphicsDevice = ComputeSharp.GraphicsDevice;

namespace ComputeSharp.MonoGame;

/// <summary>
/// ComputeSharp 3.2.0 内部布局的反射读写。字段名经 Tinters 桥(同版本)运行时验证;
/// 升级 ComputeSharp 需重新核对 —— 任何字段缺失都会抛异常并被启动探针捕获,整体降级。
/// </summary>
internal static unsafe class CsInterop
{
  private const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

  public static FieldInfo FindField(object obj, string name)
  {
    for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
    {
      FieldInfo field = type.GetField(name, All);
      if (field != null)
      {
        return field;
      }
    }

    throw new MissingFieldException(obj.GetType().FullName, name);
  }

  /// <summary>读出 ComputeSharp ComPtr 字段里的裸 COM 指针(如 Texture2D.d3D12Resource)。</summary>
  public static void* ReadComPtr(object owner, string fieldName)
  {
    object boxed = FindField(owner, fieldName).GetValue(owner)
        ?? throw new InvalidOperationException($"{fieldName} is null");

    FieldInfo ptrField = FindField(boxed, "ptr_");
    return (void*)(IntPtr)Pointer.Unbox(ptrField.GetValue(boxed));
  }

  /// <summary>替换 ComputeSharp ComPtr 字段里的裸 COM 指针。</summary>
  public static void WriteComPtr(object owner, string fieldName, void* value)
  {
    FieldInfo field = FindField(owner, fieldName);
    object boxed = field.GetValue(owner)
        ?? throw new InvalidOperationException($"{fieldName} is null");

    FieldInfo ptrField = FindField(boxed, "ptr_");
    ptrField.SetValue(boxed, System.Reflection.Pointer.Box(value, ptrField.FieldType));
    field.SetValue(owner, boxed);
  }

  /// <summary>读出纹理描述符句柄(D3D12_CPU_DESCRIPTOR_HANDLE.ptr, UIntPtr)。</summary>
  public static ulong ReadDescriptorHandle(object csTexture, string handleFieldName)
  {
    object handles = FindField(csTexture, "d3D12ResourceDescriptorHandles").GetValue(csTexture)
        ?? throw new InvalidOperationException("no descriptor handles");

    object handle = FindField(handles, handleFieldName).GetValue(handles)
        ?? throw new InvalidOperationException($"{handleFieldName} is null");

    object value = FindField(handle, "ptr").GetValue(handle);
    return value is UIntPtr u ? (ulong)u : Convert.ToUInt64(value);
  }

  /// <summary>改写 ComputeSharp 纹理跟踪的 D3D12 资源状态。</summary>
  public static void SetResourceState(object csTexture, uint state)
  {
    FieldInfo field = FindField(csTexture, "d3D12ResourceState");
    field.SetValue(csTexture, Enum.ToObject(field.FieldType, state));
  }
}

/// <summary>
/// 等待 ComputeSharp 的内部派发完成栅栏:每次 For() 派发都会向它 Signal 下一个值,
/// CPU 等到该值即"本次派发已在 GPU 上完成"(Tinters 桥验证)。
/// </summary>
internal sealed unsafe class ComputeFenceSync : IDisposable
{
  // 热路径字段缓存:ReadNextValue 每帧调用,不做类型爬升
  private static readonly FieldInfo NextFenceValueField =
      typeof(CsGraphicsDevice).GetField("nextD3D12ComputeFenceValue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
      ?? throw new MissingFieldException("ComputeSharp.GraphicsDevice", "nextD3D12ComputeFenceValue");

  private readonly void* _fence;
  private readonly IntPtr _waitEvent;

  public ComputeFenceSync(CsGraphicsDevice device)
  {
    _fence = CsInterop.ReadComPtr(device, "d3D12ComputeFence");
    _waitEvent = D3D12Interop.CreateAutoResetEvent();
  }

  /// <summary>下一次派发的命令列表将 Signal 的栅栏值(派发前读取)。</summary>
  public ulong ReadNextValue(CsGraphicsDevice device)
  {
    return Convert.ToUInt64(NextFenceValueField.GetValue(device));
  }

  public void Wait(ulong value)
  {
    D3D12Interop.WaitForFenceValue(_fence, value, _waitEvent);
  }

  public void Dispose()
  {
    // 栅栏指针借自 ComputeSharp 的 ComPtr(未 AddRef),无需释放
  }
}
