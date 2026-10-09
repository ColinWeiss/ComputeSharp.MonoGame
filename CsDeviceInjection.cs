using System;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using ComputeSharp;
using CsGraphicsDevice = ComputeSharp.GraphicsDevice;
using GraphicsDevice = Microsoft.Xna.Framework.Graphics.GraphicsDevice;

namespace ComputeSharp.MonoGame;

/// <summary>
/// 把 ComputeSharp"注入"到 MonoGame 的 D3D12 设备上(同设备方案)。
///
/// ComputeSharp.GraphicsDevice 有一个 internal 构造函数直接接受外部
/// ID3D12Device* / IDXGIAdapter* / DXGI_ADAPTER_DESC1* / ID3D12MemoryAllocator*。
/// 这里实现它的分配器 COM 接口(仅 4 个方法):AllocateResource 直接转发到
/// ID3D12Device::CreateCommittedResource(27 号槽,已实测可用),在 MonoGame 的
/// 设备上完成分配。反射调用构造函数后,ComputeSharp 与 MonoGame 同设备 ——
/// 跨设备共享 / NT 句柄 / CreateSharedHandle 全部不再需要。
///
/// 背景:本机所有 D3D12 设备都落在 OS 自带的早期 Agility 核心
/// (SYSTEM32\D3D12Core.dll 10.0.22621.2361)上,其 CreateSharedHandle 存在
/// E_INVALIDARG 问题;同设备方案从根上绕开了它。
///
/// COM 对象布局:两格原生内存 [0]=vtbl [1]=状态指针;thunk 为静态函数指针。
/// 参数类型一律从构造函数反射获取(它们是 internal 的,源代码里不具名引用)。
/// </summary>
internal static unsafe class CsDeviceInjection
{
  private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

  private static readonly delegate* unmanaged<void*, Guid*, void**, int> QueryInterfacePtr = &QueryInterfaceThunk;
  private static readonly delegate* unmanaged<void*, Guid*, void**, int> AllocationQueryInterfacePtr = &AllocationQueryInterfaceThunk;
  private static readonly delegate* unmanaged<void*, uint> NoRefPtr = &NoRefThunk;
  private static readonly delegate* unmanaged<void*, uint> AddRefPtr = &AddRefThunk;
  private static readonly delegate* unmanaged<void*, uint> AllocationReleasePtr = &AllocationReleaseThunk;
  private static readonly delegate* unmanaged<void*, void*, uint, uint, int, void**, int> AllocateResourcePtr = &AllocateResourceThunk;
  private static readonly delegate* unmanaged<void*, void**, int> GetD3D12ResourcePtr = &GetD3D12ResourceThunk;

  // ID3D12MemoryAllocator: [QI, AddRef, Release, AllocateResource]
  private static readonly void* AllocatorVtbl = BuildTable(
      (void*)QueryInterfacePtr, (void*)NoRefPtr, (void*)NoRefPtr, (void*)AllocateResourcePtr);

  // ID3D12Allocation: [QI, AddRef, Release, GetD3D12Resource]
  private static readonly void* AllocationVtbl = BuildTable(
      (void*)AllocationQueryInterfacePtr, (void*)AddRefPtr, (void*)AllocationReleasePtr, (void*)GetD3D12ResourcePtr);

  /// <summary>注入出的实例持有的分配器对象,防 GC 回收(桥生命周期一份)。</summary>
  private static readonly System.Collections.Generic.List<object> KeepAlive = new();

