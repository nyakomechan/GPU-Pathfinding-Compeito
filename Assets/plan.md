# SVO GPU Pathfinding — Unity C# + Compute Shader 実装プラン

## 概要

WebGL 2.0 GPGPU で実装済みの SVO 経路探索アルゴリズムを、Unity C# + Compute Shader に移植する。

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
│  - ComputeBuffer へのデータ詰め                        │
│  - ディスパッチ制御 (ピンポン)                         │
│  - AsyncGPUReadback で結果取得                        │
│  - 経路復元 (CPU側で親ポインタを逆追跡)                │
└───────────────┬──────────────────────────────────────┘
                │ ComputeBuffer.SetData / AsyncGPUReadback
┌───────────────▼──────────────────────────────────────┐
│  Compute Shader (PathfindCompute.compute)             │
│                                                       │
│  ┌──────────────────┐   ピンポン   ┌───────────────┐ │
│  │ pathDataA        │ ◄──────────►│ pathDataB      │ │
│  │ (StructuredBuf)  │             │ (RWStructured) │ │
│  └──────────────────┘             └───────────────┘ │
│                                                       │
│  カーネル:                                            │
│  1. WavefrontExpand — 各スレッド=リーフ1つ、プル型展開  │
│  2. GoalDetect      — ゴールノードのstate判定          │
│  3. Reset           — 全ノード初期化                   │
└───────────────────────────────────────────────────────┘
```

## WebGL版からの主な変更点

| 項目 | WebGL版 | Unity版 |
|------|---------|---------|
| GPU計算 | FBOピンポン + Fragment Shader | ComputeShader.Dispatch + ComputeBuffer |
| データ形式 | テクスチャにfloatエンコード | StructuredBuffer\<struct\>（直接構造体） |
| 結果読み戻し | readPixels（同期ストール） | AsyncGPUReadback（非同期） |
| 可視化 | Three.js InstancedMesh | Graphics.DrawMeshInstanced |
| UI | HTML/CSSボタン・スライダー | IMGUI (OnGUI) |
| 入力処理 | Canvas クリックイベント → Raycaster | Physics.Raycast / Plane交差 |

## ファイル構成

```
Assets/
├── Scripts/
│   ├── SVOBuilder.cs           - CPU: SVO構築 + 隣接ノード事前計算
│   ├── PathfindEngine.cs       - GPU: ComputeBuffer管理, ディスパッチ制御, ピンポン
│   ├── PathfindVisualizer.cs   - 描画: 壁/frontier/visited/pathの3D表示
│   └── PathfindDemo.cs         - MonoBehaviour: 初期化, UI, 入力, アニメーションループ
└── Shaders/
    └── PathfindCompute.compute  - HLSL: 3カーネル(WavefrontExpand, GoalDetect, Reset)
```

## Compute Shader 詳細 (`PathfindCompute.compute`)

### データ構造

```hlsl
struct PathNode {
    float cost;     // 累積コスト (INF=999999)
    int state;       // 0=unreached, 1=frontier, 2=visited
    int parent;      // 親ノードインデックス (-1=none)
    int padding;     // 16バイト境界合わせ
};

struct NeighborData {
    int neighbors[6]; // +X,-X,+Y,-Y,+Z,-Z のリーフインデックス (-1=none)
};
```

### バッファ

| バッファ | タイプ | 内容 |
|---------|--------|------|
| `neighbors` | StructuredBuffer\<NeighborData\> | リーフノード6近傍インデックス (読み取り専用) |
| `pathDataIn` | StructuredBuffer\<PathNode\> | 現在のノード状態 (読み取り) |
| `pathDataOut` | RWStructuredBuffer\<PathNode\> | 次のノード状態 (書き込み) |
| `goalResult` | RWStructuredBuffer\<int\> | [0]=ゴール到達フラグ |
| `resetParams` | RWStructuredBuffer\<int\> | [0]=startIndex, [1]=nodeCount |

### カーネル1: `WavefrontExpand`

```
[numthreads(64,1,1)]
void WavefrontExpand (uint3 id : SV_DispatchThreadID)
{
    int idx = (int)id.x;
    if (idx >= nodeCount) return;

    PathNode my = pathDataIn[idx];

    // visited → 変更なし
    if (my.state == 2) {
        pathDataOut[idx] = my;
        return;
    }

    // frontier → visited に遷移
    if (my.state == 1) {
        my.state = 2;
        pathDataOut[idx] = my;
        return;
    }

    // unreached → 6近傍のfrontierから最小costをpull
    NeighborData nbr = neighbors[idx];
    float bestCost = INF;
    int bestParent = -1;

    for (int i = 0; i < 6; i++) {
        int nIdx = nbr.neighbors[i];
        if (nIdx < 0) continue;
        PathNode nd = pathDataIn[nIdx];
        if (nd.state != 1) continue;  // frontier以外skip
        float newCost = nd.cost + 1.0;
        if (newCost < bestCost) {
            bestCost = newCost;
            bestParent = nIdx;
        }
    }

    PathNode result;
    result.cost = (bestCost < INF) ? bestCost : my.cost;
    result.state = (bestCost < INF) ? 1 : 0;  // frontier or unreached
    result.parent = (bestCost < INF) ? bestParent : -1;
    result.padding = 0;
    pathDataOut[idx] = result;
}
```

### カーネル2: `GoalDetect`

```
[numthreads(1,1,1)]
void GoalDetect (uint3 id : SV_DispatchThreadID)
{
    PathNode goal = pathDataIn[goalIndex];
    goalResult[0] = (goal.state >= 2) ? 1 : 0;
}
```

### カーネル3: `Reset`

```
[numthreads(64,1,1)]
void Reset (uint3 id : SV_DispatchThreadID)
{
    int idx = (int)id.x;
    if (idx >= nodeCount) return;

    PathNode node;
    node.cost = 999999.0;
    node.state = 0;
    node.parent = -1;
    node.padding = 0;

    pathDataOut[idx] = node;
}
```

- スタートノードのみ `cost=0, state=1(frontier)` に設定（Reset後にC#側で1要素だけ上書き）

## C# クラス詳細

### SVOBuilder.cs

WebGL版 `svo-builder.js` と同一ロジック:

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
    int gridSize;
    int leafCount;
}
```

