using System;
using System.Runtime.InteropServices;

namespace ComputeSharp.MonoGame;

/// <summary>
/// 裸 ID3D12 虚表互操作(不引入任何 D3D12 绑定库)。
/// 槽位 = d3d12.idl 中 ID3D12Device 方法顺序 + 6(IUnknown 0..2, ID3D12Object 3..6),
/// 并与 ComputeSharp 3.2.0 自身 IL 中硬编码的槽位交叉核对一致:
/// CreateCommittedResource=27, GetDeviceRemovedReason=37, CreateFence=36。
///
/// 注意:37 号之后的槽位不要使用 —— 实测本机运行时(所有设备均落在 OS 自带的
/// SYSTEM32\D3D12Core.dll 10.0.22621.2361 上)在该区域与头文件不一致,
/// GetAdapterLuid(43 号)调用即 AV。
/// </summary>
internal static unsafe class D3D12Interop
{
  // {696442BE-A72E-4059-BC79-5B5C98040FAD}
  public static readonly Guid IID_ID3D12Resource = new("696442be-a72e-4059-bc79-5b5c98040fad");

  private const int CreateCommittedResourceSlot = 21 + 6;
  private const int GetDeviceRemovedReasonSlot = 31 + 6;
  private const int ReleaseSlot = 2;
  private const int AddRefSlot = 1;
  private const int FenceSetEventOnCompletionSlot = 9;

  private const uint HeapTypeDefault = 1;

  [StructLayout(LayoutKind.Sequential)]
  private struct HeapProperties
  {
    public uint Type;
    public uint CpuPageProperty;
    public uint MemoryPoolPreference;
    public uint CreationNodeMask;
    public uint VisibleNodeMask;
  }

  // 逐字段对齐后的 D3D12_RESOURCE_DESC(原生 56 字节含尾填充,本布局一致)
  [StructLayout(LayoutKind.Sequential)]
  private struct ResourceDesc
  {
    public uint Dimension;
    public ulong Alignment;
    public ulong Width;
    public uint Height;
    public ushort DepthOrArraySize;
    public ushort MipLevels;
    public ushort Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Layout;
    public uint Flags;
  }

  /// <summary>通用 CreateCommittedResource:desc 由调用方给出
  /// (ComputeSharp 分配器转发,或其它原生建资源场景)。</summary>
  public static void* CreateCommittedResource(void* device, void* desc, uint heapType, uint initialState)
  {
    var heap = new HeapProperties { Type = heapType };

    void** vtbl = *(void***)device;
    var create = (delegate* unmanaged[MemberFunction]<void*, HeapProperties*, uint, ResourceDesc*, uint, void*, Guid*, void**, int>)
        vtbl[CreateCommittedResourceSlot];

    Guid iid = IID_ID3D12Resource;
    void* resource = null;
    Marshal.ThrowExceptionForHR(create(device, &heap, 0, (ResourceDesc*)desc, initialState, null, &iid, &resource));
    return resource;
  }

  /// <summary>ID3D12Device::GetDeviceRemovedReason(零参):兼作虚表机器的试金石。</summary>
  public static int GetDeviceRemovedReason(void* device)
  {
    void** vtbl = *(void***)device;
    var get = (delegate* unmanaged[MemberFunction]<void*, int>)vtbl[GetDeviceRemovedReasonSlot];
    return get(device);
  }

  /// <summary>IUnknown::Release。</summary>
  public static uint Release(void* comObject)
  {
    void** vtbl = *(void***)comObject;
    var release = (delegate* unmanaged[MemberFunction]<void*, uint>)vtbl[ReleaseSlot];
    return release(comObject);
  }

  /// <summary>IUnknown::AddRef。</summary>
  public static uint AddRef(void* comObject)
  {
    void** vtbl = *(void***)comObject;
    var addRef = (delegate* unmanaged[MemberFunction]<void*, uint>)vtbl[AddRefSlot];
    return addRef(comObject);
  }

  /// <summary>ID3D12Fence::SetEventOnCompletion + Win32 事件:CPU 侧有界等待栅栏值。</summary>
  public static void WaitForFenceValue(void* fence, ulong value, IntPtr waitEvent)
  {
    void** vtbl = *(void***)fence;
    var set = (delegate* unmanaged[MemberFunction]<void*, ulong, IntPtr, int>)vtbl[FenceSetEventOnCompletionSlot];

    if (set(fence, value, waitEvent) != 0)
    {
      return; // 无法挂起等待;放弃阻塞继续走(宁要丢帧不要挂死)
    }

    for (int i = 0; i < 50; i++)   // 50 × 100ms 上限
    {
      if (WaitForSingleObject(waitEvent, 100) == 0)
      {
        return;
      }
    }
  }

  [DllImport("kernel32", SetLastError = true)]
  private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, char* name);

  [DllImport("kernel32", SetLastError = true)]
  private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

  public static IntPtr CreateAutoResetEvent() => CreateEventW(IntPtr.Zero, false, false, null);
}
