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
│   ├── SVOBuilder.cs           - CPU: SVO構築 + 隣接ノード事前計算
│   ├── PathfindEngine.cs       - GPU: Texture/RT管理, Compeito.Dispatch, ピンポン
│   ├── PathfindVisualizer.cs   - 描画: 壁/frontier/visited/pathの3D表示
│   └── PathfindDemo.cs         - MonoBehaviour: 初期化, UI, 入力, アニメーションループ
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

### PathfindEngine.cs

**フィールド**:
- `Material program` — Compeito から生成されたマテリアル
- `int kernelWavefront, kernelGoalDetect, kernelReset` — `material.FindPass("...")`
- `Texture2D neighborTex` — 隣接データ (nodeCount × 2, RGBAFloat)
- `Texture2D costTex` — 移動コスト (nodeCount × 1, RFloat)
- `RenderTexture pathDataA`, `pathDataB` — ピンポン用 ARGBFloat RT
- `RenderTexture goalResultTex` — ゴール到達フラグ (1×1, RFloat)
- `int nodeCount`, `int startIdx`, `int goalIdx`
- `bool currentReadA` — ピンポン状態

**メソッド**:
- `Init(Material program, SVOData data, int wallLayerMask)`: テクスチャ作成・データアップロード
- `SetStart(int idx)`, `SetGoal(int idx)`: インデックス設定
- `Reset()`: Reset カーネルを両方の RT にディスパッチ
- `Iterate(int count)`: WavefrontExpand × count + GoalDetect をディスパッチ、ピンポン切り替え
- `RequestReadback()`: `AsyncGPUReadback.Request(goalResultTex)` 呼び出し
- `IsReadbackReady()`: 読み戻し完了判定
- `GetGoalReached()`: goalResultTex[0] を取得
- `GetPathData()`: pathDataIn 全体を取得 (Color[] → PathNode[] 変換)
- `ReconstructPath(PathNode[] data)`: ゴール→スタートの親ポインタ逆追跡

**ピンポン制御**:

```csharp
DispatchWavefront(count):
  for i in 0..count-1:
    RenderTexture readBuf  = currentReadA ? pathDataA : pathDataB;
    RenderTexture writeBuf = currentReadA ? pathDataB : pathDataA;

    program.SetTexture("_PathDataIn", readBuf);
    Compeito.Dispatch(program, kernelWavefront, writeBuf);

    currentReadA = !currentReadA;
    iteration++;

  // GoalDetect
  RenderTexture currentBuf = currentReadA ? pathDataA : pathDataB;
  program.SetTexture("_PathDataIn", currentBuf);
  Compeito.Dispatch(program, kernelGoalDetect, goalResultTex);
  goalReadbackRequest = AsyncGPUReadback.Request(goalResultTex, 0, TextureFormat.RFloat);
```

### PathfindVisualizer.cs

変更なし。描画要素:

| 要素 | 手法 | 色 |
|------|------|-----|
| 壁ボクセル | InstancedMesh (Box) | 半透明グレー (0x4A4A6A, α=0.5) |
| Frontier | InstancedMesh (Box, 小) | 緑 (0x40916C) |
| Visited | InstancedMesh (Box, 小) | 暗緑 (0x2D6A4F, α=0.5) |
| 経路ライン | LineRenderer | 黄色 (0xFFD166) |
| Start マーカー | Sphere Mesh | 明緑 (0x06D6A0) |
| Goal マーカー | Sphere Mesh | 赤 (0xEF476F) |
| Zスライス平面 | 透明Quad | 黄色 (α=0.08) |

### PathfindDemo.cs

**定数**:
- `GRID_SIZE = 16`
- `ITERS_PER_FRAME = 8`

**主な変更点**:
- `public ComputeShader pathfindShader` → `public Material pathfindMaterial`
- 未割り当て時のフォールバック:
  - Editor: `AssetDatabase.LoadAssetAtPath<Material>("Assets/Shaders/PathfindCompeito.compeito")`
  - Runtime: `Shader.Find("Compeito/Generated/PathfindCompeito")` から Material を生成

