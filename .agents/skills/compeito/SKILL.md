---
name: compeito
description: Use when editing .compeito files, writing Compeito GPGPU kernels, creating C# drivers for Compeito dispatch, or working with VRChat GPGPU shader generation. Triggers on .compeito files, Compeito.Dispatch, Compeito.CreateRT, Compeito.Copy, and CompeitoImporter references.
---

# Compeito Skill

Compeito is a GPGPU utility for VRChat that auto-generates shaders and materials from `.compeito` files, enabling Compute Shader-style programming using full-screen quad fragment shader passes.

## .compeito File Syntax

A `.compeito` file has two sections: **pragma declarations** (must come first) and **HLSL body**.

### Pragma Section

All `#pragma kernel` directives must appear at the top of the file, before any code. Blank lines and comments (`//`, `/* */`) are allowed between pragmas.

```
#pragma kernel <NAME> [<RETURN_TYPE>]
```

- `<NAME>`: Kernel function name (C-style identifier)
- `<RETURN_TYPE>`: Optional. Defaults to `float4`. Common types: `float`, `float2`, `float4`
- Multiple kernels are allowed; each becomes a separate shader pass

Examples:
```hlsl
#pragma kernel Main
#pragma kernel AddForce float2
#pragma kernel Pressure float
```

### Body Section

After all pragmas, write HLSL code verbatim. Each declared kernel must have a matching function:

```hlsl
<RETURN_TYPE> <KERNEL_NAME>(uint2 id) { ... }
```

- `id`: Texel coordinate (pixels) of the output texture
- Return value: written to the output texture at that texel

### Complete Minimal Example

```hlsl
#pragma kernel Main

float4 Main(uint2 id) {
  float2 uv = (id + 0.5) / 256;
  return float4(uv, 0, 1);
}
```

## Texture and Uniform Patterns

### Declaring Textures (in .compeito body)

```hlsl
Texture2D<float4> _ColorTex;
Texture2D<float2> _Velocity;
Texture2D<float> _Pressure;
```

### Reading Textures

```hlsl
// Direct texel read at integer coordinates
float4 c = _ColorTex.Load(int3(id, 0));

// Bilinear sampling at UV coordinates
float4 c = _ColorTex.SampleLevel(sampler_linear_clamp, uv, 0);
```

### Sampler States

```hlsl
SamplerState sampler_linear_clamp;
```

### Uniform Variables

```hlsl
float _Init;
float _DeltaTime;
```

Set from C# via `material.SetFloat("_Init", 1)`, `material.SetTexture("_Velocity", rt)`, etc.

### Built-in Unity Variables

`UnityCG.cginc` is auto-included, providing `_Time`, `_SinTime`, etc.

## Compeito C# API

Namespace: `imaginantia.Compeito`

### Compeito.CreateRT

```csharp
public static RenderTexture CreateRT(string name, int width, int height,
    RenderTextureFormat format = RenderTextureFormat.ARGBFloat,
    bool autoGenerateMips = false)
```

Creates a RenderTexture with `FilterMode.Point`. Common formats:
- `RenderTextureFormat.ARGBFloat` - 4x float32 (default)
- `RenderTextureFormat.RGFloat` - 2x float32 (velocity)
- `RenderTextureFormat.RFloat` - 1x float32 (pressure, divergence)

### Compeito.Dispatch

```csharp
public static void Dispatch(Material program, int kernel, RenderTexture dest)
```

- `kernel`: Pass index from `material.FindPass("KernelName")`
- In Udon: uses `VRCGraphics.Blit`
- In standard Unity: uses `Graphics.Blit`

### Compeito.Copy

```csharp
public static void Copy(RenderTexture src, RenderTexture dest)
```

Blits source to destination for double-buffering.

## C# Driver Pattern

### Minimal Driver (UdonSharp)

```csharp
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using imaginantia.Compeito;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ComputeTest : UdonSharpBehaviour
{
    public Material compute;
    public Material display;
    private RenderTexture output;
    int kernel;

    void Start() {
        kernel = compute.FindPass("Main");
        output = Compeito.CreateRT("Output", 256, 256);
        display.SetTexture("_MainTex", output);
    }

    void Update() {
        Compeito.Dispatch(compute, kernel, output);
    }
}
```

