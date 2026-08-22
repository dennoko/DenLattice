using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    internal partial class EditSession
    {
        // ------------------------------------------------------------------
        // ラティスの状態

        /// <summary>ボックス内判定とパラメータが現在の形状・ボックスと整合しているか。</summary>
        private bool _paramsValid;

        // パラメータ化を作ったときのボックス。ここが変わらない限り作り直す必要はない
        private Vector3 _paramsBoxPosition;
        private Quaternion _paramsBoxRotation = Quaternion.identity;
        private Vector3 _paramsBoxSize;

        /// <summary>前回の Refresh 時点でプロキシを取得できていなかったか。</summary>
        private bool _lastAnyFallback;

        /// <summary>制御点オフセット（ボックスローカル）。ドラッグ中の未確定値を含む。</summary>
        private Vector3[] _controlOffsets = System.Array.Empty<Vector3>();

        /// <summary>スナップショット時点の制御点オフセット。ドラッグはここからの増分になる。</summary>
        private Vector3[] _offsetsSnapshot = System.Array.Empty<Vector3>();

        /// <summary>制御点のワールド位置。オフセットが変わるたびに組み直す。</summary>
        private Vector3[] _controlWorld = System.Array.Empty<Vector3>();

        /// <summary>選択中の制御点に掛かる係数。ハンドルの変位がこの倍だけ配分される。</summary>
        private float[] _controlWeights = System.Array.Empty<float>();

        /// <summary>ミラー側の制御点に掛かる係数。反射した変位が配分される。</summary>
        private float[] _controlMirrorWeights = System.Array.Empty<float>();

        private readonly HashSet<int> _selected = new HashSet<int>();

        private bool _hasSelection => _selected.Count > 0;

        // ------------------------------------------------------------------
        // 基準空間

        /// <summary>
        /// ラティスボックスの基準空間。複数 Renderer を跨いで変形するため、
        /// コンポーネントの Transform ではなくアバタールートを使う。
        /// </summary>
        private Transform SpaceRoot
        {
            get
            {
                var avatarRoot = nadena.dev.ndmf.runtime.RuntimeUtil.FindAvatarInParents(_component.transform);
                return avatarRoot != null ? avatarRoot : _component.transform;
            }
        }

        /// <summary>
        /// ボックスローカル → ワールド。
        ///
        /// ボックス自身のスケールは行列に含めない（<c>Vector3.one</c>）。含めてしまうと
        /// 制御点オフセットがボックスの縦横比で歪み、「ボックスを伸ばしたら変形量も伸びる」
        /// という予測しづらい挙動になる。格子点の間隔は <c>boxSize</c> を直接使って作る。
        /// </summary>
        private Matrix4x4 BoxToWorld
        {
            get
            {
                var root = SpaceRoot;

                // ボックスをドラッグしている間はコンポーネントへ書かない（Undo を 1 段にするため）。
                // その間の表示は未確定値を使わないと、ハンドルだけが動いてボックスが取り残される
                var position = _boxDragging ? _pendingBoxPosition : _component.boxPosition;
                var rotation = _boxDragging ? _pendingBoxRotation : _component.boxRotation;

                var boxToRoot = Matrix4x4.TRS(position, rotation, Vector3.one);
                return root.localToWorldMatrix * boxToRoot;
            }
        }

        /// <summary>ボックスの各辺の長さ。ドラッグ中は未確定値。</summary>
        private Vector3 CurrentBoxSize => _boxDragging ? _pendingBoxSize : _component.boxSize;

        private static Vector3 Reflect(Vector3 v, LatticeAxis axis)
        {
            switch (axis)
            {
                case LatticeAxis.V: return new Vector3(v.x, -v.y, v.z);
                case LatticeAxis.W: return new Vector3(v.x, v.y, -v.z);
                default: return new Vector3(-v.x, v.y, v.z);
            }
        }

        // ------------------------------------------------------------------
        // 制御点

        private void EnsureControlArrays()
        {
            var count = _component.ControlCount;

            if (_controlOffsets.Length != count) _controlOffsets = new Vector3[count];
            if (_offsetsSnapshot.Length != count) _offsetsSnapshot = new Vector3[count];
            if (_controlWorld.Length != count) _controlWorld = new Vector3[count];
            if (_controlWeights.Length != count) _controlWeights = new float[count];
            if (_controlMirrorWeights.Length != count) _controlMirrorWeights = new float[count];
        }

        /// <summary>コンポーネントの制御点オフセットを作業用配列へ読み直す。</summary>
        private void SyncOffsetsFromComponent()
        {
            EnsureControlArrays();

            for (var i = 0; i < _controlOffsets.Length; i++)
            {
                _controlOffsets[i] = _component.GetControlOffset(i);
                _offsetsSnapshot[i] = _controlOffsets[i];
            }

            RebuildControlWorld();
        }

        private void RebuildControlWorld()
        {
            EnsureControlArrays();

            var boxToWorld = BoxToWorld;
            var size = CurrentBoxSize;
            var resU = _component.ResU;
            var resV = _component.ResV;
            var resW = _component.ResW;

            for (var k = 0; k < resW; k++)
            {
                for (var j = 0; j < resV; j++)
                {
                    for (var i = 0; i < resU; i++)
                    {
                        var index = _component.ControlIndex(i, j, k);

                        var rest = new Vector3(
                            (Fraction(i, resU) - 0.5f) * size.x,
                            (Fraction(j, resV) - 0.5f) * size.y,
                            (Fraction(k, resW) - 0.5f) * size.z);

                        // ボックスを動かすとオフセットはリセットされる（→ ApplyBoxTransform）。
                        // ドラッグ中からその結果を見せておくと、離した瞬間にケージが飛ばない
                        var offset = _boxDragging ? Vector3.zero : _controlOffsets[index];

                        _controlWorld[index] = boxToWorld.MultiplyPoint3x4(rest + offset);
                    }
                }
            }
        }

        private static float Fraction(int index, int resolution)
        {
            return resolution <= 1 ? 0.5f : index / (float)(resolution - 1);
        }

        /// <summary>
        /// ミラー相手の制御点。インデックスの反転なので相手探索が自明で、
        /// 頂点ペア方式のような許容誤差パラメータが要らない。
        /// </summary>
        private int MirrorIndexOf(int index)
        {
            var resU = _component.ResU;
            var resV = _component.ResV;
            var resW = _component.ResW;

            var i = index % resU;
            var j = index / resU % resV;
            var k = index / (resU * resV);

            switch (_component.mirrorAxis)
            {
                case LatticeAxis.V:
                    j = resV - 1 - j;
                    break;
                case LatticeAxis.W:
                    k = resW - 1 - k;
                    break;
                default:
                    i = resU - 1 - i;
                    break;
            }

            return _component.ControlIndex(i, j, k);
        }

        /// <summary>選択中の制御点の重心。移動ハンドルの基準位置になる。</summary>
        private void RecomputeCenter()
        {
            if (!_hasSelection) return;

            var sum = Vector3.zero;
            var count = 0;

            foreach (var index in _selected)
            {
                if (index < 0 || index >= _controlWorld.Length) continue;
                sum += _controlWorld[index];
                count++;
            }

            if (count == 0) return;

            _centerWorld = sum / count;
            _handlePosition = _centerWorld;
        }

        // ------------------------------------------------------------------
        // ボックス内判定とパラメータ

        private void EnsureParams()
        {
            if (_paramsValid) return;
            BuildParams();
        }

        /// <summary>
        /// 各頂点のボックス内正規化座標を求める。
        ///
        /// <b>基準にするのは「自分の変形を取り除いた形状」</b>（<c>WorldVertices - skin·delta</c>）。
        /// 変形後の位置から計算すると、変形するたびに基底の値が変わってしまい、
        /// 「制御点を元の位置へ戻しても形が戻らない」ことになる。デルタは疎なので、
        /// 引き算が要るのは実際に動かした頂点だけで済む。
        ///
        /// ボックスの外にある頂点は登録しない（＝変形しない）。外挿はしない。
        /// </summary>
        private void BuildParams()
        {
            _paramsValid = true;

            var worldToBox = BoxToWorld.inverse;
            var size = CurrentBoxSize;

            // 潰れた軸で 0 除算しない
            var inverseSize = new Vector3(
                Mathf.Abs(size.x) < 1e-6f ? 0f : 1f / size.x,
                Mathf.Abs(size.y) < 1e-6f ? 0f : 1f / size.y,
                Mathf.Abs(size.z) < 1e-6f ? 0f : 1f / size.z);

            _paramsBoxPosition = _boxDragging ? _pendingBoxPosition : _component.boxPosition;
            _paramsBoxRotation = _boxDragging ? _pendingBoxRotation : _component.boxRotation;
            _paramsBoxSize = size;

            foreach (var target in _targets)
            {
                UpdateBoneMatrices(target);
                target.InBoxIndices.Clear();
                target.InBoxParams.Clear();

                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var rest = vertices[i];

                    if (target.Working.TryGetValue(i, out var delta) && delta != Vector3.zero)
                    {
                        rest -= SkinMatrix(target, i).MultiplyVector(delta);
                    }

                    var local = worldToBox.MultiplyPoint3x4(rest);

                    var u = local.x * inverseSize.x + 0.5f;
                    if (u < 0f || u > 1f) continue;

                    var v = local.y * inverseSize.y + 0.5f;
                    if (v < 0f || v > 1f) continue;

                    var w = local.z * inverseSize.z + 0.5f;
                    if (w < 0f || w > 1f) continue;

                    target.InBoxIndices.Add(i);
                    target.InBoxParams.Add(new Vector3(u, v, w));
                }
            }
        }

        /// <summary>パラメータ化を作ったときからボックスが変わっているか。</summary>
        private bool BoxChangedSinceParams()
        {
            return _paramsBoxPosition != _component.boxPosition
                   || _paramsBoxRotation != _component.boxRotation
                   || _paramsBoxSize != _component.boxSize;
        }

        // ------------------------------------------------------------------
        // 影響の計算

        /// <summary>
        /// 選択中の制御点とミラー相手に、ハンドルの変位を配分する係数を決める。
        ///
        /// 自分自身がミラー相手になる制御点（解像度が奇数のときの中心列）は、
        /// 両側へ半分ずつ配分する。結果として変位は <c>(D + R·D) / 2</c>、つまり
        /// ミラー面に平行な成分だけになり、軸方向成分は打ち消される。
        /// 単純に両方へ 1 を入れると面内成分が 2 倍になってしまう。
        /// </summary>
        private void BuildControlWeights()
        {
            EnsureControlArrays();

            System.Array.Clear(_controlWeights, 0, _controlWeights.Length);
            System.Array.Clear(_controlMirrorWeights, 0, _controlMirrorWeights.Length);

            foreach (var index in _selected)
            {
                if (index < 0 || index >= _controlWeights.Length) continue;

                _controlWeights[index] = 1f;
            }

            if (!_component.mirror) return;

            foreach (var index in _selected)
            {
                if (index < 0 || index >= _controlWeights.Length) continue;

                var mirrored = MirrorIndexOf(index);

                if (mirrored == index)
                {
                    _controlWeights[index] = 0.5f;
                    _controlMirrorWeights[index] = 0.5f;
                    continue;
                }

                // 相手も選択されている場合は、その相手が自分の変位を既に受け取っている。
                // ここで反射分まで足すと二重に動く
                if (_selected.Contains(mirrored)) continue;

                _controlMirrorWeights[mirrored] = 1f;
            }
        }

        /// <summary>
        /// ボックス内の各頂点について、選択中の制御点から受ける基底関数の合計を求める。
        ///
        /// DenMeshEditor では「中心からの距離 + 減衰カーブ」で重みを作っていたが、
        /// ここではそれを基底関数に差し替えているだけで、以降の処理（スナップショットからの
        /// 作り直し、スキニング逆行列でのメッシュローカル化、確定、Undo）はすべて共通。
        /// </summary>
        private void BuildInfluences()
        {
            _influences.Clear();

            if (!_hasSelection)
            {
                return;
            }

            EnsureParams();
            BuildControlWeights();

            var resU = _component.ResU;
            var resV = _component.ResV;
            var resW = _component.ResW;
            var type = _component.interpolation;

            foreach (var target in _targets)
            {
                UpdateBoneMatrices(target);
                var indices = target.InBoxIndices;
                var parameters = target.InBoxParams;

                for (var n = 0; n < indices.Count; n++)
                {
                    var uvw = parameters[n];

                    var au = LatticeBasis.Compute(uvw.x, resU, type);
                    var av = LatticeBasis.Compute(uvw.y, resV, type);
                    var aw = LatticeBasis.Compute(uvw.z, resW, type);

                    var weight = 0f;
                    var mirrorWeight = 0f;

                    for (var c = 0; c < aw.Count; c++)
                    {
                        var k = Mathf.Clamp(aw.Start + c, 0, resW - 1);
                        var wc = aw[c];
                        if (wc == 0f) continue;

                        for (var b = 0; b < av.Count; b++)
                        {
                            var j = Mathf.Clamp(av.Start + b, 0, resV - 1);
                            var wb = wc * av[b];
                            if (wb == 0f) continue;

                            var rowBase = (k * resV + j) * resU;

                            for (var a = 0; a < au.Count; a++)
                            {
                                var i = Mathf.Clamp(au.Start + a, 0, resU - 1);
                                var basis = wb * au[a];
                                if (basis == 0f) continue;

                                var control = rowBase + i;
                                weight += _controlWeights[control] * basis;
                                mirrorWeight += _controlMirrorWeights[control] * basis;
                            }
                        }
                    }

                    // Catmull-Rom は負の重みを取りうるので絶対値で判定する
                    if (Mathf.Abs(weight) < 1e-6f && Mathf.Abs(mirrorWeight) < 1e-6f) continue;

                    var index = indices[n];
                    _influences.Add(new Influence
                    {
                        Target = target,
                        Index = index,
                        Weight = weight,
                        MirrorWeight = mirrorWeight,
                        InverseSkin = SafeInverse(SkinMatrix(target, index)),
                    });
                }
            }
        }

        // ------------------------------------------------------------------
        // 変位の適用

        /// <summary>
        /// ハンドルの変位に対応する、ミラー側の変位を求める。
        /// 相手の制御点だけでなく変位ベクトルも反射する。これを忘れると反対側が同じ向きに動く。
        /// </summary>
        private Vector3 MirrorDisplacement(Vector3 displacement)
        {
            var boxToWorld = BoxToWorld;
            var local = boxToWorld.inverse.MultiplyVector(displacement);
            return boxToWorld.MultiplyVector(Reflect(local, _component.mirrorAxis));
        }

        /// <summary>
        /// ハンドルの変位を制御点と頂点へ配分する。ドラッグ中は毎フレーム呼ばれる。
        /// </summary>
        private void ApplyDisplacement()
        {
            var displacement = _handlePosition - _centerWorld;
            var mirrorDisplacement = MirrorDisplacement(displacement);

            // --- 制御点（表示用。確定するまでコンポーネントには書かない） ---
            var worldToBox = BoxToWorld.inverse;
            var localDisplacement = worldToBox.MultiplyVector(displacement);
            var localMirror = worldToBox.MultiplyVector(mirrorDisplacement);

            for (var i = 0; i < _controlOffsets.Length; i++)
            {
                _controlOffsets[i] = _offsetsSnapshot[i]
                                     + localDisplacement * _controlWeights[i]
                                     + localMirror * _controlMirrorWeights[i];
            }

            RebuildControlWorld();

            // --- 頂点 ---
            // 前フレームの寄与を打ち消すため、確定済みスナップショットから作り直す
            foreach (var target in _targets)
            {
                if (target.Touched) ResetWorkingToSnapshot(target);
            }

            foreach (var influence in _influences)
            {
                var target = influence.Target;
                if (!target.Touched)
                {
                    ResetWorkingToSnapshot(target);
                    target.Touched = true;
                }

                var worldDelta = displacement * influence.Weight + mirrorDisplacement * influence.MirrorWeight;
                var localDelta = influence.InverseSkin.MultiplyVector(worldDelta);

                target.Snapshot.TryGetValue(influence.Index, out var baseDelta);
                target.Working[influence.Index] = baseDelta + localDelta;
            }

            // ドラッグ中はコンポーネントを書き換えない。毎フレーム dirty にすると
            // NDMF がその都度プレビューパイプラインを作り直してしまうため、
            // 未確定データとしてプレビューへ直接渡す。
            foreach (var target in _targets)
            {
                if (!target.Touched) continue;
                LiveEdits.Publish(target.Edit, target.Working);
            }

            SceneView.RepaintAll();
        }

        /// <summary>
        /// 現在の内容を「次のドラッグの基準」として確定する。
        /// 頂点デルタと制御点オフセットの両方を同時に進める必要がある。
        /// </summary>
        private void CommitSnapshot()
        {
            foreach (var target in _targets)
            {
                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
            }

            EnsureControlArrays();
            System.Array.Copy(_controlOffsets, _offsetsSnapshot, _controlOffsets.Length);
        }

        /// <summary>スナップショットから制御点が 1 つでも動いているか。</summary>
        private bool ControlOffsetsChanged()
        {
            for (var i = 0; i < _controlOffsets.Length; i++)
            {
                if (_controlOffsets[i] != _offsetsSnapshot[i]) return true;
            }

            return false;
        }

        private static void ResetWorkingToSnapshot(TargetState target)
        {
            CopyDeltas(target.Snapshot, target.Working);
        }

        /// <summary>
        /// デルタ辞書の内容を移す。インスタンスは作り直さず、確保済みの容量を使い回す。
        /// </summary>
        private static void CopyDeltas(Dictionary<int, Vector3> source, Dictionary<int, Vector3> destination)
        {
            destination.Clear();
            foreach (var pair in source)
            {
                destination.Add(pair.Key, pair.Value);
            }
        }

        // ------------------------------------------------------------------
        // スキニング行列

        /// <summary>
        /// 頂点 index のメッシュローカル空間 → ワールド空間のスキニング行列。
        /// M = Σ wi * (bones[i].localToWorldMatrix * bindposes[i])
        /// </summary>
        private static Matrix4x4 SkinMatrix(TargetState target, int index)
        {
            // ドラッグ中は Refresh を止めているため、ここへ来た時点でプロキシが
            // 破棄済み（パイプライン再構築）ということがありうる
            if (target.Proxy == null) return Matrix4x4.identity;

            // ボーン情報を読めなかったときは頂点位置の取得と同じ変換で代用する。
            // ここだけ localToWorldMatrix を使うと、スケールの扱いが
            // WorldVertices と食い違ってデルタがずれる
            var fallback = MeshToWorld(target);

            if (target.Skinned == null || target.Bones == null || target.BindPoses.Count == 0 ||
                index >= target.BoneWeights.Count)
            {
                return fallback;
            }

            if (target.BoneMatrices == null)
            {
                UpdateBoneMatrices(target);
                if (target.BoneMatrices == null) return fallback;
            }

            var bw = target.BoneWeights[index];
            var accumulated = new Matrix4x4();
            var total = 0f;

            total += Accumulate(ref accumulated, target, bw.boneIndex0, bw.weight0);
            total += Accumulate(ref accumulated, target, bw.boneIndex1, bw.weight1);
            total += Accumulate(ref accumulated, target, bw.boneIndex2, bw.weight2);
            total += Accumulate(ref accumulated, target, bw.boneIndex3, bw.weight3);

            if (total <= 1e-6f) return fallback;

            if (!Mathf.Approximately(total, 1f))
            {
                var scale = 1f / total;
                for (var i = 0; i < 16; i++) accumulated[i] *= scale;
            }

            return accumulated;
        }

        private static float Accumulate(ref Matrix4x4 accumulated, TargetState target, int boneIndex, float weight)
        {
            if (weight <= 0f) return 0f;
            if (target.BoneMatrices == null || boneIndex < 0 || boneIndex >= target.BoneMatrices.Length) return 0f;

            var m = target.BoneMatrices[boneIndex];
            for (var i = 0; i < 16; i++) accumulated[i] += m[i] * weight;

            return weight;
        }

        private static Matrix4x4 SafeInverse(Matrix4x4 m)
        {
            return Mathf.Abs(m.determinant) < 1e-12f ? Matrix4x4.identity : m.inverse;
        }

        // ------------------------------------------------------------------
        // 格子数・ボックスの変更

        /// <summary>
        /// 格子数を変更する。<b>メッシュの形状は変化しない。</b>
        ///
        /// 変形の実体は頂点デルタとして既にコンポーネントへ入っているので、
        /// ここで行うのは「制御点ケージを新しい解像度で作り直す」ことだけ。
        /// 制御点はボックスの格子位置へ戻り、そこから掴み直す形になる
        /// （理由は <see cref="LatticeResampler.ChangeResolution"/>）。
        ///
        /// 頂点のパラメータはボックス基準なので、格子数を変えても計算し直す必要はない。
        /// </summary>
        internal void ChangeResolution(int u, int v, int w)
        {
            if (!LatticeResampler.ChangeResolution(_component, u, v, w)) return;

            // 制御点インデックスの意味が変わるので選択は捨てる。
            // 頂点のパラメータはボックス基準なので取り直す必要はない
            ClearSelection();
            SyncOffsetsFromComponent();
            SceneView.RepaintAll();
        }

        /// <summary>制御点を格子位置へ戻す。確定済みの頂点デルタに合わせてボックスサイズを再フィットする。</summary>
        internal void ResetControlPoints()
        {
            AutoFitBox();
        }

        /// <summary>
        /// ボックスの位置・回転・サイズを差し替える。
        ///
        /// 変位場はボックスに紐づいているため、ボックスが動いたら制御点はゼロへ戻す。
        /// 頂点デルタには触れないので<b>形状は変化せず</b>、「別の部位へボックスを移して
        /// 続きを変形する」という使い方がそのまま成立する。
        /// </summary>
        private void ApplyBoxTransform(Vector3 position, Quaternion rotation, Vector3 size, string undoName)
        {
            var clamped = new Vector3(
                Mathf.Max(1e-4f, Mathf.Abs(size.x)),
                Mathf.Max(1e-4f, Mathf.Abs(size.y)),
                Mathf.Max(1e-4f, Mathf.Abs(size.z)));

            // RegisterCompleteObjectUndo は変更が無くても 1 段積む（RecordObject の差分方式と違う）。
            // 「対象に合わせる」を続けて押したときに空の段が残り、Ctrl+Z が空振りするのを防ぐ
            if (_component.boxInitialized
                && _component.boxPosition == position
                && _component.boxRotation == rotation
                && _component.boxSize == clamped
                && !_component.HasControlOffsets)
            {
                return;
            }

            BeginUndoGroup(undoName);

            _component.boxPosition = position;
            _component.boxRotation = rotation;
            _component.boxSize = clamped;
            _component.boxInitialized = true;
            _component.ClearControlOffsets();

            EndUndoGroup();

            _paramsValid = false;
            ClearSelection();
            SyncOffsetsFromComponent();
            SceneView.RepaintAll();
        }

        /// <summary>変形対象のバウンズへボックスを合わせる。</summary>
        internal void AutoFitBox()
        {
            if (_component == null) return;

            Refresh(true);

            var root = SpaceRoot;
            var worldToRoot = root.worldToLocalMatrix;

            var hasAny = false;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;

            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                foreach (var world in vertices)
                {
                    var local = worldToRoot.MultiplyPoint3x4(world);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    hasAny = true;
                }
            }

            if (!hasAny) return;

            var size = max - min;

            // 境界上の頂点が判定から漏れないよう、各軸 5%（最低 1cm）の余白を持たせる
            var margin = Vector3.Max(size * 0.05f, Vector3.one * 0.01f);
            size += margin * 2f;

            ApplyBoxTransform((min + max) * 0.5f, Quaternion.identity, size, "Dennoko Lattice Fit Box");
        }

        /// <summary>
        /// ボックスをアバターの対称面へ合わせる。回転をリセットし、X 中心を 0 に置く。
        /// 左右対称の変形をするにはボックス自体が対称でなければならない。
        /// </summary>
        internal void CenterBoxOnMirrorPlane()
        {
            if (_component == null) return;

            var position = _component.boxPosition;
            position.x = 0f;

            ApplyBoxTransform(position, Quaternion.identity, _component.boxSize,
                "Dennoko Lattice Center Box");
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// コンポーネントを書き換える前後で Undo グループを切る。
        /// RecordObject ではなく RegisterCompleteObjectUndo を使うことで、
        /// Prefab インスタンス上での膨大な PropertyModification 比較・照合（Hold on ポップアップのフリーズ）を防ぐ。
        /// </summary>
        private void BeginUndoGroup(string name)
        {
            DenLatticeUndo.BeginGroup(_component, name);
        }

        private void EndUndoGroup()
        {
            DenLatticeUndo.Apply(_component);
            DenLatticeUndo.EndGroup();
        }
    }
}