  /// <summary>把 ComputeSharp 设备注入到 MonoGame 的 D3D12 设备上。</summary>
  public static CsGraphicsDevice Inject(Microsoft.Xna.Framework.Graphics.GraphicsDevice mgd, void* device, void* adapter)
  {
    // 1. 适配器描述(IDXGIAdapter1::GetDesc1,槽 10;失败则退回 GetDesc,槽 8)
    void* desc1 = NativeMemory.AllocZeroed(312);   // DXGI_ADAPTER_DESC1 尺寸
    void** vtbl = *(void***)adapter;
    var getDesc1 = (delegate* unmanaged[MemberFunction]<void*, void*, int>)vtbl[10];
    int hr = getDesc1(adapter, desc1);

    if (hr < 0)
    {
      var getDesc = (delegate* unmanaged[MemberFunction]<void*, void*, int>)vtbl[8];
      hr = getDesc(adapter, desc1);

      if (hr < 0)
      {
        NativeMemory.Free(desc1);
        throw new InvalidOperationException($"IDXGIAdapter::GetDesc1/GetDesc 失败: 0x{hr:X8}");
      }
    }

    // 2. 分配器 COM 对象(绑定 MonoGame 设备)
    var allocator = new AllocatorCom(device);

    // 3. 反射调用 internal 构造函数(4 个指针参数的那个)
    ConstructorInfo ctor = null;
    foreach (ConstructorInfo candidate in typeof(CsGraphicsDevice).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
    {
      ParameterInfo[] ps = candidate.GetParameters();
      if (ps.Length == 4 && ps.All(p => p.ParameterType.IsPointer))
      {
        ctor = candidate;
        break;
      }
    }

    if (ctor == null)
    {
      throw new InvalidOperationException("未找到 GraphicsDevice 内部注入构造函数");
    }

    ParameterInfo[] parameters = ctor.GetParameters();
    object[] args =
    {
            Pointer.Box(device, parameters[0].ParameterType),
            Pointer.Box(adapter, parameters[1].ParameterType),
            Pointer.Box(desc1, parameters[2].ParameterType),
            Pointer.Box(allocator.Instance, parameters[3].ParameterType),
        };

    var instance = (CsGraphicsDevice)ctor.Invoke(args);
    KeepAlive.Add(allocator);   // 分配器原生指针被实例持有,保活至进程结束
    return instance;
  }

  // ————— vtable 构建 —————

  private static void* BuildTable(void* a, void* b, void* c, void* d)
  {
    void** table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
    table[0] = a;
    table[1] = b;
    table[2] = c;
    table[3] = d;
    return table;
  }

  /// <summary>COM 对象 = 两格原生内存 [0]=vtbl [1]=状态。</summary>
  private static void** AllocObject(void* vtbl, void* state)
  {
    void** cells = (void**)NativeMemory.Alloc((nuint)(2 * sizeof(void*)));
    cells[0] = vtbl;
    cells[1] = state;
    return cells;
  }

  // ————— thunk —————

  /// <summary>分配器对象的 QI:生命周期由桥管理,不做引用计数。</summary>
  [UnmanagedCallersOnly]
  private static int QueryInterfaceThunk(void* self, Guid* iid, void** ppv)
  {
    if (*iid == IID_IUnknown)
    {
      *ppv = self;
      return 0;
    }

    *ppv = null;
    return unchecked((int)0x80004002);   // E_NOINTERFACE
  }

  /// <summary>分配对象的 QI:与 IUnknown 一样参与引用计数。</summary>
  [UnmanagedCallersOnly]
  private static int AllocationQueryInterfaceThunk(void* self, Guid* iid, void** ppv)
  {
    if (*iid == IID_IUnknown)
    {
      Interlocked.Increment(ref GetAllocationState(self)->RefCount);
      *ppv = self;
      return 0;
    }

    *ppv = null;
    return unchecked((int)0x80004002);   // E_NOINTERFACE
  }

  [UnmanagedCallersOnly]
  private static uint NoRefThunk(void* self) => 1;

  [UnmanagedCallersOnly]
  private static uint AddRefThunk(void* self)
  {
    return Interlocked.Increment(ref GetAllocationState(self)->RefCount);
  }

  [UnmanagedCallersOnly]
  private static uint AllocationReleaseThunk(void* self)
  {
    AllocationState* state = GetAllocationState(self);
    uint left = Interlocked.Decrement(ref state->RefCount);

    if (left == 0)
    {
      D3D12Interop.Release(state->Resource);
      NativeMemory.Free(state);
      NativeMemory.Free(self);
    }

    return left;
  }

  private static AllocationState* GetAllocationState(void* self) => (AllocationState*)((void**)self)[1];

  /// <summary>ID3D12Allocation 的状态块(原生,随最后一个引用释放)。</summary>
  private struct AllocationState
  {
    public void* Resource;
    public uint RefCount;
  }

  // ————— 分配器 thunk:转发到 CreateCommittedResource —————

  [UnmanagedCallersOnly]
  private static int AllocateResourceThunk(void* self, void* desc, uint heapType, uint state, int clear, void** allocation)
  {
    *allocation = null;

    void** stateBlock = (void**)((void**)self)[1];
    void* device = stateBlock[0];
    void* resource = D3D12Interop.CreateCommittedResource(device, desc, heapType, state);

    var allocState = (AllocationState*)NativeMemory.Alloc((nuint)sizeof(AllocationState));
    allocState->Resource = resource;      // CCR 的初始引用归分配对象所有
    allocState->RefCount = 1;

    *allocation = AllocObject(AllocationVtbl, allocState);
    return 0;
  }

  [UnmanagedCallersOnly]
  private static int GetD3D12ResourceThunk(void* self, void** resource)
  {
    AllocationState* state = GetAllocationState(self);
    D3D12Interop.AddRef(state->Resource);
    *resource = state->Resource;
    return 0;
  }

  /// <summary>分配器 COM 对象(生命周期与桥相同,由 GCHandle 保活)。</summary>
  private sealed class AllocatorCom
  {
    public readonly void* Instance;

    public AllocatorCom(void* device)
    {
      void** state = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
      state[0] = device;
      Instance = AllocObject(AllocatorVtbl, state);
    }
  }
}
