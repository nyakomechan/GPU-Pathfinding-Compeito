# SVO GPU Pathfinding — Unity C# + Compeito 実装プラン

## 概要

WebGL 2.0 GPGPU で実装済みの SVO 経路探索アルゴリズムを、Unity C# + Compeito（GPGPU Fragment Shader ジェネレータ）に移植する。

**アルゴリズム**: Wavefront Propagation (プル型並列BFS)
- 各ノードが自身の6近傍をチェックし、frontierノードがあれば自身を新規frontierに更新
- 状態遷移: `UNREACHED → FRONTIER → VISITED`
- ピンポンバッファで毎イテレーションの入出力を切り替え
- ゴール到達後の余分なイテレーションは許容

## アーキテクチャ

```
┌──────────────────────────────────────────────────────┐
│  C# Host (PathfindDemo / PathfindEngine)             │
│  - SVO構築 (CPU)                                     │
│  - Texture2D / RenderTexture へのデータ詰め          │
│  - Compeito.Dispatch によるピンポン制御              │
│  - AsyncGPUReadback で結果取得                        │
│  - 経路復元 (CPU側で親ポインタを逆追跡)              │
└───────────────┬──────────────────────────────────────┘
                │ Material.SetTexture / Compeito.Dispatch
┌───────────────▼──────────────────────────────────────┐
│  Compeito Shader (PathfindCompeito.compeito)         │
│   → 生成シェーダー "Compeito/Generated/PathfindCompeito"│
│                                                       │
│  ┌──────────────────┐   ピンポン   ┌───────────────┐ │
│  │ pathDataA        │ ◄──────────►│ pathDataB      │ │
│  │ (ARGBFloat RT)   │             │ (ARGBFloat RT) │ │
│  └──────────────────┘             └───────────────┘ │
│                                                       │
│  カーネル:                                            │
│  1. WavefrontExpand — 各ピクセル=リーフ1つ、プル型展開 │
│  2. GoalDetect      — ゴールノードのstate判定         │
│  3. Reset           — 全ノード初期化                  │
└───────────────────────────────────────────────────────┘
```

## WebGL版 / ComputeShader版からの主な変更点

| 項目 | WebGL版 | ComputeShader版 | Compeito版 |
|------|---------|-----------------|------------|
| GPU計算 | FBOピンポン + Fragment Shader | ComputeShader.Dispatch + ComputeBuffer | Compeito.Dispatch + RenderTexture |
| データ形式 | テクスチャにfloatエンコード | StructuredBuffer\<struct\>（直接構造体） | ARGBFloat RenderTexture / Texture2D |
| 隣接データ | テクスチャ | StructuredBuffer\<NeighborData\> | Texture2D<float4> (nodeCount × 2) |
| 移動コスト | 固定 | StructuredBuffer<float> | Texture2D<float> (nodeCount × 1) |
| 結果読み戻し | readPixels（同期ストール） | AsyncGPUReadback（非同期） | AsyncGPUReadback(RenderTexture)（非同期） |
| 可視化 | Three.js InstancedMesh | Graphics.DrawMeshInstanced | Graphics.DrawMeshInstanced |
| UI | HTML/CSSボタン・スライダー | IMGUI (OnGUI) | IMGUI (OnGUI) |
| 入力処理 | Canvas クリックイベント → Raycaster | Physics.Raycast / Plane交差 | Physics.Raycast / Plane交差 |

## ファイル構成

```
Assets/
├── Scripts/
│   ├── Pathfinding/            - 経路探索コアレイヤー
│   │   ├── PathResult.cs       - 経路探索結果コンテナ
│   │   ├── PathRequest.cs      - 経路探索リクエスト
│   │   ├── SVODataExtensions.cs- SVOData 座標変換ヘルパー
│   │   ├── GpuPathfinder.cs    - GPU BFS 専用エンジン
│   │   ├── PathProcessor.cs    - 経路復元・平滑化
│   │   ├── VoxelWorld.cs       - ボクセルグリッド / SVO 管理
│   │   └── PathfindingManager.cs - ゲーム向け高レベルAPI
│   ├── SVOBuilder.cs           - CPU: SVO構築 + 隣接ノード事前計算
│   ├── PathfindVisualizer.cs   - 描画: 壁/frontier/visited/pathの3D表示
│   └── PathfindDemo.cs         - MonoBehaviour: デモ用UI/入力
├── Shaders/
│   ├── PathfindCompeito.compeito  - Compeito GPGPU カーネル
│   └── Backup/
│       ├── PathfindCompute.compute  - 旧 compute shader (参考用バックアップ)
│       └── Resources/
│           └── PathfindCompute.compute
```