### Multi-Kernel Driver Pattern

```csharp
void Start() {
    addForce = compute.FindPass("AddForce");
    advect = compute.FindPass("Advect");
    pressureInit = compute.FindPass("PressureInit");

    velocity0 = Compeito.CreateRT("Vel0", W, W, RenderTextureFormat.RGFloat);
    velocity1 = Compeito.CreateRT("Vel1", W, W, RenderTextureFormat.RGFloat);
    pressure0 = Compeito.CreateRT("Prs0", W, W, RenderTextureFormat.RFloat);
}

void Update() {
    compute.SetFloat("_Init", initialized ? 0 : 1);
    compute.SetTexture("_Velocity", velocity0);
    Compeito.Dispatch(compute, addForce, velocity1);

    compute.SetTexture("_Velocity", velocity1);
    Compeito.Dispatch(compute, advect, velocity0);

    // Jacobi iteration pattern
    for (int i = 0; i < iterations; i++) {
        compute.SetTexture("_Pressure", pressure0);
        Compeito.Dispatch(compute, projectStep, pressure1);
        compute.SetTexture("_Pressure", pressure1);
        Compeito.Dispatch(compute, projectStep, pressure0);
    }
}
```

## Initialization Pattern

RenderTextureの初期値は未定義のため、初回フレームで明示的に初期化する必要があります。`_Init`ユニフォーム変数を使ったパターンが標準的です。

### .compeito側

```hlsl
float _Init;

float2 AddForce(uint2 id) {
  if (_Init > 0.5) return float2(0, 0);
  // ... 通常の処理
}

float PressureInit(uint2 id) {
  if (_Init > 0.5) return 0;
  // ... 通常の処理
}
```

### C#側

```csharp
private bool initialized = false;

void Update() {
  compute.SetFloat("_Init", initialized ? 0 : 1);
  // ... Dispatch呼び出し
  initialized = true;
}
```

初回フレームでは`_Init`が1 (> 0.5) となり、各カーネルでゼロ初期値が返されます。2フレーム目以降は`_Init`が0となり通常の計算処理に移行します。

## Generated Shader Structure

Each `.compeito` file generates a shader named `Compeito/Generated/<FileName>` containing one `Pass` per kernel. The pass name matches the kernel name for `FindPass` lookup.

Generated pass structure:
- Vertex shader: full-screen quad (UV 0-1 mapped to clip space -1 to 1)
- Fragment shader: casts pixel position to `uint2 id`, calls user kernel, outputs result
- `Cull Off` is set (both faces rendered)
- `#define CompeitoPass_<KERNEL_NAME>` is emitted per pass for conditional compilation
- `#line` directives map errors back to `.compeito` source

## Key Constraints and Tips

1. **Pragma order**: All `#pragma kernel` must be at the top of the file, before any code
2. **Return type**: If not specified in pragma, defaults to `float4`
3. **Function signature**: Must be `<TYPE> <NAME>(uint2 id)` — the parameter must be `uint2`
4. **Double-buffering**: Use two RenderTextures and swap inputs/outputs between dispatches
5. **Boundary handling**: Implement boundary conditions explicitly in kernel code (no automatic clamping)
6. **Include files**: `#include` works for shared HLSL headers (e.g. `#include "Fluid.hlsl"`)
7. **VSCode syntax highlighting**: Add `{"files.associations": {"*.compeito": "hlsl"}}` to `.vscode/settings.json`
8. **New file creation**: In Unity, `Assets > Create > Compeito` creates a new `.compeito` file with a template
9. **VPM install**: `https://phi16.github.io/VRC_Packages/`
10. **PC-only**: Only tested on PC platform

## Compeito Udon Assembly

The Compeito runtime uses assembly definitions:
- Editor: `Compeito.Udon.asmdef` (Editor/)
- Runtime: `Compeito.Udon.asmdef` (Udon/)

Reference `imaginantia.Compeito` namespace in scripts that use the API.