**初期化フロー** (`Awake`):
1. グリッド生成: 外壁 + 内部壁
2. `SVOBuilder` で SVO 構築
3. `PathfindEngine.Init(pathfindMaterial, svoData)` で GPU テクスチャ初期化
4. `PathfindVisualizer` 初期化
5. デフォルトスタート/ゴール設定
6. `engine.Reset()`

## 一連の処理フロー

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 0: 初期化 (Awake)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  [CPU] グリッド生成 (外壁 + 内部壁)
         │
         ▼
  [CPU] SVOBuilder.Build()
         │  ・再帰的に8分木を構築
         │  ・空ボクセルのリーフノードを抽出
         │  ・6近傍リーフインデックスを事前計算
         ▼
  [CPU→GPU] Texture2D.SetPixelData() でアップロード
         │  ・neighbors → neighborTex (Texture2D)
         │  ・leafCosts → costTex (Texture2D)
         ▼
  [CPU] engine.Reset() → Reset カーネルを A/B 両方にディスパッチ
         │  ・全ノード: cost=INF, state=0, parent=-1
         │  ・スタートノード: cost=0, state=1(frontier)


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 1: 経路探索 (Update ループ)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  for i in 0..ITERS_PER_FRAME-1:
    ┌──────────────────────────────────────────────────┐
    │  WavefrontExpand カーネル                         │
    │                                                   │
    │  各ピクセル (id.x = リーフインデックス):         │
    │                                                   │
    │    1. _PathDataIn[id.x] を Load                 │
    │    2. state が visited(2) → そのまま出力         │
    │    3. state が frontier(1) → visited(2) に変更   │
    │    4. state が unreached(0):                     │
    │       ・_Neighbors[id.x] から6方向を Load        │
    │       ・frontier 近傍があれば:                   │
    │         bestCost = min(近傍cost) + leafCost      │
    │         bestParent = 近傍インデックス            │
    │         state = frontier(1)                      │
    │       ・frontier 近傍がなければ:                 │
    │         変更なし (unreached)                      │
    │    5. 出力 RenderTexture[id.x] に書き込み         │
    └──────────────────────────────────────────────────┘
         │  (ピンポン切り替え: A↔B)
         ▼
  ┌──────────────────────────────────────────────────┐
  │  GoalDetect カーネル                               │
  │                                                   │
  │    _PathDataIn[_GoalIndex].state >= 2 ?          │
  │      goalResultTex[0] = 1 : 0                   │
  └──────────────────────────────────────────────────┘
         │
         ▼
  [GPU→CPU] AsyncGPUReadback.Request(goalResultTex)
         │
         ▼ (次フレームで結果取得)
  [CPU] goalReached = goalResultTex[0] > 0
         ├─ 0 → 続行 (次フレームで再Phase 1)
         └─ 1 → Phase 2 へ


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 2: 経路復元 (ゴール到達時)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  [GPU→CPU] AsyncGPUReadback.Request(pathDataBuf) で全体取得
         │
         ▼
  [CPU] 経路復元:
         │  path = []
         │  current = goalIndex
         │  while current != startIndex:
         │      path.Add(current)
         │      current = pathData[current].parent
         │  path.Reverse()
         ▼
  [CPU] path → 3D座標に変換 (leafNodes[idx] → Vector3)
         │
         ▼
  [CPU] viz.UpdateVisual(pathNodes, path, startIdx, goalIdx)


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  毎フレームの処理フロー
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Update():
    if (running && !goalReached):
        engine.Iterate(8)           // 8回ディスパッチ + ゴール判定
        engine.RequestReadback()    // 非同期読み戻し要求

    if (readbackReady):
        goalReached = engine.GetGoalReached()
        pathData = engine.GetPathData()
        path = goalReached ? ReconstructPath(pathData) : null
        viz.UpdateVisual(pathData, path, startIdx, goalIdx)

    // カメラ回転・ズーム処理
    // IMGUI 描画
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