## Compeito 詳細 (`PathfindCompeito.compeito`)

### テクスチャデータ形式

**PathNode（ARGBFloat RenderTexture）**

| チャンネル | 内容 | 備考 |
|-----------|------|------|
| R | cost | 累積コスト (INF=999999) |
| G | state | 0=unreached, 1=frontier, 2=visited |
| B | parent | 親ノードインデックス (-1=none) |
| A | - | 未使用 (0) |

**Neighbors（RGBAFloat Texture2D, サイズ nodeCount × 2）**

| ピクセル | チャンネル | 内容 |
|---------|-----------|------|
| (idx, 0) | R | n0 (+X) |
| (idx, 0) | G | n1 (-X) |
| (idx, 0) | B | n2 (+Y) |
| (idx, 0) | A | n3 (-Y) |
| (idx, 1) | R | n4 (+Z) |
| (idx, 1) | G | n5 (-Z) |
| (idx, 1) | B, A | 未使用 |

**LeafCosts（RFloat Texture2D, サイズ nodeCount × 1）**

| ピクセル | 内容 |
|---------|------|
| (idx, 0) | リーフ idx の移動コスト |

### カーネル1: `WavefrontExpand`

```hlsl
#pragma kernel WavefrontExpand float4

Texture2D<float4> _PathDataIn;
Texture2D<float4> _Neighbors;
Texture2D<float> _LeafCosts;
int _NodeCount;

float4 WavefrontExpand(uint2 id) {
    int idx = (int)id.x;
    if (idx >= _NodeCount) return float4(INF, 0, -1, 0);

    float4 my = _PathDataIn.Load(int3(idx, 0, 0));
    int myState = (int)round(my.y);

    // visited → 変更なし
    if (myState == 2) return my;

    // frontier → visited に遷移
    if (myState == 1) return float4(my.x, 2, my.z, 0);

    // unreached → 6近傍のfrontierから最小costをpull
    float4 n0 = _Neighbors.Load(int3(idx, 0, 0));
    float4 n1 = _Neighbors.Load(int3(idx, 1, 0));
    int nbrs[6] = { (int)n0.x, (int)n0.y, (int)n0.z, (int)n0.w, (int)n1.x, (int)n1.y };

    float bestCost = INF;
    int bestParent = -1;
    for (int i = 0; i < 6; i++) {
        int nIdx = nbrs[i];
        if (nIdx < 0) continue;
        float4 nd = _PathDataIn.Load(int3(nIdx, 0, 0));
        if ((int)round(nd.y) != 1) continue;  // frontier以外skip
        float newCost = nd.x + _LeafCosts.Load(int3(idx, 0, 0));
        if (newCost < bestCost) {
            bestCost = newCost;
            bestParent = nIdx;
        }
    }

    if (bestCost < INF)
        return float4(bestCost, 1, (float)bestParent, 0);
    else
        return float4(INF, 0, -1, 0);
}
```

### カーネル2: `GoalDetect`

```hlsl
#pragma kernel GoalDetect float

Texture2D<float4> _PathDataIn;
int _GoalIndex;

float GoalDetect(uint2 id) {
    float4 goal = _PathDataIn.Load(int3(_GoalIndex, 0, 0));
    int goalState = (int)round(goal.y);
    return (goalState >= 2) ? 1.0 : 0.0;
}
```

### カーネル3: `Reset`

```hlsl
#pragma kernel Reset float4

int _NodeCount;
int _StartIndex;

float4 Reset(uint2 id) {
    int idx = (int)id.x;
    if (idx >= _NodeCount) return float4(INF, 0, -1, 0);

    if (idx == _StartIndex)
        return float4(0.0, 1, -1, 0);   // start: cost=0, frontier
    else
        return float4(INF, 0, -1, 0);   // その他: unreached
}
```

## C# クラス詳細

### SVOBuilder.cs

変更なし。WebGL版 `svo-builder.js` と同一ロジック:

- `int gridSize` → コンストラクタ引数 (16)
- `byte[] grid` → gridSize³ のボクセル占有配列
- `SetVoxel(x,y,z,occupied)` → grid書き込み
- `Build()` → 再帰的オクツリー構築、以下を返す:

```
SVOData {
    Node[] nodes;           // 全SVOノード
    LeafNode[] leafNodes;   // 空ボクセルのリーフノードのみ
    int[] neighbors;        // leafCount×6 の隣接インデックス
    int[] voxelToLeaf;      // gridSize³ の リーフマップ (-1=壁)
    float[] leafCosts;      // リーフごとの移動コスト
    int gridSize;
    int leafCount;
}
```

