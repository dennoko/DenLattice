# DenLattice — Unity 6 移行調査

- 調査日: 2026-09-06
- 現行: Unity 2022.3.22f1 / Built-in RP
- 目標: Unity 6 (6000.0 LTS) / **BiRP 維持**
- 共通調査: [`unity6-migration-overview.md`](unity6-migration-overview.md)

## 判定

🔍 **要検証** ＋ ⛔ **外部依存あり** — Unity 6 非対応の API は **0 件**。修正すべきコードはない。
UnityEditor 内部 API へのリフレクションが 1 箇所あり、Unity 6 上での実動作確認が必要。

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

### 1. `UnityEditor.AnnotationUtility` へのリフレクション（🔍 要検証・本ツール最大のリスク）

`Editor/Session/SelectionOutline.cs:124-145`

```csharp
var type = typeof(EditorUtility).Assembly.GetType("UnityEditor.AnnotationUtility");
...
var property = type.GetProperty("showSelectionOutline",
                   BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
```

- `UnityEditor.AnnotationUtility.showSelectionOutline` は **Unity 本体の internal API**。
  ラティス編集中に SceneView の選択アウトライン（オレンジ枠）を一時的に抑制するために使用。
- Unity 6 で改名・移動・削除されてもコンパイルエラーにならず、静かに解決失敗する。
- 実装は非常に防御的:
  - `typeof(EditorUtility).Assembly` を優先して探し、見つからなければ全アセンブリを走査（`:138-145`）
  - プロパティ型が `bool` で読み書き可能であることまで検証（`:128-133`）
  - **失敗をキャッシュしない**（ソース内コメント `:112-115` に理由が明記されている）
    — ドメインリロード直後の未解決状態を「見つからない」と覚え込まないための配慮

**Unity 6 で起きること**: 例外は出ず、**編集中に選択アウトラインが出たままになる**だけ。
機能は失われるが、ラティス変形そのものは動作する。気付きにくい見た目の劣化。

**対応**

1. Unity 6 上でラティス編集を開始し、選択アウトラインが消えるか目視確認する。
2. 解決失敗時に一度だけ警告ログを出す（ただし本実装は失敗をキャッシュしない設計なので、
   毎フレーム出力しないようフラグ管理に注意する）。

> 同一実装が `DennokoMeshEditor/Editor/Session/SelectionOutline.cs` にも存在する。
> 片方を修正したらもう片方も同じ修正を入れること。

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

- [ ] `SelectionOutline.ResolveProperty()` の解決失敗時に警告ログを追加
      （毎フレーム出力しないようフラグ管理する。失敗をキャッシュしない設計は維持すること）
- [ ] 同じ修正を `DennokoMeshEditor/Editor/Session/SelectionOutline.cs` にも適用

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
- [ ] **編集開始時に SceneView の選択アウトラインが消える**（`AnnotationUtility` リフレクションの確認）
- [ ] 編集終了時に選択アウトラインが元に戻る（設定の復元）
- [ ] ラティスの格子点を SceneView 上でドラッグして変形できる
- [ ] `Handles.matrix` によるローカル座標での格子描画が正しい位置に出る
- [ ] Undo / Redo が正しく動作する

### SDK/NDMF 対応版で確認する項目

- [ ] `IEditorOnly` によりアップロード時にコンポーネントが除去される
- [ ] NDMF ビルドパイプラインでラティス変形が適用される
- [ ] アバターのアップロードが成功する
