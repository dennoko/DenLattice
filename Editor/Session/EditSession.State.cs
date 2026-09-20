using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenLattice.Editor
{
    internal partial class EditSession
    {
        private sealed class TargetState
        {
            public Renderer Original;
            public Renderer Proxy;
            public SkinnedMeshRenderer Skinned;
            public Mesh Mesh;

            /// <summary>
            /// 最後に読めたメッシュの頂点数。
            ///
            /// <see cref="Mesh"/> はプレビューパイプラインが所有するインスタンスで、
            /// パイプラインが作り直されると破棄される（＝ Unity の null 比較で null になる）。
            /// ドラッグ中は <see cref="Refresh"/> を止めているため、確定時には
            /// 破棄済みの参照しか残っていないことがある。頂点数だけは控えておく。
            /// </summary>
            public int VertexCount;

            public Mesh BakeScratch;
            public Vector3[] WorldVertices;
            public MeshEdit Edit;

            /// <summary>BakeMesh / Mesh から頂点を読み出すための使い回しバッファ。</summary>
            public readonly List<Vector3> LocalVertices = new List<Vector3>();

            public Transform[] Bones;
            public readonly List<Matrix4x4> BindPoses = new List<Matrix4x4>();
            public readonly List<BoneWeight> BoneWeights = new List<BoneWeight>();

            /// <summary>
            /// ボーンごとの (bone.localToWorldMatrix * bindPose) の事前計算キャッシュ。
            /// 頂点ごとの重複計算を防ぐ。
            /// </summary>
            public Matrix4x4[] BoneMatrices;

            /// <summary>
            /// ラティスボックスの内側にある頂点と、そのボックス内正規化座標。
            ///
            /// ボックスの外の頂点は変形しないので、影響計算はここに載っているものだけを見る。
            /// パラメータはボックスか対象が変わるまで固定される（→ <see cref="BuildParams"/>）。
            /// </summary>
            public readonly List<int> InBoxIndices = new List<int>();

            public readonly List<Vector3> InBoxParams = new List<Vector3>();

            // Undo やドラッグのたびに作り直さず、中身だけ入れ替えて使う。
            // Working は LiveEdits へ参照のまま渡してあるので、インスタンスを差し替えない
            // ことが「公開済みの参照が古くならない」ことの保証にもなっている
            public readonly Dictionary<int, Vector3> Working = new Dictionary<int, Vector3>();
            public readonly Dictionary<int, Vector3> Snapshot = new Dictionary<int, Vector3>();
            public bool Touched;
        }

        /// <summary>1 頂点に対する、選択中の制御点群からの影響。</summary>
        private struct Influence
        {
            public TargetState Target;
            public int Index;

            /// <summary>選択中の制御点の基底関数の合計。</summary>
            public float Weight;

            /// <summary>ミラー側の制御点の基底関数の合計。</summary>
            public float MirrorWeight;

            public Matrix4x4 InverseSkin;
        }

        private readonly List<TargetState> _targets = new List<TargetState>();

        /// <summary>
        /// <see cref="SyncTargetList"/> が使い回す作業リスト。
        /// Refresh は 0.1 秒ごとに走るので、毎回 List を確保しない。
        /// </summary>
        private readonly List<MeshEdit> _wantedEdits = new List<MeshEdit>();

        private readonly List<Influence> _influences = new List<Influence>();

        private void DisposeTargets()
        {
            foreach (var target in _targets)
            {
                if (target.BakeScratch != null) Object.DestroyImmediate(target.BakeScratch);
            }

            _targets.Clear();
            _paramsValid = false;
        }

        // ------------------------------------------------------------------
        // プロキシからの頂点位置取得

        private void Refresh(bool force, bool allowCachedVertices = false)
        {
            if (!force && EditorApplication.timeSinceStartup - _lastRefresh < RefreshIntervalSeconds) return;
            _lastRefresh = EditorApplication.timeSinceStartup;

            if (_component == null) return;

            if (SyncTargetList()) _paramsValid = false;

            var anyFallback = false;
            var anyVertexCountMismatch = false;

            foreach (var target in _targets)
            {
                var proxy = ProxyRegistry.ResolveOrOriginal(target.Original, out var usingProxy);

                // 下流の NDMF フィルタが頂点数を変えている場合、プロキシから読んだ頂点位置と
                // 元メッシュのインデックスで保存するデルタの対応が取れない。
                // そのまま編集させると無関係な頂点が動くので、プロキシを捨てて元 Renderer へ退避する。
                if (usingProxy && DownstreamGuard.HasVertexCountMismatch(target.Original))
                {
                    proxy = target.Original;
                    usingProxy = false;
                    anyVertexCountMismatch = true;
                }

                if (!usingProxy) anyFallback = true;

                if (target.Proxy != proxy)
                {
                    target.Proxy = proxy;
                    target.Skinned = proxy as SkinnedMeshRenderer;
                }

                // プレビュー用メッシュはパイプライン再構築のたびに作り直されるため、
                // プロキシが同じでもメッシュのインスタンスは変わりうる。毎回読み直す。
                var mesh = MeshDeltaApplier.GetSharedMesh(proxy);

                // 確定時にメッシュが破棄済みでも頂点数を書けるよう、読めたときに控えておく
                if (mesh != null) target.VertexCount = mesh.vertexCount;

                var meshChanged = target.Mesh != mesh;
                if (meshChanged)
                {
                    target.Mesh = mesh;

                    // List 版の取得 API を使い、パイプライン再構築のたびに
                    // ボーンウェイト（頂点数 × 32 バイト）を確保し直さないようにする
                    if (mesh != null)
                    {
                        mesh.GetBindposes(target.BindPoses);
                        mesh.GetBoneWeights(target.BoneWeights);
                    }
                    else
                    {
                        target.BindPoses.Clear();
                        target.BoneWeights.Clear();
                    }
                }

                // Scale Adjuster などがシャドウボーンを差し替えることがあるので毎回読み直す
                target.Bones = target.Skinned != null ? target.Skinned.bones : null;
                target.BoneMatrices = null;

                // ボックス内判定を取り直すかどうか。
                //
                // 頂点数が変わった場合（＝別のメッシュになった）は無条件に取り直す。
                // 位置だけが変わった場合は、制御点がまだ格子上にあるときだけ取り直す。
                // ケージを動かした後にパラメータを作り直すと基底の値が変わり、
                // 「制御点を元の位置へ戻しても形が戻らない」ことになるため
                //（→ BuildParams）。上流ツールのスライダー操作などには、
                // ケージが素の状態のうちに追従しておく。
                var canUseCache = allowCachedVertices && !meshChanged;
                var moved = UpdateWorldVertices(target, out var resized, canUseCache);
                if (resized || (moved && !_component.HasControlOffsets)) _paramsValid = false;
            }

            AnyFallback = anyFallback;
            AnyVertexCountMismatch = anyVertexCountMismatch;

            // プロキシが後から利用可能になると形状が一段変わる。
            // ボックス内判定はその形状を基準にするので、切り替わった時点で取り直す
            if (_lastAnyFallback && !anyFallback) _paramsValid = false;
            _lastAnyFallback = anyFallback;

            // 編集開始直後やパイプライン再構築の数フレームは、正常でも一時的に
            // フォールバック状態になる。状態が続いたときにだけ警告する（明滅させない）
            if (!anyFallback)
            {
                _fallbackSince = -1;
            }
            else if (_fallbackSince < 0)
            {
                _fallbackSince = EditorApplication.timeSinceStartup;
            }

            _showFallbackWarning = _fallbackSince >= 0
                                   && EditorApplication.timeSinceStartup - _fallbackSince
                                   > FallbackWarningDelaySeconds;
        }

        /// <summary>
        /// 変形データに添える頂点数を決める。取得できなければ 0。
        ///
        /// プレビュー用メッシュはパイプライン再構築のたびに破棄されるため、
        /// <see cref="TargetState.Mesh"/> をそのまま参照すると確定時に null になっていることがある。
        /// 控えておいた頂点数、それも無ければ元 Renderer のメッシュへ順に退避する。
        /// </summary>
        private static int ResolveVertexCount(TargetState target)
        {
            if (target.Mesh != null) return target.Mesh.vertexCount;
            if (target.VertexCount > 0) return target.VertexCount;

            var mesh = MeshDeltaApplier.GetSharedMesh(target.Original);
            return mesh != null ? mesh.vertexCount : 0;
        }

        /// <summary>対象リストを作り直したら true。</summary>
        private bool SyncTargetList()
        {
            var wanted = _wantedEdits;
            wanted.Clear();

            foreach (var edit in _component.edits)
            {
                if (edit?.target == null) continue;
                if (MeshDeltaApplier.GetSharedMesh(edit.target) == null) continue;
                wanted.Add(edit);
            }

            // 作り直すかどうかは Renderer の顔ぶれだけで決める。
            //
            // Undo / Redo・Prefab の巻き戻し・ドメインリロードでは、対象が何も変わっていなくても
            // MeshEdit がデシリアライズされて別インスタンスになりうる。ここで参照の同一性まで
            // 求めると、そのたびに TargetState が全破棄され、ボーンウェイトの取り直しや
            // BakeScratch の再確保、ボックス内判定の作り直しが発生する。
            if (_targets.Count == wanted.Count)
            {
                var same = true;
                for (var i = 0; i < wanted.Count; i++)
                {
                    if (_targets[i].Original == wanted[i].target) continue;
                    same = false;
                    break;
                }

                if (same)
                {
                    RebindEdits(wanted);
                    return false;
                }
            }

            DisposeTargets();
            ClearSelection();

            foreach (var edit in wanted)
            {
                var state = new TargetState
                {
                    Original = edit.target,
                    Edit = edit,
                };

                edit.CopyTo(state.Working);
                CopyDeltas(state.Working, state.Snapshot);
                _targets.Add(state);
            }

            return true;
        }

        /// <summary>
        /// <see cref="MeshEdit"/> のインスタンスだけが差し替わった場合に、
        /// <see cref="TargetState"/> を保ったまま参照と作業状態を貼り替える。
        /// </summary>
        private void RebindEdits(List<MeshEdit> wanted)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                var target = _targets[i];
                var edit = wanted[i];
                if (ReferenceEquals(target.Edit, edit)) continue;

                // 古いインスタンス宛の未確定データはもう辿れない。
                // 巻き戻った内容を作業状態の基準として取り直す
                target.Edit = edit;
                edit.CopyTo(target.Working);
                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
            }
        }

        /// <summary>
        /// 頂点の取得元の空間 → ワールド空間の変換行列。
        ///
        /// スキンドの場合は <c>BakeMesh</c> の出力を使うが、その頂点は
        /// 「Renderer の Transform のスケールを一切考慮しない」空間に入っている
        /// （既定の <c>useScale: false</c> の挙動）。そのため <c>localToWorldMatrix</c> を
        /// 掛けるとスケールが二重に効いてしまう。
        ///
        /// スキンドでない場合の頂点はメッシュローカルなので、スケールを含む行列が正しい。
        /// </summary>
        private static Matrix4x4 MeshToWorld(TargetState target)
        {
            var transform = target.Proxy.transform;

            return target.Skinned != null
                ? Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one)
                : transform.localToWorldMatrix;
        }

        /// <summary>
        /// プロキシからワールド空間の頂点位置を取り直す。位置が実際に変わったら true。
        /// </summary>
        /// <param name="resized">頂点数そのものが変わった場合に true（＝別のメッシュになった）。</param>
        /// <param name="allowCached">頂点配列が既に存在し頂点数が一致していれば BakeMesh をスキップする。</param>
        private static bool UpdateWorldVertices(TargetState target, out bool resized, bool allowCached = false)
        {
            resized = false;
            if (target.Proxy == null || target.Mesh == null) return false;

            var vertexCount = target.Mesh.vertexCount;
            if (allowCached && target.WorldVertices != null && target.WorldVertices.Length == vertexCount)
            {
                return false;
            }

            var localToWorld = MeshToWorld(target);
            var source = target.LocalVertices;

            if (target.Skinned != null)
            {
                if (target.BakeScratch == null)
                {
                    target.BakeScratch = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }

                // プロキシの bones（Scale Adjuster のシャドウボーンを含む）を用いてスキニング結果を得る
                target.Skinned.BakeMesh(target.BakeScratch);

                // Mesh.vertices は呼び出しごとに Vector3[] を確保する。
                // 0.1 秒ごとに走る経路なので List 版を使い回す
                target.BakeScratch.GetVertices(source);
            }
            else
            {
                target.Mesh.GetVertices(source);
            }

            var count = source.Count;
            resized = target.WorldVertices == null || target.WorldVertices.Length != count;
            if (resized) target.WorldVertices = new Vector3[count];

            var changed = resized;
            var world = target.WorldVertices;

            for (var i = 0; i < count; i++)
            {
                var position = localToWorld.MultiplyPoint3x4(source[i]);

                // Vector3 の == は近似比較。微小な数値誤差で作り直しを誘発しないので都合がよい
                if (!changed && position != world[i]) changed = true;
                world[i] = position;
            }

            return changed;
        }

        /// <summary>
        /// ボーンごとの (bone.localToWorldMatrix * bindPose) を一括事前計算する。
        /// 頂点ごとに繰り返し計算するのを防ぎ、O(ボーン数) に抑える。
        /// </summary>
        private static void UpdateBoneMatrices(TargetState target)
        {
            if (target.Bones == null || target.BindPoses.Count == 0)
            {
                target.BoneMatrices = null;
                return;
            }

            var count = Mathf.Min(target.Bones.Length, target.BindPoses.Count);
            if (target.BoneMatrices == null || target.BoneMatrices.Length != count)
            {
                target.BoneMatrices = new Matrix4x4[count];
            }

            for (var i = 0; i < count; i++)
            {
                var bone = target.Bones[i];
                target.BoneMatrices[i] = bone != null
                    ? bone.localToWorldMatrix * target.BindPoses[i]
                    : Matrix4x4.identity;
            }
        }
    }
}