### Pathfinding/PathResult.cs

経路探索の結果を表すゲーム向けコンテナ。

```csharp
public class PathResult
{
    public bool Success;
    public string ErrorMessage;
    public List<Vector3> Waypoints;
    public List<int> LeafIndices;
    public PathNode[] PathData;
    public float TotalCost;
    public int IterationCount;
}
```

### Pathfinding/PathRequest.cs

経路探索リクエスト。

```csharp
public class PathRequest
{
    public Vector3 StartWorld;
    public Vector3 GoalWorld;
    public Action<PathResult> OnComplete;
}
```

### Pathfinding/SVODataExtensions.cs

`SVOData` の拡張メソッド群。

- `WorldToLeaf(Vector3)` — ワールド座標 → リーフインデックス
- `LeafToWorld(int, bool center)` — リーフインデックス → ワールド座標
- `FindNearestEmptyLeaf(Vector3)` — 最寄りの通行可能リーフ
- `IsValidLeaf(int)` — インデックス範囲チェック

### Pathfinding/GpuPathfinder.cs

**責務**: GPU 上の Wavefront BFS のみ。

**主要 API**:

```csharp
public void Init(Material program, SVOData data);
public void FindPath(int startIdx, int goalIdx, int maxIterations, Action<PathNode[]> onComplete);
public void Tick();
public void Cancel();
public void Dispose();
```

`FindPath` を呼ぶと非同期探索が開始され、`Tick()` を毎フレーム呼ぶことで進行する。完了するとコールバックが呼ばれる。

### Pathfinding/PathProcessor.cs

**責務**: `PathNode[]` → `PathResult` への変換。

- `ReconstructPath` — 親ポインタ逆追跡
- `ComputeWaypoints` — リーフ列を 3D 座標列に変換
- `SmoothPath` — Physics.SphereCast による直線近似
- `Process` — 上記を統合して `PathResult` を生成

### Pathfinding/VoxelWorld.cs

**責務**: シーンの `BoxCollider` からボクセルグリッドを構築・管理。

```csharp
public void Rebuild();
public void Rebuild(int gridSize, float heightFactor, int wallLayer);
public int WorldToLeaf(Vector3 worldPos);
public Vector3 LeafToWorld(int leafIdx, bool center = true);
public int FindNearestEmptyLeaf(Vector3 worldPos);
```

動的な壁変更に対応するため `Rebuild()` を呼び出し可能。

### Pathfinding/PathfindingManager.cs

**責務**: ゲーム向け高レベル API を提供する MonoBehaviour。

```csharp
public void RequestPath(PathRequest request);
public void RequestPath(Vector3 start, Vector3 goal, Action<PathResult> callback);
public void RebuildWorld();
```

内部で `VoxelWorld`, `GpuPathfinder`, `PathProcessor` を連携させ、リクエストをキューイングして順次処理する。

使用例:

```csharp
PathfindingManager.Instance.RequestPath(
    transform.position,
    target.position,
    result => {
        if (result.Success) agent.SetPath(result.Waypoints);
    }
);
```

### PathfindVisualizer.cs

描画要素:

| 要素 | 手法 | 色 |
|------|------|-----|
| 壁ボクセル | InstancedMesh (Box) | 半透明グレー (0x4A4A6A, α=0.5) |
| Frontier | InstancedMesh (Box, 小) | 緑 (0x40916C) |
| Visited | InstancedMesh (Box, 小) | 暗緑 (0x2D6A4F, α=0.5) |
| 経路ライン | LineRenderer | 黄色 (0xFFD166) |
| Start マーカー | Sphere Mesh | 明緑 (0x06D6A0) |
| Goal マーカー | Sphere Mesh | 赤 (0xEF476F) |
| Zスライス平面 | 透明Quad | 黄色 (α=0.08) |

`PathResult` 版の `UpdateVisual(PathResult, int startIdx, int goalIdx)` を追加。

### PathfindDemo.cs

**主な変更点**:
- `PathfindEngine` 直接操作 → `PathfindingManager` 経由
- `PathfindMaterial` 管理 → `PathfindingManager` に委譲
- SVO / グリッド構築 → `VoxelWorld` に委譲
- UI はカメラ操作、スタート/ゴール配置、Run/Reset ボタンのみ

**初期化フロー** (`Start`):
1. `PathfindingManager` を確保（存在しなければ生成）
2. `PathfindVisualizer` 初期化
3. デフォルトスタート/ゴール設定