- 隣接計算: リーフノードの3D座標から6方向の隣接ボクセルを検索、`voxelToLeaf`でリーフインデックス取得

### PathfindEngine.cs

**フィールド**:
- `ComputeShader computeShader`
- `ComputeBuffer neighborBuf` — 隣接データ (読み取り専用)
- `ComputeBuffer pathDataA`, `pathDataB` — ピンポン用
- `ComputeBuffer goalResultBuf` — ゴール到達フラグ
- `int nodeCount`, `int startIdx`, `int goalIdx`
- `bool currentReadA` — ピンポン状態

**メソッド**:
- `Init(SVOData data)`: バッファ作成・データアップロード
- `SetStart(int idx)`, `SetGoal(int idx)`: インデックス設定
- `Reset()`: Reset カーネルをディスパッチ、スタートノードをfrontierに設定
- `Iterate(int count)`: WavefrontExpand × count + GoalDetect をディスパッチ、ピンポン切り替え
- `RequestReadback()`: AsyncGPUReadback.Request() 呼び出し
- `IsReadbackReady()`: 読み戻し完了判定
- `GetGoalReached()`: goalResultBuf[0] を取得 (ゴール到達判定)
- `GetPathData()`: pathDataIn全体を取得 (経路復元用)
- `ReconstructPath(PathNode[] data)`: ゴール→スタートの親ポインタ逆追跡

**ピンポン制御**:

```
Iterate(count):
  for i in 0..count-1:
    if currentReadA:
      cs.SetBuffer(kernel, "pathDataIn", pathDataA)
      cs.SetBuffer(kernel, "pathDataOut", pathDataB)
    else:
      cs.SetBuffer(kernel, "pathDataIn", pathDataB)
      cs.SetBuffer(kernel, "pathDataOut", pathDataA)
    Dispatch(WavefrontExpand, nodeCount/64, 1, 1)
    currentReadA = !currentReadA

  // GoalDetect
  cs.SetBuffer(goalKernel, "pathDataIn", currentBuf)
  Dispatch(GoalDetect, 1, 1, 1)
```

### PathfindVisualizer.cs

**描画要素**:

| 要素 | 手法 | 色 |
|------|------|-----|
| 壁ボクセル | InstancedMesh (Box) | 半透明グレー (0x4A4A6A, α=0.5) |
| Frontier | InstancedMesh (Box, 小) | 緑 (0x40916C) |
| Visited | InstancedMesh (Box, 小) | 暗緑 (0x2D6A4F, α=0.5) |
| 経路ライン | LineRenderer | 黄色 (0xFFD166) |
| Start マーカー | Sphere Mesh | 明緑 (0x06D6A0) |
| Goal マーカー | Sphere Mesh | 赤 (0xEF476F) |
| Zスライス平面 | 透明Quad | 黄色 (α=0.08) |

- 毎フレーム `UpdateVisual(pathNodes, pathResult, startIdx, goalIdx)` 呼び出し
- frontier/visited メッシュの `count` を動的更新
- 経路が見つかったら LineRenderer でパス描画

**カメラ操作**:
- マウス右ドラッグ: 回転 (Orbit)
- マウスホイール: ズーム
- マウス中ドラッグ: パン

### PathfindDemo.cs

**定数**:
- `GRID_SIZE = 16`
- `ITERS_PER_FRAME = 8`

**初期化フロー** (`Awake`):
1. グリッド生成: 外壁 + 内部壁 (WebGL版と同じ壁配置)
2. `SVOBuilder` で SVO 構築
3. `PathfindEngine.Init(svoData)` で GPU バッファ初期化
4. `PathfindVisualizer` 初期化
5. デフォルトスタート/ゴール設定
6. `engine.Reset()`

