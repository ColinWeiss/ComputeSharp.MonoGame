using ComputeSharp;

namespace ComputeShar.Graphics;

/// <summary>
/// 彩虹 shader:按对角线方向 + 时间扫过色相的余弦调色板彩虹,
/// 以鱼头亮度调制浓淡、保留轮廓 alpha(输出为预乘 alpha,配 SpriteBatch 默认混合)。
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RainbowShader(
    ReadWriteTexture2D<float4> source,
    ReadWriteTexture2D<float4> target,
    float time) : IComputeShader
{
  public void Execute()
  {
    float4 c = source[ThreadIds.XY];

    float2 uv = new float2(ThreadIds.X, ThreadIds.Y) / new float2(DispatchSize.X, DispatchSize.Y);
    float hue = Hlsl.Frac(uv.X * 1.5f - uv.Y * 0.5f + time * 0.25f);

    // IQ 余弦调色板彩虹:hue 在 [0,1) 平移相位,三通道错开 120°
    float3 rainbow = 0.5f + 0.5f * Hlsl.Cos(6.2831853f * (new float3(0.0f, 0.33f, 0.67f) + hue));

    float luma = Hlsl.Dot(c.RGB, new float3(0.2126f, 0.7152f, 0.0722f));
    float shade = 0.35f + 0.65f * luma;

    target[ThreadIds.XY] = new float4(rainbow * shade * c.W, c.W);
  }
}