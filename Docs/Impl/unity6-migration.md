# DenLattice — Unity 6 移行調査

- 調査日: 2026-09-06
- 現行: Unity 2022.3.22f1 / Built-in RP
- 目標: Unity 6 (6000.0 LTS) / **BiRP 維持**
- 共通調査: [`unity6-migration-overview.md`](unity6-migration-overview.md)

## 判定

⛔ **外部依存あり** — Unity 6 非対応の API は **0 件**。修正すべきコードはない。
選択アウトライン抑制の廃止により、UnityEditor 内部 API へのリフレクションも **0 件**。

## 構成

| 項目 | 内容 |
|---|---|
| 規模 | C# 27 ファイル / 約 6,562 行 |
| asmdef | `dennokoworks.DenLattice.Editor`（→ Runtime, `nadena.dev.ndmf`, `nadena.dev.ndmf.runtime`）<br>`dennokoworks.DenLattice.Runtime`（→ `VRC.SDKBase`） |
| エントリ | `MenuItem("GameObject/dennokoworks/Dennoko Lattice")` |
| UI | カスタムインスペクタ（IMGUI）+ SceneView 編集セッション |
| 外部依存 | **NDMF**（asmdef 参照）、**VRChat SDK**（`VRC.SDKBase.IEditorOnly`） |

`versionDefines` により `DEN_LATTICE_VRCSDK` が定義される設計で、
SDK 未導入環境でもコンパイルが通るよう配慮されている。

## 検出事項

### 1. `UnityEditor.AnnotationUtility` へのリフレクション（✅ 解消済み）

v1.0.2 まで選択アウトラインを internal API で自動抑制していたが、機能ごと廃止した。
Unity 全体の永続設定を書き換え、エディタが落ちると OFF のまま残るなどの副作用が理由。
選択ワイヤーフレームを `EditorUtility.SetSelectedRenderState` で隠す処理も撤去した。
必要に応じて Scene ビューの Gizmos メニューで Selection Outline / Selection Wire を切り替える。

`SelectionOutline.cs` はコメントのみとし、ファイルと GUID を保持する。
上書きインポートで旧実装を置き換えるため、パッケージのエクスポート対象から外さない。
旧版の EditorPrefs 退避キーは読み出す実装がなく無害。旧版でアウトラインが消えたままなら、
Gizmos メニューから手動で ON に戻す。DennokoMeshEditor の `c828b9d` と同じ方針。

### 2. `Handles.matrix` によるローカル座標描画（✅ 影響なし）

`Editor/Session/EditSession.Rendering.cs:65-94`

```csharp
var previousMatrix = Handles.matrix;
...
Handles.matrix = BoxToWorld;
...
Handles.matrix = previousMatrix;   // 復元
```

`Handles.matrix` の退避／復元が対称。Unity 6 で `Handles` API に変更はない。**修正不要。**

### 3. `SceneView.duringSceneGui`（✅ 影響なし）

`Editor/Session/EditSession.cs:108,129` — 登録／解除が対称。Unity 6 で API 変更なし。

### 4. `VRC.SDKBase.IEditorOnly`（⛔ 外部依存）

`Runtime/DenLattice.cs:22`

```csharp
, VRC.SDKBase.IEditorOnly
```

- ラティスコンポーネントをアップロード時にランタイムから除去するためのマーカー。
- asmdef の `versionDefines` で `DEN_LATTICE_VRCSDK` が定義される構成のため、
  SDK 未導入環境でもコンパイル可能。
- VRChat SDK は Unity 6 対応済みを前提とする。事前作業は不要。

### 5. NDMF 参照（⛔ 外部依存）

asmdef が `nadena.dev.ndmf` / `nadena.dev.ndmf.runtime` を参照。
**NDMF の内部 API へのリフレクションは本ツールには存在しない**
（BoxWeightTransfer / DenEmo / HairChimeraTool にはある）ため、
NDMF の公開 API が維持される限り追従コストは低い。

### 6. バージョンチェッカー（✅ 影響なし）

`Editor/Version/DenLatticeVersion.cs:167`

```csharp
var inspectors = Resources.FindObjectsOfTypeAll<DenLatticeInspector>();
```

`Resources.FindObjectsOfTypeAll<T>()` は **Obsolete ではない**（共通調査 2.2 節の注記参照）。
`Object.FindObjectsOfType` と混同して置換しないこと。**修正不要。**

`Editor/Version/DennokoVersionChecker.cs:136` の `#if UNITY_2020_2_OR_NEWER` は
Unity 6 でも true。旧分岐が死にコードになるだけ。**修正不要。**

## 非該当の確認

| 確認項目 | 結果 |
|---|---|
| `Object.FindObjectsOfType` / `FindObjectOfType` | **なし**（`Resources.FindObjectsOfTypeAll` のみ = Obsolete 対象外） |
| `GraphicsFormat.DepthAuto` / `ShadowAuto` / `VideoAuto` | **なし** |
| UI Toolkit（`ExecuteDefaultAction` / `UxmlFactory` 等） | **なし**（IMGUI のみ） |
| IMGUI テーマ（共有 `EditorStyles` 書き換え） | **なし** |
| Compute シェーダ / カスタムシェーダ | **なし** |
| `Lightmapping` / 物理 API | **なし** |
| NDMF 内部 API へのリフレクション | **なし** |

## 移行手順

### フェーズ 1（Unity 2022.3.22f1 のまま実施可）

- [x] 選択アウトライン／選択ワイヤーフレーム抑制と内部 API 依存を撤去

### フェーズ 3（外部依存の Unity 6 対応後）

1. VRChat SDK（Unity 6 対応済み）を導入
2. NDMF の Unity 6 対応版を導入
3. 下記チェックリストで動作確認

> **先行検証について**: `VRC.SDKBase` は Runtime asmdef のみが参照しており、
> `versionDefines` でガードされている。SDK 未導入の Unity 6 環境でも
> コンパイルは通るため、**ラティス編集機能そのものの先行検証は可能**。

## 検証チェックリスト（Unity 6）

### SDK/NDMF なしで確認できる項目（先行検証）

- [ ] コンパイルエラー・警告が 0 件
- [ ] `GameObject/dennokoworks/Dennoko Lattice` からラティスを追加できる
- [ ] カスタムインスペクタが正しく描画される
- [ ] 編集開始・終了で Gizmos の Selection Outline / Selection Wire 設定が変わらない
- [ ] ラティスの格子点を SceneView 上でドラッグして変形できる
- [ ] `Handles.matrix` によるローカル座標での格子描画が正しい位置に出る
- [ ] Undo / Redo が正しく動作する

### SDK/NDMF 対応版で確認する項目

- [ ] `IEditorOnly` によりアップロード時にコンポーネントが除去される
- [ ] NDMF ビルドパイプラインでラティス変形が適用される
- [ ] アバターのアップロードが成功する