**毎フレーム** (`Update`):
```
if (running && !goalReached):
    goalReached = engine.Iterate(ITERS_PER_FRAME)
    engine.RequestReadback()

if (readbackReady):
    pathNodes = engine.GetPathData()
    goalReached = engine.GetGoalReached()
    if (goalReached):
        path = engine.ReconstructPath(pathNodes)
    viz.UpdateVisual(pathNodes, path, startIdx, goalIdx)
```

**IMGUI** (`OnGUI`):
- [Run] / [Pause] ボタン
- [Step] ボタン (1イテレーション実行)
- [Reset] ボタン
- [Orbit] / [Place] モード切替
- Z-Slice スライダー (0〜15)
- 情報表示: グリッドサイズ, リーフ数, イテレーション数, frontier/visited数, ステータス

**マウスクリック** (Place モード時):
- カメラ位置からマウス方向へRay
- Y=zSlice+0.5 平面との交差判定
- 交差ボクセル座標 → `voxelToLeaf[idx]` でリーフインデックス
- 左クリック: Start または Goal 設定 (右クリックで切替)

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
  [CPU→GPU] ComputeBuffer.SetData() でアップロード
         │  ・neighbors (NeighborData[]) → neighborBuf
         │  ・pathDataA/B 初期状態 → pathDataA/B
         ▼
  [CPU] engine.Reset() → Reset カーネルディスパッチ
         │  ・全ノード: cost=INF, state=0, parent=-1
         │  ・スタートノード: cost=0, state=1(frontier)


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 Phase 1: 経路探索 (Update ループ)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  for i in 0..ITERS_PER_FRAME-1:
    ┌──────────────────────────────────────────────────┐
    │  WavefrontExpand カーネル                         │
    │                                                   │
    │  各スレッド (dtid = リーフインデックス):          │
    │                                                   │
    │    1. pathDataIn[dtid] を読み取り                 │
    │    2. state が visited(2) → そのまま出力          │
    │    3. state が frontier(1) → visited(2) に変更   │
    │    4. state が unreached(0):                      │
    │       ・neighbors[dtid].neighbors[6] をチェック   │
    │       ・frontier 近傍があれば:                    │
    │         bestCost = min(近傍cost) + 1              │
    │         bestParent = 近傍インデックス             │
    │         state = frontier(1)                       │
    │       ・frontier 近傍がなければ:                  │
    │         変更なし (unreached)                       │
    │    5. pathDataOut[dtid] に書き込み                │
    └──────────────────────────────────────────────────┘
         │  (ピンポン切り替え: A↔B)
         ▼
  ┌──────────────────────────────────────────────────┐
  │  GoalDetect カーネル                               │
  │                                                   │
  │    pathDataIn[goalIndex].state >= 2 ?             │
  │      goalResult[0] = 1 : goalResult[0] = 0      │
  └──────────────────────────────────────────────────┘
         │
         ▼
  [GPU→CPU] AsyncGPUReadback.Request(goalResultBuf)
         │
         ▼ (次フレームで結果取得)
  [CPU] goalReached = goalResult[0] > 0
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

WebGL版の `readPixels` (同期ストール) に対し、Unity版は `AsyncGPUReadback` を使用:

```csharp
private AsyncGPUReadbackRequest readbackRequest;

public void RequestReadback() {
    readbackRequest = AsyncGPUReadback.Request(goalResultBuf);
}

public bool IsReadbackReady() {
    return readbackRequest != null && readbackRequest.done;
}

public int GetGoalReached() {
    return readbackRequest.GetData<int>()[0];
}
```

経路復元時は `pathDataIn` バッファ全体の読み戻しも必要:

```csharp
public void RequestPathDataReadback() {
    pathDataReadbackRequest = AsyncGPUReadback.Request(currentReadBuf);
}
```

## パフォーマンス考慮事項

1. **readPixelsストール回避**: WebGL版では毎フレーム同期ストールが発生していたが、Unity版では `AsyncGPUReadback` で完全に非同期化
2. **1フレーム遅延**: 読み戻し結果は次フレームで反映されるため、表示が1フレーム遅れるがリアルタイム用途では問題なし
3. **Dispatch回数**: 1フレームあたり 8×WavefrontExpand + 1×GoalDetect = 9ディスパッチ (WebGL版は 8×3drawcalls + 1readPixels)
4. **ComputeBufferサイズ**: 16³ ≒ 3000リーフ × 16byte(PathNode) = 48KB → GPU VRAM的には問題なし
5. **InstancedMesh更新**: 毎フレーム frontier/visited のインスタンス行列を更新 → `SetData` で動的更新

## 設定パラメータ

| パラメータ | 値 | 備考 |
|-----------|-----|------|
| GRID_SIZE | 16 | 16×16×16 ボクセルグリッド |
| ITERS_PER_FRAME | 8 | 1フレームあたりのWavefront展開回数 |
| KERNEL_WAVEFRONT | 64 threads/group | ComputeShaderスレッドグループサイズ |
| INF | 999999.0 | 到達不能コストの初期値 |