## 一連の処理フロー

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 0: 初期化
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  [CPU] VoxelWorld.Rebuild()
         │  ・シーンの BoxCollider からグリッド生成
         │  ・SVOBuilder.Build() で SVO 構築
         ▼
  [CPU→GPU] GpuPathfinder.Init()
         │  ・neighbors → neighborTex (Texture2D)
         │  ・leafCosts → costTex (Texture2D)
         │  ・pathDataA/B, goalResultTex を作成
         ▼
  [CPU] PathProcessor 作成
         │  ・SVOData と wallLayerMask を保持


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 1: 経路探索リクエスト
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  PathfindingManager.RequestPath(startWorld, goalWorld, callback)
         │
         ▼
  [CPU] VoxelWorld.WorldToLeaf / FindNearestEmptyLeaf
         │
         ▼
  [CPU] GpuPathfinder.FindPath(startIdx, goalIdx, maxIterations, onGpuComplete)
         │  ・Reset カーネルで初期化
         │  ・state = Running


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 2: 毎フレームの探索進行
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  PathfindingManager.Update():
    GpuPathfinder.Tick()
      │
      ├─ Running なら WavefrontExpand をバッチ実行
      │      → GoalDetect + AsyncGPUReadback
      │
      ├─ GoalReadbackPending なら完了チェック
      │      ├─ ゴール到達 → PathData 読み戻し
      │      ├─ 未到達 & 残イテレーションあり → 次バッチ
      │      └─ 未到達 & 最大イテレーション → 失敗
      │
      └─ PathReadbackPending なら完了チェック
             → PathNode[] をコールバックで返す


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 3: 経路復元・コールバック
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  PathfindingManager.OnGpuPathComplete(PathNode[])
         │
         ▼
  [CPU] PathProcessor.Process(PathNode[], startIdx, goalIdx)
         │  ・ReconstructPath
         │  ・ComputeWaypoints
         │  ・SmoothPath
         ▼
  [CPU] request.OnComplete(PathResult)
         │
         ▼
  [CPU] PathfindVisualizer.UpdateVisual(PathResult)


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  動的グリッド再構築
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  PathfindingManager.RebuildWorld()
         │
         ▼
  [CPU] VoxelWorld.Rebuild()
         │  ・シーンコライダーを再スキャン
         │  ・SVO を再構築
         ▼
  [CPU] GpuPathfinder を再初期化
         │  ・RenderTexture サイズ変更
         │  ・neighbors / leafCosts テクスチャを作り直し
```

## 非同期読み戻しの設計

ComputeShader 版と同様に `AsyncGPUReadback` を使用するが、対象が RenderTexture となる。

```csharp
// ゴール判定 (1×1 RFloat RT)
goalReadbackRequest = AsyncGPUReadback.Request(goalResultTex, 0, TextureFormat.RFloat);
public int GetGoalReached() {
    return goalReadbackRequest.GetData<float>()[0] > 0.5f ? 1 : 0;
}

// 経路復元用 (nodeCount × 1 ARGBFloat RT)
pathReadbackRequest = AsyncGPUReadback.Request(currentPathDataRT, 0, TextureFormat.RGBAFloat);
public PathNode[] GetPathData() {
    var colors = pathReadbackRequest.GetData<Color>();
    var result = new PathNode[nodeCount];
    for (int i = 0; i < nodeCount; i++) {
        result[i].cost = colors[i].r;
        result[i].state = (int)colors[i].g;
        result[i].parent = (int)colors[i].b;
    }
    return result;
}
```

## パフォーマンス考慮事項

1. **readPixelsストール回避**: WebGL版では毎フレーム同期ストールが発生していたが、Unity版では `AsyncGPUReadback` で完全に非同期化
2. **1フレーム遅延**: 読み戻し結果は次フレームで反映されるため、表示が1フレーム遅れるがリアルタイム用途では問題なし
3. **Dispatch回数**: 1フレームあたり 8×WavefrontExpand + 1×GoalDetect = 9 回の `Compeito.Dispatch`（各々は `Graphics.Blit` 相当）
4. **テクスチャサイズ**: 16³ ≒ 4096 リーフ × ARGBFloat = 64KB/RT → GPU VRAM的には問題なし
5. **InstancedMesh更新**: 毎フレーム frontier/visited のインスタンス行列を更新 → `SetData` で動的更新

## 設定パラメータ

| パラメータ | 値 | 備考 |
|-----------|-----|------|
| GRID_SIZE | 16 | 16×16×16 ボクセルグリッド |
| ITERS_PER_FRAME | 8 | 1フレームあたりのWavefront展開回数 |
| INF | 999999.0 | 到達不能コストの初期値 |
